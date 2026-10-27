using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Cli.Commands;

/// <summary>One partial-method delegate a generated ViewModel expects: <c>OnSaveAsync()</c> / <c>OnEditAsync(Order item)</c>.</summary>
internal sealed record StubMethod(string Name, string? ParameterType)
{
    public string Signature => ParameterType is null
        ? $"private partial Task {Name}()"
        : $"private partial Task {Name}({ParameterType} item)";

    public string StubLine => $"{Signature} => Task.CompletedTask; // TODO";
}

/// <summary>
/// <c>stubs [paths...] [--dir ViewModels] [--namespace ns] [--update]</c>: creates the hand-written ViewModel
/// partials (one per <c>bind vm:</c> type) with a <c>Task.CompletedTask</c> body for every command delegate.
/// Never overwrites; <c>--update</c> appends missing methods to existing files.
/// </summary>
internal static class StubsCommand
{
    public static Command Create(CliHost host)
    {
        var paths = new Argument<string[]>("paths")
        {
            Description = ".flag files or directories (default: ./Flags if it exists, else .)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var dir = new Option<string>("--dir") { Description = "Directory for the hand-written partials", DefaultValueFactory = _ => "ViewModels" };
        var ns = new Option<string>("--namespace", "-n") { Description = "Root namespace (partials go in <ns>.ViewModels)" };
        var update = new Option<bool>("--update") { Description = "Append missing methods to existing partials (existing members are never touched)" };

        var cmd = new Command("stubs", "Create ViewModel partials with stub implementations of the generated command delegates (On{X}Async)");
        cmd.Arguments.Add(paths);
        cmd.Options.Add(dir);
        cmd.Options.Add(ns);
        cmd.Options.Add(update);
        host.Bind(cmd, (pr, ctx) => Run(ctx, pr.GetValue(paths), pr.GetValue(dir)!, pr.GetValue(ns), pr.GetValue(update)));
        return cmd;
    }

    public static CommandResult Run(CliContext ctx, IReadOnlyList<string>? paths, string dir, string? ns, bool update)
    {
        var result = new CommandResult("stubs");
        // stubs writes files: never fall back to scanning the whole working directory (that would pick up
        // unrelated .flag files such as test fixtures). Require a Flags/ directory or explicit paths.
        if ((paths is null || paths.Count == 0) && !Directory.Exists(ctx.Resolve(ProjectInfo.DefaultInputDir)))
            return CommandResult.Usage("stubs", "no Flags/ directory here; run stubs from the project directory or pass the .flag files or directories explicitly");
        var (files, roots, error) = FlagSources.Resolve(ctx, paths);
        if (error is not null)
            return CommandResult.Usage("stubs", error);

        var sources = FlagSources.Load(ctx, files);
        var project = ProjectInfo.Discover(ctx.WorkingDirectory, roots);
        var rootNamespace = project.ResolveNamespace(ns, sources);
        var diagnostics = new List<Diagnostic>();
        var viewModels = Collect(sources, diagnostics);
        result.AddDiagnostics(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        // The full contract (generated members not to redeclare, item types), from an in-memory compile that sees the
        // hand-written partials: a command they declare themselves (or call directly) needs no stub.
        var compiler = new FlagstaffCompiler { AppNamespace = rootNamespace, NamespaceIsOverridden = true, ReportExpressibleEscapes = false };
        compiler.UserDeclarations = UserDeclarationScanner.Scan(ctx, project, roots);
        var compilation = compiler.CompileAll(sources);
        foreach (var (vmType, methods) in viewModels)
        {
            var notNeeded = compilation.Contracts
                .Where(c => c.ViewModelType == vmType)
                .SelectMany(c => c.Commands)
                .Where(c => c.Delegate is not null && (c.DeclaredBy is not null || c.DelegateMode == DelegateMode.UserMethod))
                .Select(c => c.Delegate!)
                .ToHashSet(StringComparer.Ordinal);
            methods.RemoveAll(m => notNeeded.Contains(m.Name));
        }

        var stubDir = ctx.Resolve(dir);
        var created = new List<string>();
        var updated = new JsonObject();
        var pending = new JsonObject();
        foreach (var (vmType, methods) in viewModels)
        {
            var existing = FindExisting(stubDir, vmType);
            if (existing is null)
            {
                Directory.CreateDirectory(stubDir);
                var path = Path.Combine(stubDir, vmType + ".cs");
                File.WriteAllText(path, Render(rootNamespace, vmType, methods));
                created.Add(path);
                result.Outputs.Add(path);
                ctx.Progress($"  created {ctx.Display(path)} ({methods.Count} method(s))");
                continue;
            }

            var text = File.ReadAllText(existing);
            var missing = methods.Where(m => !Regex.IsMatch(text, $@"\b{Regex.Escape(m.Name)}\s*\(")).ToList();
            if (missing.Count == 0)
                continue;
            if (update)
            {
                File.WriteAllText(existing, AppendBeforeFinalBrace(text, missing.Select(m => m.StubLine)));
                updated[existing] = Output.ToArray(missing.Select(m => m.Signature));
                result.Outputs.Add(existing);
                ctx.Progress($"  updated {ctx.Display(existing)} (+{missing.Count} method(s))");
            }
            else
            {
                pending[existing] = Output.ToArray(missing.Select(m => m.Signature));
            }
        }

        result.Extra["namespace"] = rootNamespace + ".ViewModels";
        result.Extra["viewModels"] = Output.ToArray(viewModels.Keys);
        result.Extra["created"] = Output.ToArray(created);
        result.Extra["updated"] = updated;
        result.Extra["missing"] = pending;
        if (pending.Count > 0)
            result.Hint = "Some existing partials lack methods; run 'flagstaff stubs --update' to append them.";
        foreach (var (file, sigs) in pending)
        {
            foreach (var sig in sigs!.AsArray())
                result.TextLines.Add($"{ctx.Display(file)}: missing {sig}");
        }

        ContractOutput.Attach(result, compilation, ctx.Display);
        result.TextLines.AddRange(ContractOutput.TextLines(compilation));
        ctx.Progress($"stubs: {created.Count} created, {updated.Count} updated, {pending.Count} with missing methods");
        return result;
    }

    /// <summary>Command delegates per ViewModel type, in document order, de-duplicated by method name.</summary>
    public static Dictionary<string, List<StubMethod>> Collect(IEnumerable<SourceFile> sources, List<Diagnostic> diagnostics)
    {
        var map = new Dictionary<string, List<StubMethod>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (Parse(source, diagnostics) is not { } file)
                continue;
            foreach (var view in file.Declarations.OfType<ViewDecl>())
            {
                if (view.Binding is null)
                    continue;
                if (!map.TryGetValue(view.Binding.ViewModelType, out var methods))
                    map[view.Binding.ViewModelType] = methods = [];
                foreach (var member in TypeInference.InferMembers(view).Where(m => m.Kind == InferredKind.Command))
                {
                    var stem = member.Name.EndsWith("Command", StringComparison.Ordinal) && member.Name.Length > "Command".Length
                        ? member.Name[..^"Command".Length]
                        : member.Name;
                    var name = $"On{stem}Async";
                    if (methods.All(m => m.Name != name))
                        methods.Add(new StubMethod(name, member.ParameterType));
                }
            }
        }
        return map;
    }

    public static FlagFile? Parse(SourceFile source, List<Diagnostic> diagnostics)
    {
        Parser? parser = null;
        try
        {
            parser = new Parser(new Lexer(source.Content, source.Path).Tokenize(tolerant: true));
            var file = parser.Parse();
            diagnostics.AddRange(parser.Diagnostics);
            return file;
        }
        catch (FlagstaffParseException ex)
        {
            if (parser is not null)
                diagnostics.AddRange(parser.Diagnostics);
            diagnostics.Add(ex.ToDiagnostic());
            return null;
        }
    }

    public static string Render(string rootNamespace, string vmType, IEnumerable<StubMethod> methods)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// Hand-written half of {vmType}. Flagstaff generates {vmType}.g.cs (properties + [RelayCommand]s);");
        sb.AppendLine("// implement the command delegates here. 'flagstaff stubs --update' appends methods for new commands.");
        sb.AppendLine($"// Do not redeclare the generated properties or commands (listed at the top of Generated/{vmType}.g.cs).");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine($"namespace {rootNamespace}.ViewModels;");
        sb.AppendLine();
        sb.AppendLine($"public partial class {vmType}");
        sb.AppendLine("{");
        foreach (var m in methods)
            sb.AppendLine($"    {m.StubLine}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>The hand-written partial for <paramref name="vmType"/>: <c>{vmType}.cs</c>, else any non-generated .cs declaring it.</summary>
    public static string? FindExisting(string dir, string vmType)
    {
        if (!Directory.Exists(dir))
            return null;
        var direct = Path.Combine(dir, vmType + ".cs");
        if (File.Exists(direct))
            return direct;
        var pattern = new Regex($@"\bpartial\s+class\s+{Regex.Escape(vmType)}\b");
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(f => pattern.IsMatch(File.ReadAllText(f)));
    }

    /// <summary>Inserts <paramref name="lines"/> (indented one level) before the last <c>}</c> of a C# file.</summary>
    public static string AppendBeforeFinalBrace(string text, IEnumerable<string> lines)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var close = text.LastIndexOf('}');
        if (close < 0)
            return text + newline + string.Concat(lines.Select(l => "    " + l + newline));

        var lineStart = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
        var braceOnOwnLine = string.IsNullOrWhiteSpace(text[lineStart..close]);
        var before = braceOnOwnLine ? text[..lineStart] : text[..close] + newline;
        var after = braceOnOwnLine ? text[lineStart..] : text[close..];
        var trimmed = before.TrimEnd();
        var needsBlank = trimmed.Length > 0 && trimmed[^1] != '{';
        var block = new StringBuilder();
        if (needsBlank)
            block.Append(newline);
        foreach (var l in lines)
            block.Append("    ").Append(l).Append(newline);
        return before + block + after;
    }
}
