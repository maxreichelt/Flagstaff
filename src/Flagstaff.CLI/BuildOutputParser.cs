using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flagstaff.Cli;

/// <summary>Turns MSBuild console output and WinUI XamlCompiler <c>output.json</c> files into diagnostics.</summary>
internal static partial class BuildOutputParser
{
    // path(line,col): error CODE: message [project]   (also (line), (line,col,endLine,endCol), "12>" node prefixes)
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<file>[^\s].*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?:(?<sub>[^:]*?)\s+)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*)$")]
    private static partial Regex LocatedLine();

    // origin : error CODE: message   (MSBUILD : error MSB1009, CSC : error CS2001, EXEC : error ...)
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<file>[^\s(:][^(:]*?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*)$")]
    private static partial Regex UnlocatedLine();

    [GeneratedRegex(@"\s+\[(?<proj>[^\[\]]+\.(?:cs|vb|fs|vcx|wap)?proj(?:::[^\]]*)?)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectSuffix();

    [GeneratedRegex(@"\s*\[hint:\s*(?<hint>.*)\]\s*$")]
    private static partial Regex HintSuffix();

    /// <summary>Parses one canonical MSBuild diagnostic line, or returns null.</summary>
    public static CliDiagnostic? ParseLine(string line)
    {
        var m = LocatedLine().Match(line);
        string? file;
        int? lineNo = null, col = null;
        if (m.Success)
        {
            file = m.Groups["file"].Value.Trim();
            lineNo = int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (m.Groups["col"].Success)
                col = int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            m = UnlocatedLine().Match(line);
            if (!m.Success)
                return null;
            file = m.Groups["file"].Value.Trim();
        }

        var message = ProjectSuffix().Replace(m.Groups["msg"].Value, "").Trim();
        string? hint = null;
        var h = HintSuffix().Match(message);
        if (h.Success)
        {
            hint = h.Groups["hint"].Value.Trim();
            message = message[..h.Index].TrimEnd();
        }
        return new CliDiagnostic(m.Groups["sev"].Value, m.Groups["code"].Value, message, file, lineNo, col, hint);
    }

    /// <summary>Parses all diagnostic lines, dropping the duplicates MSBuild prints in its summary.</summary>
    public static List<CliDiagnostic> ParseLines(IEnumerable<string> lines)
    {
        var seen = new HashSet<CliDiagnostic>();
        var result = new List<CliDiagnostic>();
        foreach (var line in lines)
        {
            if (ParseLine(line) is { } d && seen.Add(d))
                result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// Reads the <c>MSBuildLogEntries</c> of a XamlCompiler <c>output.json</c>. Entries carrying an error code or a
    /// file are reported (progress markers are skipped); severity comes from an <c>EntryType</c>/<c>Type</c> field
    /// when present, else entries with a code are errors.
    /// </summary>
    public static List<CliDiagnostic> ParseXamlCompilerOutput(string json)
    {
        var result = new List<CliDiagnostic>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("MSBuildLogEntries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                continue;
            var code = GetString(e, "ErrorCode");
            var file = GetString(e, "File");
            var message = GetString(e, "Message") ?? "";
            if (code is null && file is null)
                continue;
            if (message.StartsWith("Xaml Compiler Marker", StringComparison.Ordinal))
                continue;

            var type = (GetString(e, "EntryType") ?? GetString(e, "Type") ?? GetString(e, "Severity"))?.ToLowerInvariant();
            var severity = type switch
            {
                not null when type.Contains("warn", StringComparison.Ordinal) => "warning",
                not null when type.Contains("message", StringComparison.Ordinal) || type.Contains("info", StringComparison.Ordinal) => "info",
                _ => "error",
            };
            var line = GetInt(e, "LineNumber");
            var col = GetInt(e, "ColumnNumber");
            result.Add(new CliDiagnostic(severity, code, message, file, line > 0 ? line : null, col > 0 ? col : null, null));
        }
        return result;
    }

    /// <summary>The newest <c>obj/**/output.json</c> under a project written at or after <paramref name="since"/>.</summary>
    public static string? FindXamlCompilerOutput(string projectDirectory, DateTime since)
    {
        var obj = Path.Combine(projectDirectory, "obj");
        if (!Directory.Exists(obj))
            return null;
        return Directory.EnumerateFiles(obj, "output.json", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTimeUtc >= since.ToUniversalTime().AddSeconds(-2))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
