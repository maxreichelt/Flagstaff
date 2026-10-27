using Flagstaff.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Flagstaff.Cli;

/// <summary>
/// Fills <see cref="UserDeclarations"/> from the project's hand-written C# files: a syntax-only Roslyn parse (no
/// compilation, no references) of every <c>.cs</c> file under the project directory outside <c>bin/</c>, <c>obj/</c>
/// and the generated output, recording the members of each top-level <c>partial class</c> by full name. The compiler
/// looks up only the types it generates (<c>{ns}.ViewModels.{Vm}</c>, <c>{ns}.{View}</c>, <c>{ns}.ViewModels.{Item}</c>).
/// </summary>
internal static class UserDeclarationScanner
{
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", ProjectInfo.DefaultOutputDir };

    /// <summary>Upper bound on files read, so a scan rooted at a large directory without a project stays cheap.</summary>
    private const int MaxFiles = 2000;

    /// <summary>
    /// Scans the directory the hand-written half lives in: the project directory when there is a <c>.csproj</c>, else
    /// the parent of each <c>Flags/</c> input root (or the root itself). <paramref name="excludeDirectory"/> (the output
    /// directory) is skipped too.
    /// </summary>
    public static UserDeclarations Scan(CliContext ctx, ProjectInfo project, IEnumerable<string> inputRoots, string? excludeDirectory = null)
    {
        var baseDirectory = project.ProjectFile is not null ? project.Directory : null;
        var directories = baseDirectory is not null
            ? [baseDirectory]
            : inputRoots.Select(ScanRootFor).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var user = new UserDeclarations();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var file in Enumerate(directory, excludeDirectory))
            {
                if (!seen.Add(file) || seen.Count > MaxFiles)
                    continue;
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                if (!text.Contains("partial", StringComparison.Ordinal))
                    continue;
                var relative = Path.GetRelativePath(baseDirectory ?? directory, file).Replace('\\', '/');
                ScanText(text, ctx.Display(file), relative, user);
            }
        }
        return user;
    }

    private static string? ScanRootFor(string root)
    {
        var dir = Directory.Exists(root) ? root : Path.GetDirectoryName(root);
        if (dir is null)
            return null;
        return Path.GetFileName(dir.TrimEnd('\\', '/')).Equals(ProjectInfo.DefaultInputDir, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir.TrimEnd('\\', '/'))
            : dir;
    }

    private static IEnumerable<string> Enumerate(string directory, string? exclude)
    {
        if (!Directory.Exists(directory))
            yield break;
        var excluded = exclude is null ? null : Path.GetFullPath(exclude).TrimEnd('\\', '/');
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> files, subdirectories;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.cs").Order(StringComparer.Ordinal).ToList();
                subdirectories = Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal).Reverse().ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (!file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }
            foreach (var sub in subdirectories)
            {
                var name = Path.GetFileName(sub);
                if (SkippedDirectories.Contains(name) || name.StartsWith('.'))
                    continue;
                if (excluded is not null && string.Equals(Path.GetFullPath(sub).TrimEnd('\\', '/'), excluded, StringComparison.OrdinalIgnoreCase))
                    continue;
                pending.Push(sub);
            }
        }
    }

    /// <summary>Records the members of every top-level partial class in <paramref name="text"/>.</summary>
    /// <param name="displayFile">The file as diagnostics show it.</param>
    /// <param name="projectRelativeFile">The file relative to the project, for messages and generated headers.</param>
    public static void ScanText(string text, string displayFile, string projectRelativeFile, UserDeclarations user)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        foreach (var type in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<ClassDeclarationSyntax>())
        {
            if (!type.Modifiers.Any(SyntaxKind.PartialKeyword))
                continue;
            var fullName = NamespaceOf(type) is { Length: > 0 } ns ? $"{ns}.{type.Identifier.ValueText}" : type.Identifier.ValueText;
            user.AddType(fullName);
            foreach (var member in type.Members)
            {
                foreach (var m in Members(member, displayFile, projectRelativeFile))
                    user.Add(fullName, m);
            }
        }
    }

    private static string NamespaceOf(SyntaxNode node)
    {
        var parts = new List<string>();
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is BaseNamespaceDeclarationSyntax ns)
                parts.Insert(0, ns.Name.ToString());
        }
        return string.Join(".", parts);
    }

    private static IEnumerable<UserMember> Members(MemberDeclarationSyntax member, string displayFile, string file)
    {
        UserMember Make(UserMemberKind kind, SyntaxToken identifier, string? type, IReadOnlyList<UserParameter>? parameters = null,
            bool hasBody = false, bool callsInitialize = false)
        {
            var span = identifier.GetLocation().GetLineSpan().StartLinePosition;
            return new UserMember(kind, identifier.ValueText, new SourceLocation(displayFile, span.Line + 1, span.Character + 1), file,
                type, Accessibility(member.Modifiers), member.Modifiers.Any(SyntaxKind.PartialKeyword), hasBody,
                Attributes(member.AttributeLists), parameters, callsInitialize);
        }

        switch (member)
        {
            case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                foreach (var variable in field.Declaration.Variables)
                    yield return Make(UserMemberKind.Field, variable.Identifier, field.Declaration.Type.ToString());
                break;
            case PropertyDeclarationSyntax property:
                yield return Make(UserMemberKind.Property, property.Identifier, property.Type.ToString());
                break;
            case MethodDeclarationSyntax method:
                yield return Make(UserMemberKind.Method, method.Identifier, method.ReturnType.ToString(), Parameters(method.ParameterList),
                    method.Body is not null || method.ExpressionBody is not null);
                break;
            case ConstructorDeclarationSyntax ctor when !ctor.Modifiers.Any(SyntaxKind.StaticKeyword):
                yield return Make(UserMemberKind.Constructor, ctor.Identifier, null, Parameters(ctor.ParameterList),
                    ctor.Body is not null || ctor.ExpressionBody is not null, CallsInitializeComponent(ctor));
                break;
        }
    }

    private static List<UserParameter> Parameters(ParameterListSyntax list) =>
        list.Parameters.Select(p => new UserParameter(p.Type?.ToString() ?? "", p.Identifier.ValueText)).ToList();

    private static string Accessibility(SyntaxTokenList modifiers) =>
        string.Join(" ", modifiers.Where(m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
                                                  or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword)
                                  .Select(m => m.ValueText));

    private static List<string> Attributes(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(l => l.Attributes)
            .Select(a => a.Name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax q => q.Name.Identifier.ValueText,
                SimpleNameSyntax s => s.Identifier.ValueText,
                _ => a.Name.ToString(),
            })
            .Select(n => n.EndsWith("Attribute", StringComparison.Ordinal) && n.Length > "Attribute".Length ? n[..^"Attribute".Length] : n)
            .ToList();

    private static bool CallsInitializeComponent(ConstructorDeclarationSyntax ctor) =>
        ctor.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == "InitializeComponent",
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma => ma.Name.Identifier.ValueText == "InitializeComponent",
            _ => false,
        });
}
