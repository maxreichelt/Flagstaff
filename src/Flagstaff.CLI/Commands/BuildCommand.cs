using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Flagstaff.Compiler;

namespace Flagstaff.Cli.Commands;

internal sealed record BuildRequest(string? ProjectOrDir, string Configuration, string Platform, bool Binlog)
{
    /// <summary>MSBuild's spelling of the platform: <c>x64</c> / <c>ARM64</c>.</summary>
    public string MsBuildPlatform => Platform.Equals("arm64", StringComparison.OrdinalIgnoreCase) ? "ARM64" : "x64";
}

/// <summary>
/// <c>build [project-or-dir] [-c Debug|Release] [-p x64|arm64] [--binlog]</c>: compiles Flags/ in-process, then
/// builds with Visual Studio's MSBuild.exe (falls back to <c>dotnet build</c> + XamlCompiler output.json parsing).
/// </summary>
internal static class BuildCommand
{
    public static string DefaultPlatform =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public static (Argument<string> Project, Option<string> Configuration, Option<string> Platform, Option<bool> Binlog) AddBuildOptions(Command cmd)
    {
        var project = new Argument<string>("project-or-dir")
        {
            Description = "A .csproj, or a directory containing exactly one (default: .)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var configuration = new Option<string>("--configuration", "-c") { Description = "Debug or Release", DefaultValueFactory = _ => "Debug" };
        configuration.AcceptOnlyFromAmong("Debug", "Release");
        var platform = new Option<string>("--platform", "-p") { Description = "x64 or arm64 (default: this machine's)", DefaultValueFactory = _ => DefaultPlatform };
        platform.AcceptOnlyFromAmong("x64", "arm64", "ARM64", "X64");
        var binlog = new Option<bool>("--binlog") { Description = "Write msbuild.binlog next to the project" };
        cmd.Arguments.Add(project);
        cmd.Options.Add(configuration);
        cmd.Options.Add(platform);
        cmd.Options.Add(binlog);
        return (project, configuration, platform, binlog);
    }

    public static Command Create(CliHost host)
    {
        var cmd = new Command("build", "Build the WinUI app with Visual Studio's MSBuild.exe (reliable XAML errors); falls back to dotnet build");
        var (project, configuration, platform, binlog) = AddBuildOptions(cmd);
        host.Bind(cmd, (pr, ctx) => Run(ctx, new BuildRequest(pr.GetValue(project), pr.GetValue(configuration)!, pr.GetValue(platform)!, pr.GetValue(binlog)), "build", out _));
        return cmd;
    }

    /// <summary>Finds the project file for a <c>project-or-dir</c> argument.</summary>
    public static (ProjectInfo? Project, string? Error) ResolveProject(CliContext ctx, string? projectOrDir)
    {
        var path = ctx.Resolve(projectOrDir ?? ".");
        if (File.Exists(path) && path.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            return (ProjectInfo.ForProjectFile(path), null);
        if (!Directory.Exists(path))
            return (null, $"Not found: {ctx.Display(path)}");
        var projects = Directory.GetFiles(path, "*.csproj");
        return projects.Length switch
        {
            1 => (ProjectInfo.ForProjectFile(projects[0]), null),
            0 => (null, $"No .csproj in {ctx.Display(path)}. Pass the project path, or create one with 'flagstaff init <AppName>'."),
            _ => (null, $"Several .csproj files in {ctx.Display(path)}; pass one explicitly."),
        };
    }

    public static CommandResult Run(CliContext ctx, BuildRequest request, string command, out ProjectInfo? project)
    {
        var result = new CommandResult(command);
        (project, var error) = ResolveProject(ctx, request.ProjectOrDir);
        if (project is null)
            return result.Fail(error!, ExitCodes.Usage);
        var projectFile = project.ProjectFile!;

        result.Extra["project"] = projectFile;
        result.Extra["configuration"] = request.Configuration;
        result.Extra["platform"] = request.MsBuildPlatform;

        // 1. Compile Flags/ first: fast .flag errors without MSBuild, and Generated/ exists before MSBuild evaluates globs.
        ContractHints? hints = null;
        if (project.HasFlags)
        {
            var compile = CompileCommand.Run(ctx, command,
                new CompileRequest([project.FlagsDirectory], OutputDirectory: project.GeneratedDirectory, Namespace: project.EffectiveNamespace),
                out var compilation, out var rootNamespace);
            result.Diagnostics.AddRange(compile.Diagnostics);
            if (!compile.Ok)
                return result.Fail(compile.Error ?? "Fix the .flag errors above before building.", compile.ExitCode == ExitCodes.Ok ? ExitCodes.Failed : compile.ExitCode);
            if (compilation is not null)
                hints = CreateHints(project.Directory, compilation, rootNamespace, ctx.WorkingDirectory);
        }

        var binlogPath = request.Binlog ? Path.Combine(project.Directory, "msbuild.binlog") : null;
        if (binlogPath is not null)
            result.Extra["binlog"] = binlogPath;
        void Stream(string line)
        {
            if (!ctx.Quiet)
                ctx.Err.WriteLine(line);
        }

        var msbuild = MsBuildLocator.Find();
        result.Extra["msbuild"] = msbuild;
        var started = DateTime.Now;
        if (msbuild is not null)
        {
            result.Extra["engine"] = "msbuild";
            var args = new List<string>
            {
                projectFile, "/restore", $"/p:Platform={request.MsBuildPlatform}", $"/p:Configuration={request.Configuration}",
                "-nologo", "-v:m", "-clp:ForceNoAlign;Summary",
            };
            if (binlogPath is not null)
                args.Add($"-bl:{binlogPath}");
            ctx.Progress($"building {ctx.Display(projectFile)} with {msbuild}");
            var run = ProcessRunner.Run(msbuild, args, project.Directory, Stream, ctx.CancellationToken);
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!run.Started)
                return result.Fail($"Could not start MSBuild.exe: {run.StartError}", ExitCodes.Environment, MsBuildLocator.BuildToolsInstallHint);
            Merge(result, BuildOutputParser.ParseLines(run.Lines), hints);
            result.Extra["processExitCode"] = run.ExitCode;
            result.Ok = run.ExitCode == 0;
            if (!result.Ok && result.ErrorCount == 0)
                result.Error = $"MSBuild exited with code {run.ExitCode}.";
        }
        else
        {
            result.Extra["engine"] = "dotnet";
            ctx.Progress("MSBuild.exe not found; falling back to 'dotnet build' (WinUI XAML compiler errors may be hidden).");
            var args = new List<string>
            {
                "build", projectFile, $"-p:Platform={request.MsBuildPlatform}", "-c", request.Configuration,
                "-nologo", "-v:m", "-tl:off", "-clp:ForceNoAlign;Summary",
            };
            if (binlogPath is not null)
                args.Add($"-bl:{binlogPath}");
            var run = ProcessRunner.Run("dotnet", args, project.Directory, Stream, ctx.CancellationToken);
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!run.Started)
            {
                return result.Fail($"Neither MSBuild.exe nor the dotnet CLI could be started ({run.StartError}).",
                    ExitCodes.Environment, MsBuildLocator.BuildToolsInstallHint);
            }
            Merge(result, BuildOutputParser.ParseLines(run.Lines), hints);
            result.Extra["processExitCode"] = run.ExitCode;
            result.Ok = run.ExitCode == 0;
            if (!result.Ok)
            {
                // dotnet build swallows XamlCompiler errors; they are still in obj/**/output.json.
                if (BuildOutputParser.FindXamlCompilerOutput(project.Directory, started) is { } outputJson)
                {
                    result.Extra["xamlCompilerOutput"] = outputJson;
                    Merge(result, BuildOutputParser.ParseXamlCompilerOutput(File.ReadAllText(outputJson)), hints);
                }
                var onlyOpaque = result.Diagnostics.Where(d => d.IsError).All(d => d.Code is "MSB3073" or "MSB4181");
                if (result.ErrorCount == 0 || onlyOpaque)
                {
                    return result.Fail("dotnet build failed without reporting the XAML compiler's errors, and MSBuild.exe is not installed.",
                        ExitCodes.Environment, "Install the Visual Studio Build Tools so 'flagstaff build' can use MSBuild.exe: " + MsBuildLocator.BuildToolsInstallHint);
                }
                result.Hint = "Built with 'dotnet build'. For complete XAML diagnostics install the Build Tools: " + MsBuildLocator.BuildToolsInstallHint;
            }
        }

        ctx.Progress(result.Ok
            ? $"build succeeded ({result.WarningCount} warning(s))"
            : $"build failed: {result.ErrorCount} error(s), {result.WarningCount} warning(s)");
        return result;
    }

    private static void Merge(CommandResult result, IEnumerable<CliDiagnostic> diagnostics, ContractHints? hints)
    {
        foreach (var raw in diagnostics)
        {
            var d = hints?.Apply(raw) ?? raw;
            if (!result.Diagnostics.Contains(d))
                result.Diagnostics.Add(d);
        }
    }

    /// <summary>
    /// Contract hints for C# errors against the generated code; .flag paths shown relative to the project (the
    /// compilation's paths are relative to <paramref name="workingDirectory"/>, the directory flagstaff ran in).
    /// </summary>
    internal static ContractHints CreateHints(string projectDirectory, CompilationResult compilation, string rootNamespace, string? workingDirectory = null)
    {
        var cache = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        string? ReadLine(string file, int line)
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(projectDirectory, file);
            if (!cache.TryGetValue(path, out var lines))
            {
                try
                {
                    lines = File.Exists(path) ? File.ReadAllLines(path) : null;
                }
                catch (IOException)
                {
                    lines = null;
                }
                catch (UnauthorizedAccessException)
                {
                    lines = null;
                }
                cache[path] = lines;
            }
            return lines is not null && line >= 1 && line <= lines.Length ? lines[line - 1] : null;
        }

        string FlagLocation(SourceLocation l)
        {
            var path = string.IsNullOrEmpty(l.File)
                ? l.FileName
                : Path.GetRelativePath(projectDirectory, Path.GetFullPath(l.File, workingDirectory ?? projectDirectory));
            return $"{path.Replace('\\', '/')} line {l.Line}";
        }

        return new ContractHints(compilation.Contracts, compilation.ItemTypes, rootNamespace, FlagLocation, ReadLine);
    }
}
