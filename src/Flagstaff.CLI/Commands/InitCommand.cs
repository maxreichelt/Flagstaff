using System.CommandLine;
using System.Text.RegularExpressions;

namespace Flagstaff.Cli.Commands;

/// <summary>
/// <c>init &lt;AppName&gt; [-o dir] [--nav side|top] [--agent claude|cursor|none]</c>: a runnable WinUI 3 project
/// (csproj, app.manifest, Flags/app.flag + home.flag, HomeViewModel partial, App.Services.cs, tool manifest).
/// </summary>
internal static partial class InitCommand
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex AppNamePattern();

    public static Command Create(CliHost host)
    {
        var name = new Argument<string>("AppName") { Description = "Project and root namespace name (a C# identifier, e.g. TaskTrack)" };
        var output = new Option<string>("--output", "-o") { Description = "Target directory (default: ./<AppName>)" };
        var nav = new Option<string>("--nav") { Description = "Navigation style: side or top", DefaultValueFactory = _ => "side" };
        nav.AcceptOnlyFromAmong("side", "top");
        var agent = new Option<string>("--agent") { Description = "Agent instructions to add: claude, cursor or none", DefaultValueFactory = _ => "none" };
        agent.AcceptOnlyFromAmong("claude", "cursor", "none");

        var cmd = new Command("init", "Create a runnable WinUI 3 + Flagstaff project");
        cmd.Arguments.Add(name);
        cmd.Options.Add(output);
        cmd.Options.Add(nav);
        cmd.Options.Add(agent);
        host.Bind(cmd, (pr, ctx) => Run(ctx, pr.GetValue(name)!, pr.GetValue(output), pr.GetValue(nav)!, pr.GetValue(agent)!));
        return cmd;
    }

    /// <summary>Agent assets per <c>--agent</c>: template name → output path.</summary>
    public static IReadOnlyList<(string Template, string Path)> AgentFiles(string agent) => agent switch
    {
        "claude" =>
        [
            ("agents/AGENTS.md", "AGENTS.md"),
            ("agents/CLAUDE.md", "CLAUDE.md"),
            ("agents/SKILL.md", Path.Combine(".claude", "skills", "flagstaff", "SKILL.md")),
        ],
        "cursor" =>
        [
            ("agents/AGENTS.md", "AGENTS.md"),
            ("agents/flagstaff.mdc", Path.Combine(".cursor", "rules", "flagstaff.mdc")),
        ],
        _ => [],
    };

    public static CommandResult Run(CliContext ctx, string appName, string? output, string nav, string agent)
    {
        var result = new CommandResult("init");
        if (!AppNamePattern().IsMatch(appName))
            return result.Fail($"'{appName}' is not a valid C# identifier / namespace.", ExitCodes.Usage, "Use a name like TaskTrack or Contoso.Notes.");

        var target = ctx.Resolve(output ?? appName);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return result.Fail($"{ctx.Display(target)} already exists and is not empty.", ExitCodes.Failed, "Choose another name or pass -o <empty dir>.");

        var values = new Dictionary<string, string>
        {
            ["AppName"] = appName,
            ["Nav"] = nav,
            ["FlagstaffVersion"] = Cli.Version,
            ["WindowsAppSdkVersion"] = Cli.WindowsAppSdkVersion,
        };

        var files = Templates.List("init")
            .Select(t => (Template: t, Path: Templates.OutputPath(t["init/".Length..], values)))
            .Concat(AgentFiles(agent))
            .ToList();
        foreach (var (template, relative) in files)
        {
            var path = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Templates.Read(template, values));
            result.Outputs.Add(path);
            ctx.Progress($"  created {ctx.Display(path)}");
        }

        var cd = ctx.Display(target);
        var nextSteps = new[] { $"cd {cd}", "flagstaff check", "flagstaff run" };
        result.Extra["directory"] = target;
        result.Extra["project"] = Path.Combine(target, appName + ".csproj");
        result.Extra["nextSteps"] = Output.ToArray(nextSteps);
        result.TextLines.Add($"Created {appName} in {cd}. Next steps:");
        result.TextLines.AddRange(nextSteps.Select(s => "  " + s));
        return result;
    }
}
