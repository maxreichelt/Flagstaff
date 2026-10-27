using System.CommandLine;
using System.Text;
using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Cli.Commands;

/// <summary>
/// <c>new view &lt;Name&gt; [--label "Text"] [--icon home]</c>: writes <c>Flags/&lt;name-kebab&gt;.flag</c> and
/// <c>ViewModels/&lt;Name&gt;ViewModel.cs</c>, and adds a nav link to <c>Flags/app.flag</c>. Never overwrites.
/// </summary>
internal static partial class NewCommand
{
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex PascalName();

    public static Command Create(CliHost host)
    {
        var name = new Argument<string>("Name") { Description = "PascalCase view name, e.g. Settings or OrderHistory (a View suffix is added)" };
        var label = new Option<string>("--label") { Description = "Nav link and heading text (default: the name in words)" };
        var icon = new Option<string>("--icon") { Description = "Symbol icon in lowercase (default: document)", DefaultValueFactory = _ => "document" };
        var view = new Command("view", "Add a view: Flags/<name>.flag, ViewModels/<Name>ViewModel.cs and a nav link in Flags/app.flag");
        view.Arguments.Add(name);
        view.Options.Add(label);
        view.Options.Add(icon);
        host.Bind(view, (pr, ctx) => RunView(ctx, pr.GetValue(name)!, pr.GetValue(label), pr.GetValue(icon)!));

        var cmd = new Command("new", "Add items to a Flagstaff project");
        cmd.Subcommands.Add(view);
        return cmd;
    }

    public static CommandResult RunView(CliContext ctx, string name, string? label, string icon)
    {
        var result = new CommandResult("new view");
        if (!PascalName().IsMatch(name))
            return result.Fail($"'{name}' must be a PascalCase identifier (e.g. Settings, OrderHistory).", ExitCodes.Usage);

        var viewName = name.EndsWith("View", StringComparison.Ordinal) && name.Length > 4 ? name : name + "View";
        var baseName = viewName[..^"View".Length];
        var vmType = baseName + "ViewModel";
        label ??= Words(baseName);
        icon = icon.ToLowerInvariant();

        var project = ProjectInfo.ForDirectory(ctx.WorkingDirectory);
        var flagsDir = project.FlagsDirectory;
        var flagPath = Path.Combine(flagsDir, Kebab(baseName) + ".flag");
        var vmDir = Path.Combine(ctx.WorkingDirectory, "ViewModels");
        var vmPath = Path.Combine(vmDir, vmType + ".cs");

        var existing = new[] { flagPath, vmPath }.Where(File.Exists).ToList();
        if (StubsCommand.FindExisting(vmDir, vmType) is { } otherVm && !existing.Contains(otherVm))
            existing.Add(otherVm);
        if (existing.Count > 0)
            return result.Fail($"Refusing to overwrite: {string.Join(", ", existing.Select(ctx.Display))}.", ExitCodes.Failed);

        var appFile = FindAppFile(flagsDir);
        var appSources = appFile is null ? [] : new[] { new SourceFile(appFile, File.ReadAllText(appFile)) };
        var ns = project.ResolveNamespace(null, appSources);

        var values = new Dictionary<string, string>
        {
            ["ViewName"] = viewName,
            ["ViewModel"] = vmType,
            ["Label"] = label.Replace("\"", "'", StringComparison.Ordinal),
            ["Icon"] = icon,
        };
        Directory.CreateDirectory(flagsDir);
        Directory.CreateDirectory(vmDir);
        File.WriteAllText(flagPath, Templates.Read("newview/view.flag", values));
        File.WriteAllText(vmPath, StubsCommand.Render(ns, vmType, [new StubMethod("OnLoadAsync", null)]));
        result.Outputs.Add(flagPath);
        result.Outputs.Add(vmPath);
        ctx.Progress($"  created {ctx.Display(flagPath)}");
        ctx.Progress($"  created {ctx.Display(vmPath)}");

        result.Extra["view"] = viewName;
        result.Extra["viewModel"] = vmType;
        if (appFile is null)
        {
            result.Extra["navUpdated"] = false;
            result.Hint = $"No app.flag found; add  link \"{label}\" icon: {icon} -> {viewName};  to your nav block.";
        }
        else
        {
            var text = File.ReadAllText(appFile);
            var linkLine = $"link \"{values["Label"]}\" icon: {icon} -> {viewName};";
            var updated = InsertNavLink(text, appFile, viewName, linkLine, out var reason);
            if (updated is not null)
            {
                File.WriteAllText(appFile, updated);
                result.Outputs.Add(appFile);
                ctx.Progress($"  added nav link to {ctx.Display(appFile)}");
            }
            else
            {
                result.Hint = reason;
            }
            result.Extra["navUpdated"] = updated is not null;
            result.Extra["appFile"] = appFile;
        }
        result.TextLines.Add($"Added {viewName}. Next: edit {ctx.Display(flagPath)}, then 'flagstaff check'.");
        return result;
    }

    private static string? FindAppFile(string flagsDir)
    {
        if (!Directory.Exists(flagsDir))
            return null;
        var app = Path.Combine(flagsDir, "app.flag");
        if (File.Exists(app))
            return app;
        return Directory.EnumerateFiles(flagsDir, "*.flag", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(f => Regex.IsMatch(File.ReadAllText(f), @"(?m)^\s*app\s+[A-Za-z_]"));
    }

    /// <summary>
    /// Inserts <paramref name="linkLine"/> before the closing brace of the <c>nav</c> block, found with the
    /// compiler's lexer. Returns null (with a reason) when there is no nav block or the link already exists.
    /// </summary>
    public static string? InsertNavLink(string text, string fileName, string viewName, string linkLine, out string? reason)
    {
        reason = null;
        List<Token> tokens;
        try
        {
            tokens = new Lexer(text, fileName).Tokenize(tolerant: true);
            var parsed = new Parser(tokens).Parse();
            var links = parsed.Declarations.OfType<AppDecl>().SelectMany(a => a.Members).OfType<NavBlock>()
                .SelectMany(n => n.Members).OfType<NavLinkDecl>();
            if (links.Any(l => l.Target == viewName))
            {
                reason = $"{Path.GetFileName(fileName)} already links to {viewName}.";
                return null;
            }
        }
        catch (FlagstaffParseException ex)
        {
            reason = $"Could not parse {Path.GetFileName(fileName)} ({ex.Message}); add the nav link by hand: {linkLine}";
            return null;
        }

        var navIndex = tokens.FindIndex(t => t.IsKeyword("nav"));
        var open = navIndex < 0 ? -1 : tokens.FindIndex(navIndex, t => t.Kind == TokenKind.LeftBrace);
        if (open < 0)
        {
            reason = $"No nav block in {Path.GetFileName(fileName)}; add one with: {linkLine}";
            return null;
        }
        var depth = 0;
        var close = -1;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.LeftBrace)
                depth++;
            else if (tokens[i].Kind == TokenKind.RightBrace && --depth == 0)
            {
                close = i;
                break;
            }
        }
        if (close < 0)
        {
            reason = $"Unbalanced nav block in {Path.GetFileName(fileName)}.";
            return null;
        }

        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }
        int Offset(SourceLocation loc) => lineStarts[loc.Line - 1] + loc.Column - 1;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var braceOffset = Offset(tokens[close].Location);
        var braceLineStart = lineStarts[tokens[close].Location.Line - 1];
        var braceIndent = LeadingWhitespace(text, braceLineStart);

        // Indent like the last link/brand line inside the block, else one level deeper than the brace.
        var lastMember = tokens.Take(close).Skip(open + 1).LastOrDefault(t => t.IsKeyword("link") || t.IsKeyword("brand"));
        var indent = lastMember.Kind == TokenKind.Keyword
            ? LeadingWhitespace(text, lineStarts[lastMember.Location.Line - 1])
            : braceIndent + "    ";

        var sb = new StringBuilder();
        if (string.IsNullOrWhiteSpace(text[braceLineStart..braceOffset]))
        {
            sb.Append(text, 0, braceLineStart);
            sb.Append(indent).Append(linkLine).Append(newline);
            sb.Append(text, braceLineStart, text.Length - braceLineStart);
        }
        else
        {
            sb.Append(text, 0, braceOffset);
            sb.Append(linkLine).Append(' ');
            sb.Append(text, braceOffset, text.Length - braceOffset);
        }
        return sb.ToString();
    }

    private static string LeadingWhitespace(string text, int lineStart)
    {
        var end = lineStart;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[lineStart..end];
    }

    public static string Kebab(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])|(?<=[A-Z])([A-Z][a-z])", "-$1$2").ToLowerInvariant();

    public static string Words(string pascal) =>
        Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])|(?<=[A-Z])([A-Z][a-z])", " $1$2");
}
