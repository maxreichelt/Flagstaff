using System.CommandLine;
using System.Diagnostics;

namespace Flagstaff.Cli.Commands;

/// <summary><c>run [project-or-dir] [--no-build]</c>: build, then launch the app's exe detached and print its pid.</summary>
internal static class RunCommand
{
    public static Command Create(CliHost host)
    {
        var cmd = new Command("run", "Build (unless --no-build) and launch the app detached; prints { pid, exe }");
        var (project, configuration, platform, binlog) = BuildCommand.AddBuildOptions(cmd);
        var noBuild = new Option<bool>("--no-build") { Description = "Launch the last build without building" };
        cmd.Options.Add(noBuild);
        host.Bind(cmd, (pr, ctx) => Run(ctx,
            new BuildRequest(pr.GetValue(project), pr.GetValue(configuration)!, pr.GetValue(platform)!, pr.GetValue(binlog)),
            pr.GetValue(noBuild)));
        return cmd;
    }

    public static CommandResult Run(CliContext ctx, BuildRequest request, bool noBuild)
    {
        CommandResult result;
        ProjectInfo? project;
        if (noBuild)
        {
            result = new CommandResult("run");
            (project, var error) = BuildCommand.ResolveProject(ctx, request.ProjectOrDir);
            if (project is null)
                return result.Fail(error!, ExitCodes.Usage);
        }
        else
        {
            result = BuildCommand.Run(ctx, request, "run", out project);
            if (!result.Ok)
                return result;
        }

        var exe = FindExe(project!, request);
        if (exe is null)
        {
            return result.Fail(
                $"No {project!.EffectiveAssemblyName}.exe under {Path.Combine(project.Directory, "bin", request.MsBuildPlatform, request.Configuration)}.",
                ExitCodes.Failed, noBuild ? "Run 'flagstaff run' without --no-build, or 'flagstaff build' first." : null);
        }

        var pid = Launch(exe);
        result.Extra["pid"] = pid;
        result.Extra["exe"] = exe;
        result.Outputs.Add(exe);
        result.TextLines.Add($"launched {exe} (pid {pid})");
        return result;
    }

    /// <summary>The newest <c>&lt;AssemblyName&gt;.exe</c> under <c>bin/&lt;platform&gt;/&lt;configuration&gt;/</c> (then <c>bin/</c>).</summary>
    public static string? FindExe(ProjectInfo project, BuildRequest request)
    {
        var name = (project.EffectiveAssemblyName ?? "app") + ".exe";
        foreach (var dir in new[]
                 {
                     Path.Combine(project.Directory, "bin", request.MsBuildPlatform, request.Configuration),
                     Path.Combine(project.Directory, "bin"),
                 })
        {
            if (!Directory.Exists(dir))
                continue;
            var exe = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories)
                .Where(f => dir.EndsWith("bin", StringComparison.OrdinalIgnoreCase) == false
                            || f.Contains(Path.DirectorySeparatorChar + request.Configuration + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (exe is not null)
                return exe;
        }
        return null;
    }

    /// <summary>Starts the exe detached (not a child of this console) and returns its pid.</summary>
    public static int Launch(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        return process.Id;
    }
}
