using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Cli;

/// <summary>
/// The consumer project around a directory: its <c>.csproj</c> (if any), <c>Flags/</c> input and
/// <c>Generated/</c> output folders, and the root namespace generated code uses.
/// </summary>
internal sealed partial class ProjectInfo
{
    public const string DefaultInputDir = "Flags";
    public const string DefaultOutputDir = "Generated";

    public required string Directory { get; init; }
    public string? ProjectFile { get; init; }
    public string? RootNamespace { get; init; }
    public string? AssemblyName { get; init; }

    public string FlagsDirectory => Path.Combine(Directory, DefaultInputDir);
    public string GeneratedDirectory => Path.Combine(Directory, DefaultOutputDir);
    public bool HasFlags => System.IO.Directory.Exists(FlagsDirectory);

    /// <summary>MSBuild's defaults: RootNamespace and AssemblyName fall back to the project file name.</summary>
    public string? ProjectName => ProjectFile is null ? null : Path.GetFileNameWithoutExtension(ProjectFile);
    public string? EffectiveNamespace => RootNamespace ?? ProjectName;
    public string? EffectiveAssemblyName => AssemblyName ?? ProjectName;

    /// <summary>Describes the project in <paramref name="directory"/> (the single <c>*.csproj</c> there, if exactly one).</summary>
    public static ProjectInfo ForDirectory(string directory)
    {
        var projects = System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(directory, "*.csproj")
            : [];
        return projects.Length == 1 ? ForProjectFile(projects[0]) : new ProjectInfo { Directory = directory };
    }

    public static ProjectInfo ForProjectFile(string projectFile)
    {
        var text = File.ReadAllText(projectFile);
        return new ProjectInfo
        {
            Directory = Path.GetDirectoryName(projectFile)!,
            ProjectFile = projectFile,
            RootNamespace = ReadProperty(text, "RootNamespace"),
            AssemblyName = ReadProperty(text, "AssemblyName"),
        };
    }

    /// <summary>
    /// Finds the project a set of inputs belongs to: the working directory, else a directory above an
    /// input (<c>MyApp/Flags</c> → <c>MyApp</c>), else the working directory without a project.
    /// </summary>
    public static ProjectInfo Discover(string workingDirectory, IEnumerable<string> fullInputPaths)
    {
        var here = ForDirectory(workingDirectory);
        if (here.ProjectFile is not null)
            return here;
        foreach (var input in fullInputPaths)
        {
            var dir = System.IO.Directory.Exists(input) ? input : Path.GetDirectoryName(input);
            for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
            {
                var candidate = ForDirectory(d);
                if (candidate.ProjectFile is not null)
                    return candidate;
                if (string.Equals(d.TrimEnd('\\', '/'), workingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }
        return here;
    }

    /// <summary>
    /// The namespace for generated code: the explicit override, else the project's RootNamespace (what
    /// the MSBuild targets pass), else the <c>app</c> name, else the directory name.
    /// </summary>
    public string ResolveNamespace(string? explicitNamespace, IEnumerable<SourceFile> sources)
    {
        if (!string.IsNullOrWhiteSpace(explicitNamespace))
            return explicitNamespace;
        if (EffectiveNamespace is { } ns)
            return ns;
        foreach (var source in sources)
        {
            var match = AppNameRegex().Match(source.Content);
            if (match.Success)
                return match.Groups[1].Value;
        }
        var name = SanitizeIdentifier(new DirectoryInfo(Directory).Name);
        return name.Length > 0 ? name : "FlagstaffApp";
    }

    public static string SanitizeIdentifier(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.').ToArray();
        var s = new string(chars).Trim('.');
        return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
    }

    private static string? ReadProperty(string projectXml, string name)
    {
        var m = Regex.Match(projectXml, $@"<{name}>\s*([^<]+?)\s*</{name}>");
        return m.Success && !m.Groups[1].Value.Contains("$(", StringComparison.Ordinal) ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?m)^\s*app\s+([A-Za-z_][A-Za-z0-9_.]*)\s*\{")]
    private static partial Regex AppNameRegex();
}

/// <summary>Expands command-line paths into .flag sources.</summary>
internal static class FlagSources
{
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", ProjectInfo.DefaultOutputDir };

    /// <summary>
    /// Resolves <paramref name="paths"/> (default: <c>./Flags</c> when it exists, else <c>.</c>) to full
    /// .flag file paths. Returns an error message for a path that does not exist.
    /// </summary>
    public static (List<string> Files, List<string> Roots, string? Error) Resolve(CliContext ctx, IReadOnlyList<string>? paths)
    {
        var roots = new List<string>();
        if (paths is null || paths.Count == 0)
        {
            var flags = ctx.Resolve(ProjectInfo.DefaultInputDir);
            roots.Add(Directory.Exists(flags) ? flags : ctx.WorkingDirectory);
        }
        else
        {
            roots.AddRange(paths.Select(ctx.Resolve));
        }

        var files = new List<string>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root))
                files.AddRange(Enumerate(root));
            else if (File.Exists(root))
                files.Add(root);
            else
                return ([], roots, $"Path not found: {ctx.Display(root)}");
        }
        var distinct = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return (distinct, roots, null);
    }

    private static IEnumerable<string> Enumerate(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.flag"))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(sub)))
                continue;
            foreach (var file in Enumerate(sub))
                yield return file;
        }
    }

    /// <summary>Reads the files as compiler sources; paths are shown relative to the working directory.</summary>
    public static List<SourceFile> Load(CliContext ctx, IEnumerable<string> files) =>
        files.Select(f => new SourceFile(ctx.Display(f), File.ReadAllText(f))).ToList();
}
