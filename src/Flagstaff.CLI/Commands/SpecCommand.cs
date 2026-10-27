using System.CommandLine;

namespace Flagstaff.Cli.Commands;

/// <summary><c>spec [--section Name] [--list]</c>: prints the embedded language reference (LANG.md).</summary>
internal static class SpecCommand
{
    public const string ResourceName = "Flagstaff.LANG.md";

    public static Command Create(CliHost host)
    {
        var section = new Option<string>("--section") { Description = "Print one '##' section (case-insensitive; prefix or substring match)" };
        var list = new Option<bool>("--list") { Description = "List the section names" };
        var cmd = new Command("spec", "Print the Flagstaff language reference (LANG.md) embedded in this tool");
        cmd.Options.Add(section);
        cmd.Options.Add(list);
        host.Bind(cmd, (pr, ctx) => Run(ctx, pr.GetValue(section), pr.GetValue(list)));
        return cmd;
    }

    public static string LoadSpec()
    {
        using var stream = typeof(SpecCommand).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static CommandResult Run(CliContext ctx, string? sectionName, bool list)
    {
        var result = new CommandResult("spec");
        var spec = LoadSpec();
        var sections = Split(spec);
        result.Extra["sections"] = Output.ToArray(sections.Select(s => s.Title));

        if (list)
        {
            result.TextLines.AddRange(sections.Select(s => s.Title));
            return result;
        }

        if (sectionName is null)
        {
            result.Extra["content"] = spec;
            result.TextLines.Add(spec.TrimEnd());
            return result;
        }

        var matches =
            sections.Where(s => s.Title.Equals(sectionName, StringComparison.OrdinalIgnoreCase))
            .Concat(sections.Where(s => s.Title.StartsWith(sectionName, StringComparison.OrdinalIgnoreCase)))
            .Concat(sections.Where(s => s.Title.Contains(sectionName, StringComparison.OrdinalIgnoreCase)))
            .Take(1).ToList();
        if (matches.Count == 0)
        {
            return result.Fail($"No section matches '{sectionName}'.", ExitCodes.Failed,
                "Sections: " + string.Join(", ", sections.Select(s => s.Title)));
        }

        var (title, content) = matches[0];
        result.Extra["section"] = title;
        result.Extra["content"] = content;
        result.TextLines.Add(content.TrimEnd());
        return result;
    }

    /// <summary>Splits markdown into <c>## </c> sections (headings inside code fences are ignored).</summary>
    public static List<(string Title, string Content)> Split(string markdown)
    {
        var sections = new List<(string Title, string Content)>();
        var lines = markdown.Split('\n');
        var inFence = false;
        string? title = null;
        var start = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;
            if (inFence || !line.StartsWith("## ", StringComparison.Ordinal))
                continue;
            if (title is not null)
                sections.Add((title, string.Join('\n', lines[start..i]).TrimEnd() + "\n"));
            title = line[3..].Trim();
            start = i;
        }
        if (title is not null)
            sections.Add((title, string.Join('\n', lines[start..]).TrimEnd() + "\n"));
        return sections;
    }
}
