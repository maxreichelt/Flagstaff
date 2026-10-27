using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flagstaff.Compiler;

namespace Flagstaff.Cli;

/// <summary>Exit codes shared by every command.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;       // diagnostics or build errors
    public const int Usage = 2;        // bad arguments, missing project, ...
    public const int Environment = 3;  // missing SDK / MSBuild / components
    public const int Crash = 4;        // internal error
}

/// <summary>A diagnostic in the CLI envelope: from the Flagstaff compiler or parsed from build output.</summary>
internal sealed record CliDiagnostic(
    string Severity,
    string? Code,
    string Message,
    string? File,
    int? Line,
    int? Column,
    string? Hint)
{
    public static CliDiagnostic From(Diagnostic d) => new(
        d.Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        },
        d.Code, d.Message, d.Location.File, d.Location.Line, d.Location.Column, d.Hint);

    public bool IsError => Severity == "error";

    /// <summary>Canonical MSBuild form: <c>path(line,col): error CODE: message [hint: ...]</c>.</summary>
    public string ToMsBuildString()
    {
        var location = File is null
            ? "flagstaff"
            : Line is null ? File : Column is null ? $"{File}({Line})" : $"{File}({Line},{Column})";
        var code = Code is null ? "" : $" {Code}";
        var hint = Hint is null ? "" : $" [hint: {Hint}]";
        return $"{location}: {Severity}{code}: {Message}{hint}";
    }

    public JsonObject ToJson() => new()
    {
        ["severity"] = Severity,
        ["code"] = Code,
        ["message"] = Message,
        ["file"] = File,
        ["line"] = Line,
        ["column"] = Column,
        ["hint"] = Hint,
    };
}

/// <summary>What a command produced; rendered by <see cref="Output.Emit"/> as text or as the JSON envelope.</summary>
internal sealed class CommandResult(string command)
{
    public string Command { get; } = command;
    public bool Ok { get; set; } = true;
    private int? _exitCode;

    /// <summary>Defaults to 0 when <see cref="Ok"/>, else 1.</summary>
    public int ExitCode
    {
        get => _exitCode ?? (Ok ? ExitCodes.Ok : ExitCodes.Failed);
        set => _exitCode = value;
    }

    public List<CliDiagnostic> Diagnostics { get; } = [];
    public List<string> Outputs { get; } = [];

    /// <summary>Command-specific envelope fields, in insertion order.</summary>
    public JsonObject Extra { get; } = new();

    /// <summary>Result lines written to stdout in text mode (after the diagnostics).</summary>
    public List<string> TextLines { get; } = [];

    /// <summary>A non-diagnostic failure (bad path, missing tool, ...).</summary>
    public string? Error { get; set; }
    public string? Hint { get; set; }

    /// <summary>Emit the JSON envelope on one line (watch writes JSON Lines).</summary>
    public bool CompactJson { get; set; }

    public int ErrorCount => Diagnostics.Count(d => d.IsError);
    public int WarningCount => Diagnostics.Count(d => d.Severity == "warning");

    public void AddDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        Diagnostics.AddRange(diagnostics.Select(CliDiagnostic.From));

    public CommandResult Fail(string error, int exitCode, string? hint = null)
    {
        Ok = false;
        Error = error;
        ExitCode = exitCode;
        Hint ??= hint;
        return this;
    }

    public static CommandResult Usage(string command, string error) =>
        new CommandResult(command).Fail(error, ExitCodes.Usage);
}

internal static class Output
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions CompactJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Envelope(CommandResult r)
    {
        var o = new JsonObject
        {
            ["tool"] = "flagstaff",
            ["version"] = Cli.Version,
            ["command"] = r.Command,
            ["ok"] = r.Ok,
            ["exitCode"] = r.ExitCode,
            ["diagnostics"] = new JsonArray(r.Diagnostics.Select(d => (JsonNode)d.ToJson()).ToArray()),
            ["outputs"] = ToArray(r.Outputs),
        };
        if (r.Error is not null)
            o["error"] = r.Error;
        if (r.Hint is not null)
            o["hint"] = r.Hint;
        foreach (var (key, value) in r.Extra)
            o[key] = value?.DeepClone();
        return o;
    }

    public static void Emit(CliContext ctx, CommandResult r)
    {
        if (ctx.Json)
        {
            ctx.Out.WriteLine(Envelope(r).ToJsonString(r.CompactJson ? CompactJsonOptions : JsonOptions));
            return;
        }

        foreach (var d in r.Diagnostics)
            ctx.Out.WriteLine(d.ToMsBuildString());
        foreach (var line in r.TextLines)
            ctx.Out.WriteLine(line);
        if (r.Error is not null)
            ctx.Err.WriteLine($"error: {r.Error}");
        if (r.Hint is not null)
            ctx.Err.WriteLine($"hint: {r.Hint}");
    }

    public static void EmitCrash(CliContext ctx, string command, Exception ex)
    {
        ctx.Err.WriteLine($"flagstaff: internal error in '{command}':");
        ctx.Err.WriteLine(ex.ToString());
        if (ctx.Json)
        {
            var o = new JsonObject
            {
                ["tool"] = "flagstaff",
                ["version"] = Cli.Version,
                ["command"] = command,
                ["ok"] = false,
                ["exitCode"] = ExitCodes.Crash,
                ["crash"] = $"{ex.GetType().Name}: {ex.Message}",
            };
            ctx.Out.WriteLine(o.ToJsonString(JsonOptions));
        }
    }

    public static JsonArray ToArray(IEnumerable<string> items) =>
        new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
}
