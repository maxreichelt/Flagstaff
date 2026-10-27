namespace Flagstaff.Cli;

/// <summary>
/// Embedded file templates (<c>Templates/**/*.tmpl</c>, logical name <c>Templates/&lt;path&gt;</c>).
/// <c>{{Key}}</c> placeholders are substituted; in paths, <c>__Key__</c> is substituted and a leading
/// <c>dot_</c> on a segment becomes <c>.</c> (so dotfiles survive packing).
/// </summary>
internal static class Templates
{
    private const string Prefix = "Templates/";

    /// <summary>Template paths (relative to <c>Templates/</c>, forward slashes, without <c>.tmpl</c>) under <paramref name="folder"/>.</summary>
    public static IEnumerable<string> List(string folder)
    {
        var start = Prefix + folder.TrimEnd('/') + "/";
        return typeof(Templates).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(start, StringComparison.Ordinal) && n.EndsWith(".tmpl", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^".tmpl".Length])
            .Order(StringComparer.Ordinal);
    }

    public static string Read(string path, IReadOnlyDictionary<string, string> values)
    {
        var assembly = typeof(Templates).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/') == Prefix + path + ".tmpl")
            ?? throw new InvalidOperationException($"Template {path} is not embedded.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Substitute(reader.ReadToEnd(), values);
    }

    public static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Maps a template path (relative to its folder) to the output path.</summary>
    public static string OutputPath(string relative, IReadOnlyDictionary<string, string> values)
    {
        var segments = relative.Split('/').Select(s =>
        {
            foreach (var (key, value) in values)
                s = s.Replace("__" + key + "__", value, StringComparison.Ordinal);
            return s.StartsWith("dot_", StringComparison.Ordinal) ? "." + s["dot_".Length..] : s;
        });
        return Path.Combine(segments.ToArray());
    }
}
