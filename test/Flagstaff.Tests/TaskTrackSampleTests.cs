using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Checks over the TaskTrack sample (samples/TaskTrack). Its generated output is snapshotted by
/// <see cref="SnapshotTests"/> (case <c>tasktrack</c>); these tests cover what a snapshot cannot:
/// the sample compiles cleanly the way the build does, and its hand-written ViewModels implement
/// every partial the generator declares.
/// </summary>
public partial class TaskTrackSampleTests
{
    private static CompilationResult CompileSample()
    {
        var flags = Directory.GetFiles(Path.Combine(SnapshotTests.TaskTrackDir, "Flags"), "*.flag")
            .Order(StringComparer.Ordinal)
            .Select(f => new SourceFile(Path.GetFileName(f), File.ReadAllText(f)));
        return new FlagstaffCompiler().CompileAll(flags);
    }

    [Fact]
    public void Sample_compiles_with_zero_diagnostics()
    {
        var result = CompileSample();

        Assert.False(result.HasErrors);
        Assert.True(result.Diagnostics.Count == 0,
            string.Join("\n", result.Diagnostics.Select(d => d.ToMsBuildString())));
        Assert.Contains(result.Files, f => f.Name == "MainWindow.xaml");
        Assert.Equal(5, result.Files.Count(f => f.Name.EndsWith("ViewModel.g.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void Sample_viewmodels_implement_every_generated_partial()
    {
        var generated = CompileSample().Files
            .Where(f => f.Name.EndsWith("ViewModel.g.cs", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(generated);

        var handWritten = string.Join("\n",
            Directory.GetFiles(Path.Combine(SnapshotTests.TaskTrackDir, "ViewModels"), "*.cs")
                .Select(File.ReadAllText));

        var declared = new List<string>();
        var missing = new List<string>();
        foreach (var file in generated)
        {
            foreach (Match m in PartialDeclaration().Matches(file.Content))
            {
                var method = m.Groups["name"].Value;
                declared.Add(method);
                var implementation = new Regex($@"private\s+partial\s+(async\s+)?Task\s+{method}\(");
                if (!implementation.IsMatch(handWritten))
                    missing.Add($"{Path.GetFileNameWithoutExtension(file.Name)}: {m.Value.Trim()}");
            }
        }

        Assert.NotEmpty(declared);
        Assert.True(missing.Count == 0, "Not implemented in samples/TaskTrack/ViewModels:\n" + string.Join("\n", missing));
    }

    // private partial Task OnSaveAsync();  /  private partial Task OnEditAsync(TaskRow item);
    [GeneratedRegex(@"private partial Task (?<name>On\w+Async)\([^)]*\);")]
    private static partial Regex PartialDeclaration();
}
