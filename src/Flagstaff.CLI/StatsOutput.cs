using System.Globalization;
using System.Text.Json.Nodes;
using Flagstaff.Compiler;

namespace Flagstaff.Cli;

/// <summary>Renders <see cref="CompilationStats"/> as the envelope's <c>stats</c> object and as text lines.</summary>
internal static class StatsOutput
{
    public static JsonObject ToJson(CompilationStats s) => new()
    {
        ["flagLines"] = s.FlagLines,
        ["escapeBlocks"] = s.EscapeBlocks,
        ["escapeLines"] = s.EscapeLines,
        ["escapeRatio"] = Math.Round(s.EscapeRatio, 4),
        ["keywordElements"] = s.KeywordElements,
        ["genericElements"] = s.GenericElements,
        ["genericRatio"] = Math.Round(s.GenericRatio, 4),
        ["files"] = new JsonArray(s.Files.Select(f => (JsonNode)new JsonObject
        {
            ["file"] = f.File,
            ["flagLines"] = f.FlagLines,
            ["escapeLines"] = f.EscapeLines,
            ["generic"] = f.GenericElements,
            ["keyword"] = f.KeywordElements,
        }).ToArray()),
        ["escape"] = new JsonArray(s.EscapeBlocksDetail.Select(b => (JsonNode)new JsonObject
        {
            ["file"] = b.File,
            ["line"] = b.Line,
            ["lines"] = b.Lines,
            ["reasons"] = Output.ToArray(b.Reasons),
            ["rootTypes"] = Output.ToArray(b.RootTypes),
            ["findings"] = Output.ToArray(b.Findings),
        }).ToArray()),
    };

    /// <summary>
    /// <c>stats: 412 flag lines, 1 xaml block (24 lines, 5.8%), 61 elements (0 generic)</c>, then one line per
    /// <c>xaml { }</c> block with its reasons.
    /// </summary>
    public static IEnumerable<string> ToText(CompilationStats s)
    {
        var elements = s.KeywordElements + s.GenericElements;
        yield return string.Create(CultureInfo.InvariantCulture,
            $"stats: {Plural(s.FlagLines, "flag line")}, {Plural(s.EscapeBlocks, "xaml block")} " +
            $"({Plural(s.EscapeLines, "line")}, {s.EscapeRatio * 100:0.0}%), " +
            $"{Plural(elements, "element")} ({s.GenericElements} generic)");
        foreach (var b in s.EscapeBlocksDetail)
        {
            var types = b.RootTypes.Count == 0 ? "" : $" [{string.Join(", ", b.RootTypes)}]";
            yield return $"  xaml {b.File}({b.Line}): {string.Join(", ", b.Reasons)}{types}";
        }
    }

    /// <summary>Adds the <c>stats</c> envelope field and the text summary lines to a command result.</summary>
    public static void Attach(CommandResult result, CompilationStats stats)
    {
        result.Extra["stats"] = ToJson(stats);
        result.TextLines.AddRange(ToText(stats));
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
