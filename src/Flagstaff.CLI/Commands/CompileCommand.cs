using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flagstaff.Compiler;

namespace Flagstaff.Cli.Commands;

/// <summary>Settings shared by <c>check</c>, <c>compile</c>, <c>watch</c> and the pre-compile step of <c>build</c>.</summary>
internal sealed record CompileRequest(
    IReadOnlyList<string>? Paths,
    string? OutputDirectory = null,
    string? Namespace = null,
    string? StylesSource = null,
    bool Clean = false);

/// <summary><c>compile [paths...] [-o dir] [-n ns] [-s styles] [--clean]</c>: writes generated files and a manifest.</summary>
internal static class CompileCommand
{
    public const string ManifestFileName = ".flagstaff-manifest.json";

    public static Command Create(CliHost host)
    {
        var paths = new Argument<string[]>("paths")
        {
            Description = ".flag files or directories (default: ./Flags if it exists, else .)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var output = new Option<string>("--output", "-o") { Description = "Output directory (default: ./Generated)" };
        var ns = new Option<string>("--namespace", "-n") { Description = "Root namespace (default: the project's RootNamespace, else the app name)" };
        var styles = new Option<string>("--styles-source", "-s") { Description = "ResourceDictionary Source for the styles (default: the Flagstaff.Stdlib styles)" };
        var clean = new Option<bool>("--clean")
        {
            Description = "Delete files from the previous manifest that were not produced, even when there are errors (by default stale files are removed only on a clean compile)",
        };

        var cmd = new Command("compile", "Compile .flag files to XAML, code-behind and ViewModel partials (writes Generated/ and its .flagstaff-manifest.json)");
        cmd.Arguments.Add(paths);
        cmd.Options.Add(output);
        cmd.Options.Add(ns);
        cmd.Options.Add(styles);
        cmd.Options.Add(clean);
        host.Bind(cmd, (pr, ctx) => Run(ctx, "compile", new CompileRequest(
            pr.GetValue(paths), pr.GetValue(output), pr.GetValue(ns), pr.GetValue(styles), pr.GetValue(clean))));
        return cmd;
    }

    /// <summary>Resolves inputs and compiles in memory. Returns null result plus an error result on a usage problem.</summary>
    public static (CompilationResult? Compilation, ProjectInfo Project, List<string> Files, string Namespace, CommandResult? Error)
        CompileInMemory(CliContext ctx, string command, CompileRequest request)
    {
        var (files, roots, error) = FlagSources.Resolve(ctx, request.Paths);
        var project = ProjectInfo.Discover(ctx.WorkingDirectory, roots);
        if (error is not null)
            return (null, project, files, "", CommandResult.Usage(command, error));
        if (files.Count == 0)
        {
            var r = new CommandResult(command).Fail(
                $"No .flag files found in {string.Join(", ", roots.Select(ctx.Display))}.", ExitCodes.Failed,
                "Put .flag files in ./Flags (see 'flagstaff spec'), or pass paths explicitly.");
            return (null, project, files, "", r);
        }

        var sources = FlagSources.Load(ctx, files);
        var ns = project.ResolveNamespace(request.Namespace, sources);
        var compiler = new FlagstaffCompiler();
        compiler.AppNamespace = ns;
        compiler.NamespaceIsOverridden = true;
        compiler.ReportExpressibleEscapes = true;
        if (request.StylesSource is not null)
            compiler.StylesSource = request.StylesSource;
        // The hand-written C# partials: generated members they already declare are skipped (FS2016/FS2017).
        compiler.UserDeclarations = UserDeclarationScanner.Scan(ctx, project, roots,
            request.OutputDirectory is null ? null : ctx.Resolve(request.OutputDirectory));
        return (compiler.CompileAll(sources), project, files, ns, null);
    }

    public static CommandResult Run(CliContext ctx, string command, CompileRequest request) =>
        Run(ctx, command, request, out _, out _);

    /// <summary>Compiles and writes the output; also returns the in-memory compilation and namespace (null on a usage error).</summary>
    public static CommandResult Run(CliContext ctx, string command, CompileRequest request,
        out CompilationResult? compiled, out string rootNamespace)
    {
        var (compilation, project, files, ns, error) = CompileInMemory(ctx, command, request);
        compiled = compilation;
        rootNamespace = ns;
        if (error is not null)
            return error;

        var result = new CommandResult(command);
        result.AddDiagnostics(compilation!.Diagnostics);

        // Default output: Generated/ beside the owning .csproj (found from the inputs), else ./Generated.
        var outDir = request.OutputDirectory is not null
            ? ctx.Resolve(request.OutputDirectory)
            : project.ProjectFile is not null
                ? project.GeneratedDirectory
                : Path.Combine(ctx.WorkingDirectory, ProjectInfo.DefaultOutputDir);
        Directory.CreateDirectory(outDir);

        var written = new List<string>();
        var unchanged = 0;
        foreach (var file in compilation.Files)
        {
            var path = Path.Combine(outDir, file.Name);
            if (File.Exists(path) && File.ReadAllText(path) == file.Content)
                unchanged++;
            else
            {
                File.WriteAllText(path, file.Content);
                written.Add(path);
            }
            result.Outputs.Add(path);
        }

        // Stale-file cleanup is driven only by the previous manifest: unlisted files are never touched.
        var produced = compilation.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(outDir, ManifestFileName);
        var previous = ReadManifest(manifestPath);
        var stale = previous.Where(f => !produced.Contains(f)).ToList();
        var removed = new List<string>();
        var keep = new List<string>();
        var removeStale = !compilation.HasErrors || request.Clean;
        foreach (var name in stale)
        {
            var path = Path.GetFullPath(Path.Combine(outDir, name));
            if (!IsInside(path, outDir) || !File.Exists(path))
                continue;
            if (removeStale)
            {
                File.Delete(path);
                removed.Add(path);
            }
            else
            {
                keep.Add(name); // a view with errors produced nothing this time: keep its last good output
            }
        }
        WriteManifest(manifestPath, produced.Concat(keep));

        result.Ok = !compilation.HasErrors;
        result.Extra["namespace"] = ns;
        result.Extra["outputDirectory"] = outDir;
        result.Extra["manifest"] = manifestPath;
        result.Extra["inputs"] = Output.ToArray(files.Select(ctx.Display));
        result.Extra["written"] = written.Count;
        result.Extra["unchanged"] = unchanged;
        result.Extra["removed"] = Output.ToArray(removed);
        result.Extra["errors"] = result.ErrorCount;
        result.Extra["warnings"] = result.WarningCount;
        StatsOutput.Attach(result, compilation.Stats);

        foreach (var path in removed)
            ctx.Progress($"  removed stale {ctx.Display(path)}");
        ctx.Progress(result.Ok
            ? $"compiled {files.Count} file(s) -> {ctx.Display(outDir)} ({written.Count} written, {unchanged} unchanged, {removed.Count} removed, {result.WarningCount} warning(s))"
            : $"{result.ErrorCount} error(s), {result.WarningCount} warning(s) in {files.Count} file(s)");
        return result;
    }

    public static List<string> ReadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return [];
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(manifestPath));
            return node?["files"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void WriteManifest(string manifestPath, IEnumerable<string> files)
    {
        var o = new JsonObject
        {
            ["tool"] = "flagstaff",
            ["version"] = Cli.Version,
            ["files"] = Output.ToArray(files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)),
        };
        var text = o.ToJsonString(Output.JsonOptions) + Environment.NewLine;
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != text)
            File.WriteAllText(manifestPath, text);
    }

    private static bool IsInside(string path, string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(full, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary><c>check [paths...]</c>: parse, generate in memory and lint, writing nothing.</summary>
internal static class CheckCommand
{
    public static Command Create(CliHost host)
    {
        var paths = new Argument<string[]>("paths")
        {
            Description = ".flag files or directories (default: ./Flags if it exists, else .)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var ns = new Option<string>("--namespace", "-n") { Description = "Root namespace used for lint checks" };
        var statsOnly = new Option<bool>("--stats-only")
        {
            Description = "Print only the escape-ratio / generic-ratio stats (no diagnostics); the exit code still reflects errors",
        };
        var cmd = new Command("check", "Parse, generate in memory and lint .flag files; writes nothing");
        cmd.Arguments.Add(paths);
        cmd.Options.Add(ns);
        cmd.Options.Add(statsOnly);
        host.Bind(cmd, (pr, ctx) => Run(ctx, new CompileRequest(pr.GetValue(paths), Namespace: pr.GetValue(ns)),
            statsOnly: pr.GetValue(statsOnly)));
        return cmd;
    }

    public static CommandResult Run(CliContext ctx, CompileRequest request, string command = "check", bool statsOnly = false)
    {
        var (compilation, _, files, ns, error) = CompileCommand.CompileInMemory(ctx, command, request);
        if (error is not null)
            return error;

        var result = new CommandResult(command);
        result.Ok = !compilation!.HasErrors;
        if (statsOnly)
        {
            result.Extra["errors"] = compilation.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            StatsOutput.Attach(result, compilation.Stats);
            return result;
        }

        result.AddDiagnostics(compilation.Diagnostics);
        result.Extra["namespace"] = ns;
        result.Extra["inputs"] = Output.ToArray(files.Select(ctx.Display));
        result.Extra["generated"] = Output.ToArray(compilation.Files.Select(f => f.Name));
        result.Extra["errors"] = result.ErrorCount;
        result.Extra["warnings"] = result.WarningCount;
        StatsOutput.Attach(result, compilation.Stats);
        ContractOutput.Attach(result, compilation, ctx.Display);
        ctx.Progress(result.Ok
            ? $"ok:{files.Count} file(s), {result.WarningCount} warning(s)"
            : $"{result.ErrorCount} error(s), {result.WarningCount} warning(s) in {files.Count} file(s)");
        return result;
    }
}
