using System.CommandLine;
using System.Diagnostics;

namespace Flagstaff.Cli.Commands;

/// <summary>
/// <c>watch [dir] [--run]</c>: recompiles on every change under Flags/ (200 ms debounce): check, then compile;
/// with <c>--run</c> also build and relaunch the app. One summary line per cycle on stderr; in
/// <c>--format json</c> one compact JSON envelope per cycle on stdout (JSON Lines).
/// </summary>
internal static class WatchCommand
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    public static Command Create(CliHost host)
    {
        var dir = new Argument<string>("dir") { Description = "Project directory (default: .)", Arity = ArgumentArity.ZeroOrOne };
        var run = new Option<bool>("--run") { Description = "Also build and (re)launch the app after each successful compile" };
        var once = new Option<bool>("--once") { Description = "Run a single cycle and exit (for scripts and tests)" };
        var cmd = new Command("watch", "Watch Flags/**/*.flag: check + compile on every change; --run also builds and relaunches");
        cmd.Arguments.Add(dir);
        cmd.Options.Add(run);
        cmd.Options.Add(once);
        host.Bind(cmd, (pr, ctx) => Run(ctx, pr.GetValue(dir), pr.GetValue(run), pr.GetValue(once)));
        return cmd;
    }

    public static CommandResult Run(CliContext ctx, string? dir, bool run, bool once)
    {
        var projectDir = ctx.Resolve(dir ?? ".");
        if (!Directory.Exists(projectDir))
            return CommandResult.Usage("watch", $"Not found: {ctx.Display(projectDir)}");
        var project = ProjectInfo.ForDirectory(projectDir);
        var watchDir = project.HasFlags ? project.FlagsDirectory : projectDir;
        if (run && project.ProjectFile is null)
            return CommandResult.Usage("watch", $"--run needs a .csproj in {ctx.Display(projectDir)}.");

        var quiet = new CliContext
        {
            Out = ctx.Out,
            Err = ctx.Err,
            WorkingDirectory = ctx.WorkingDirectory,
            Json = ctx.Json,
            Quiet = true,
            CancellationToken = ctx.CancellationToken,
        };
        var request = new CompileRequest([watchDir], OutputDirectory: project.GeneratedDirectory, Namespace: project.EffectiveNamespace);
        int? appPid = null;
        var cycles = 0;
        var lastOk = false;

        void Cycle()
        {
            cycles++;
            var stamp = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            var check = CheckCommand.Run(quiet, request, "watch");
            CommandResult outcome = check;
            string summary;
            if (!check.Ok)
            {
                summary = $"[{stamp}] check: {check.ErrorCount} error(s), {check.WarningCount} warning(s); Generated/ left unchanged";
            }
            else
            {
                var compile = CompileCommand.Run(quiet, "watch", request);
                outcome = compile;
                summary = $"[{stamp}] compiled: {compile.Extra["written"]} written, {compile.Extra["unchanged"]} unchanged, {compile.WarningCount} warning(s)";
                if (compile.Ok && run)
                {
                    var build = BuildCommand.Run(quiet, new BuildRequest(project.ProjectFile, "Debug", BuildCommand.DefaultPlatform, false), "watch", out _);
                    outcome = build;
                    if (build.Ok)
                    {
                        if (appPid is { } previous)
                            Kill(previous);
                        var exe = RunCommand.FindExe(project, new BuildRequest(project.ProjectFile, "Debug", BuildCommand.DefaultPlatform, false));
                        if (exe is not null)
                        {
                            appPid = RunCommand.Launch(exe);
                            build.Extra["pid"] = appPid;
                            build.Extra["exe"] = exe;
                            summary += $"; built and launched pid {appPid}";
                        }
                        else
                        {
                            summary += "; built, but no exe found";
                        }
                    }
                    else
                    {
                        summary += $"; build failed ({build.ErrorCount} error(s))";
                    }
                }
            }

            lastOk = outcome.Ok;
            outcome.Extra["cycle"] = cycles;
            if (!ctx.Quiet)
            {
                ctx.Err.WriteLine(summary);
                if (!ctx.Json)
                {
                    foreach (var d in outcome.Diagnostics)
                        ctx.Err.WriteLine("  " + d.ToMsBuildString());
                }
            }
            if (ctx.Json)
                ctx.Out.WriteLine(Output.Envelope(outcome).ToJsonString(Output.CompactJsonOptions));
        }

        Cycle();
        if (once)
        {
            var single = new CommandResult("watch") { Ok = lastOk, CompactJson = true };
            single.Extra["cycles"] = cycles;
            return single;
        }

        using var signal = new SemaphoreSlim(0);
        using var watcher = new FileSystemWatcher(watchDir, "*.flag")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        };
        FileSystemEventHandler onChange = (_, _) => signal.Release();
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += (_, _) => signal.Release();
        watcher.EnableRaisingEvents = true;
        ctx.Progress($"watching {ctx.Display(watchDir)} for *.flag changes (Ctrl+C to stop)");

        try
        {
            while (true)
            {
                signal.Wait(ctx.CancellationToken);
                while (signal.Wait(Debounce, ctx.CancellationToken))
                {
                    // drain bursts of events (editors write files in several steps)
                }
                Cycle();
            }
        }
        catch (OperationCanceledException)
        {
        }

        var result = new CommandResult("watch") { Ok = lastOk, CompactJson = true };
        result.Extra["cycles"] = cycles;
        if (appPid is { } pid)
            result.Extra["pid"] = pid;
        return result;
    }

    private static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            ProcessRunner.TryKill(p);
            p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // already exited
        }
    }
}
