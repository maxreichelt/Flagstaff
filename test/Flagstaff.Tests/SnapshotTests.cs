using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Snapshot tests: every directory under <c>Cases/</c> is one compilation case
/// (an <c>app.flag</c> plus one or more view <c>*.flag</c> files). Each generated
/// output file and the case's diagnostics are verified against a committed
/// <c>Snapshots/*.verified.*</c> file.
/// One extra case, <c>tasktrack</c>, reads <c>samples/TaskTrack/Flags</c> in place
/// (no copy), so the sample and its snapshots cannot drift apart.
/// </summary>
public class SnapshotTests
{
    private static string CasesDir =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Cases");

    internal static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    internal static string TaskTrackDir => Path.Combine(RepoRoot, "samples", "TaskTrack");

    /// <summary>Case name of the TaskTrack sample (snapshots: <c>Snapshots/tasktrack.*</c>).</summary>
    internal const string TaskTrackCase = "tasktrack";

    private static string CaseDir(string caseName) =>
        caseName == TaskTrackCase ? Path.Combine(TaskTrackDir, "Flags") : Path.Combine(CasesDir, caseName);

    private static readonly ConcurrentDictionary<string, CaseOutput> Compiled = new();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in DiscoverCases())
            data.Add(name);
        return data;
    }

    public static TheoryData<string, string> OutputFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in DiscoverCases())
        {
            foreach (var file in Compile(name).Files)
            {
                // GlobalUsings.g.cs is the same in every case (only the namespace differs): snapshot it once.
                if (file.Name == "GlobalUsings.g.cs" && name != TaskTrackCase)
                    continue;
                data.Add(name, file.Name);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(OutputFiles))]
    public Task Output_file_matches_snapshot(string caseName, string outputFileName)
    {
        var file = Compile(caseName).Files.Single(f => f.Name == outputFileName);
        var extension = Path.GetExtension(outputFileName).TrimStart('.');
        var baseName = Path.GetFileNameWithoutExtension(outputFileName);

        return Verify(file.Content, extension)
            .UseDirectory("Snapshots")
            .UseFileName($"{caseName}.{baseName}");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Diagnostics_match_snapshot(string caseName)
    {
        var diagnostics = Compile(caseName).Diagnostics;
        var text = new StringBuilder();
        if (diagnostics.Count == 0)
            text.Append("(no diagnostics)\n");
        foreach (var d in diagnostics)
            text.Append($"{d.Severity} {d.Location}: {d.Message}\n");

        return Verify(text.ToString(), "txt")
            .UseDirectory("Snapshots")
            .UseFileName($"{caseName}.diagnostics");
    }

    [Fact]
    public void Every_case_has_an_app_and_at_least_one_view()
    {
        var cases = DiscoverCases();
        Assert.NotEmpty(cases);
        foreach (var name in cases)
        {
            var dir = CaseDir(name);
            Assert.True(File.Exists(Path.Combine(dir, "app.flag")), $"{name}: missing app.flag");
            Assert.NotEmpty(ViewFiles(dir));
        }
    }

    private static List<string> DiscoverCases() =>
        Directory.GetDirectories(CasesDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Append(TaskTrackCase)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> ViewFiles(string caseDir) =>
        Directory.GetFiles(caseDir, "*.flag")
            .Where(f => !string.Equals(Path.GetFileName(f), "app.flag", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static CaseOutput Compile(string caseName) =>
        Compiled.GetOrAdd(caseName, static name =>
        {
            var dir = CaseDir(name);
            var compiler = new FlagstaffCompiler();
            var output = new CaseOutput();

            // App first: it sets the namespace used by every view.
            output.Add(compiler.Compile(File.ReadAllText(Path.Combine(dir, "app.flag")), "app.flag"));

            foreach (var view in ViewFiles(dir))
                output.Add(compiler.Compile(File.ReadAllText(view), Path.GetFileName(view)));

            output.Add(compiler.GenerateAppScaffold());
            return output;
        });

    private sealed class CaseOutput
    {
        public List<OutputFile> Files { get; } = [];
        public List<Diagnostic> Diagnostics { get; } = [];

        public void Add(CompilationResult result)
        {
            Files.AddRange(result.Files);
            Diagnostics.AddRange(result.Diagnostics);
        }
    }
}

public static class SnapshotModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Never pop a diff tool (CI, agents, headless runs); failures leave *.received.* files.
        DiffEngine.DiffRunner.Disabled = true;

        // Generated code must be snapshotted verbatim.
        VerifierSettings.DontScrubDateTimes();
        VerifierSettings.DontScrubGuids();
    }
}
