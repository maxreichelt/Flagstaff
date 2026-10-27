using System.CommandLine;
using System.CommandLine.Help;
using System.Reflection;
using Flagstaff.Cli.Commands;

namespace Flagstaff.Cli;

/// <summary>
/// The <c>flagstaff</c> command tree. Conventions for agents: stdout carries only the result (in
/// <c>--format json</c> exactly one JSON document), stderr carries progress and human text. Exit codes:
/// 0 ok, 1 diagnostics/build errors, 2 usage, 3 environment, 4 internal crash.
/// </summary>
internal static class Cli
{
    /// <summary>The tool version (assembly informational version without the source-revision suffix).</summary>
    public static string Version { get; } =
        (typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    /// <summary>The Microsoft.WindowsAppSDK version pinned in Directory.Packages.props when the CLI was built.</summary>
    public static string WindowsAppSdkVersion { get; } =
        typeof(Cli).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WindowsAppSdkVersion" && !string.IsNullOrEmpty(a.Value))?.Value
        ?? "1.7.260224002";

    /// <summary>Runs the CLI in-process. Tests call this with their own writers and working directory.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var host = new CliHost(stdout, stderr, Path.GetFullPath(workingDirectory), cancellationToken);
        var root = host.BuildRoot();
        var parseResult = root.Parse(args);

        if (parseResult.Errors.Count > 0 && parseResult.Action is not HelpAction)
        {
            var ctx = host.ContextFromRawArgs(args);
            var command = parseResult.CommandResult.Command is RootCommand ? "flagstaff" : parseResult.CommandResult.Command.Name;
            var result = new CommandResult(command);
            var message = string.Join("; ", parseResult.Errors.Select(e => e.Message));
            result.Fail(message, ExitCodes.Usage, "Run 'flagstaff --help' or 'flagstaff <command> --help' for usage.");
            Output.Emit(ctx, result);
            return ExitCodes.Usage;
        }

        return parseResult.Invoke(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
        });
    }
}

/// <summary>Holds the streams and global options for one invocation and wires command actions.</summary>
internal sealed class CliHost(TextWriter stdout, TextWriter stderr, string workingDirectory, CancellationToken cancellationToken)
{
    public Option<string> Format { get; } = CreateFormat();
    public Option<bool> Quiet { get; } = new("--quiet", "-q")
    {
        Description = "Suppress progress output on stderr",
        Recursive = true,
    };
    public Option<bool> Version { get; } = new("--version") { Description = "Print the tool version" };

    private static Option<string> CreateFormat()
    {
        var o = new Option<string>("--format")
        {
            Description = "Output format: text (default) or json (one JSON document on stdout)",
            DefaultValueFactory = _ => "text",
            Recursive = true,
        };
        o.AcceptOnlyFromAmong("text", "json");
        return o;
    }

    public RootCommand BuildRoot()
    {
        var root = new RootCommand("Flagstaff: an LLM-friendly DSL (.flag) that transpiles to WinUI 3 XAML + MVVM scaffolding.");
        foreach (var o in root.Options.OfType<VersionOption>().ToList())
            root.Options.Remove(o);
        root.Options.Add(Format);
        root.Options.Add(Quiet);
        root.Options.Add(Version);

        root.Subcommands.Add(CheckCommand.Create(this));
        root.Subcommands.Add(CompileCommand.Create(this));
        root.Subcommands.Add(StubsCommand.Create(this));
        root.Subcommands.Add(BuildCommand.Create(this));
        root.Subcommands.Add(RunCommand.Create(this));
        root.Subcommands.Add(WatchCommand.Create(this));
        root.Subcommands.Add(InitCommand.Create(this));
        root.Subcommands.Add(NewCommand.Create(this));
        root.Subcommands.Add(DoctorCommand.Create(this));
        root.Subcommands.Add(SpecCommand.Create(this));

        root.SetAction(pr =>
        {
            if (pr.GetValue(Version))
            {
                return Execute(pr, "version", (ctx, _) =>
                {
                    var r = new CommandResult("version");
                    r.TextLines.Add(Cli.Version);
                    return r;
                });
            }
            return root.Parse(["--help"]).Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
        });
        return root;
    }

    public CliContext Context(ParseResult pr) => new()
    {
        Out = stdout,
        Err = stderr,
        WorkingDirectory = workingDirectory,
        Json = pr.GetValue(Format) == "json",
        Quiet = pr.GetValue(Quiet),
        CancellationToken = cancellationToken,
    };

    /// <summary>Best-effort context when parsing failed (so usage errors still honor --format json).</summary>
    public CliContext ContextFromRawArgs(string[] args)
    {
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--format=json" || args[i] == "--format:json" || (args[i] == "--format" && i + 1 < args.Length && args[i + 1] == "json"))
                json = true;
        }
        return new CliContext
        {
            Out = stdout,
            Err = stderr,
            WorkingDirectory = workingDirectory,
            Json = json,
            Quiet = args.Contains("--quiet") || args.Contains("-q"),
            CancellationToken = cancellationToken,
        };
    }

    /// <summary>Sets the command's action: runs <paramref name="body"/>, emits its result, maps crashes to exit 4.</summary>
    public void Bind(Command command, Func<ParseResult, CliContext, CommandResult> body) =>
        command.SetAction(pr => Execute(pr, CommandName(command), (ctx, p) => body(p, ctx)));

    private int Execute(ParseResult pr, string name, Func<CliContext, ParseResult, CommandResult> body)
    {
        var ctx = Context(pr);
        try
        {
            var result = body(ctx, pr);
            Output.Emit(ctx, result);
            return result.ExitCode;
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            var result = new CommandResult(name).Fail("Cancelled.", ExitCodes.Failed);
            Output.Emit(ctx, result);
            return result.ExitCode;
        }
        catch (Exception ex)
        {
            Output.EmitCrash(ctx, name, ex);
            return ExitCodes.Crash;
        }
    }

    private static string CommandName(Command command) => command.Parents.OfType<Command>().FirstOrDefault() is { } parent and not RootCommand
        ? $"{parent.Name} {command.Name}"
        : command.Name;
}
