using System.Text.Json.Nodes;

namespace Flagstaff.Cli.Tests;

/// <summary>The <c>stats</c> envelope field, the text summary line, and <c>check --stats-only</c>.</summary>
public class StatsCommandTests
{
    private const string Escapes = """
        view ChartsView {
            bind vm: ChartsViewModel;
            heading "Charts";

            xaml {
                <TreeView />
            }

            xaml {
                <lvc:CartesianChart xmlns:lvc="using:LiveChartsCore.SkiaSharpView.WinUI"
                    Series="{x:Bind ViewModel.Series}" />
            }
        }
        """;

    private static TempProject Project()
    {
        var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/charts.flag", Escapes);
        return p;
    }

    private static void AssertStats(JsonObject json)
    {
        var stats = json["stats"]!.AsObject();
        Assert.Equal(2, (int)stats["escapeBlocks"]!);
        Assert.Equal(3, (int)stats["escapeLines"]!);
        Assert.Equal(1, (int)stats["keywordElements"]!);
        Assert.Equal(0, (int)stats["genericElements"]!);
        Assert.Equal(0, (double)stats["genericRatio"]!);
        var flagLines = (int)stats["flagLines"]!;
        Assert.True(flagLines > 10);
        Assert.Equal(Math.Round(3.0 / flagLines, 4), (double)stats["escapeRatio"]!);

        var files = stats["files"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(2, files.Count);
        var charts = Assert.Single(files, f => ((string?)f["file"])!.EndsWith("charts.flag", StringComparison.Ordinal));
        Assert.Equal(3, (int)charts["escapeLines"]!);
        Assert.Equal(1, (int)charts["keyword"]!);
        Assert.Equal(0, (int)charts["generic"]!);

        var escape = stats["escape"]!.AsArray().Select(n => n!.AsObject()).ToList();
        Assert.Equal(2, escape.Count);
        Assert.Equal(5, (int)escape[0]["line"]!);
        Assert.Equal(["expressible"], escape[0]["reasons"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["TreeView"], escape[0]["rootTypes"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(9, (int)escape[1]["line"]!);
        Assert.Equal(["unknown-type"], escape[1]["reasons"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["lvc:CartesianChart"], escape[1]["rootTypes"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void Check_envelope_has_stats_and_fs3011()
    {
        using var p = Project();
        var run = p.Run("check", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        var json = run.Json();
        AssertStats(json);
        var d = json["diagnostics"]!.AsArray().Select(n => n!.AsObject()).Single(n => (string?)n["code"] == "FS3011");
        Assert.Equal("warning", (string?)d["severity"]);
        Assert.Equal(5, (int)d["line"]!);
    }

    [Fact]
    public void Compile_envelope_has_stats()
    {
        using var p = Project();
        var run = p.Run("compile", "--format", "json");

        Assert.Equal(0, run.ExitCode);
        AssertStats(run.Json());
    }

    [Fact]
    public void Text_mode_prints_summary_after_diagnostics()
    {
        using var p = Project();
        var run = p.Run("check");

        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var summary = lines.FindIndex(l => l.StartsWith("stats: ", StringComparison.Ordinal));
        Assert.True(summary > 0, run.Stdout);
        Assert.Contains("warning FS3011", lines[summary - 1]);
        Assert.Matches(@"^stats: \d+ flag lines, 2 xaml blocks \(3 lines, \d+\.\d%\), 1 element \(0 generic\)$", lines[summary]);
        Assert.Matches(@"^  xaml .*charts\.flag\(5\): expressible \[TreeView\]$", lines[summary + 1]);
        Assert.Matches(@"^  xaml .*charts\.flag\(9\): unknown-type \[lvc:CartesianChart\]$", lines[summary + 2]);
    }

    [Fact]
    public void Stats_only_prints_just_the_stats()
    {
        using var p = Project();
        var run = p.Run("check", "--stats-only");

        Assert.Equal(0, run.ExitCode);
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("stats: ", lines[0]);
        Assert.DoesNotContain("FS3011", run.Stdout);

        var json = p.Run("check", "--stats-only", "--format", "json").Json();
        AssertStats(json);
        Assert.Empty(json["diagnostics"]!.AsArray());
    }

    [Fact]
    public void Clean_project_reports_zero_escapes()
    {
        using var p = new TempProject();
        p.Write("Flags/app.flag", Samples.App);
        p.Write("Flags/home.flag", Samples.Home);

        var stats = p.Run("check", "--format", "json").Json()["stats"]!.AsObject();
        Assert.Equal(0, (int)stats["escapeBlocks"]!);
        Assert.Equal(0, (double)stats["escapeRatio"]!);
        Assert.Empty(stats["escape"]!.AsArray());
        Assert.True((int)stats["keywordElements"]! > 0);
    }
}
