using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Drift guards between LANG.md (shipped as <c>flagstaff spec</c>) and the compiler: every keyword, prop and
/// diagnostic code is documented, and every <c>```flag</c> example compiles as documented.
/// <para>
/// Fence conventions in LANG.md:
/// <list type="bullet">
/// <item><c>```flag</c>: must compile with no errors and no warnings.</item>
/// <item><c>```flag warn</c>: must compile with no errors (warnings allowed).</item>
/// <item><c>```flag wrong FS1234</c>: the part before a <c>// RIGHT</c> line must report FS1234; the part after it
/// (if any) must compile with no errors and no warnings.</item>
/// </list>
/// A block without an <c>app</c>/<c>view</c> declaration is a fragment and is compiled inside
/// <c>view DocView { bind vm: DocViewModel; ... }</c>.
/// </para>
/// </summary>
public partial class LangDocTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string LangMd = File.ReadAllText(Path.Combine(RepoRoot, "LANG.md")).ReplaceLineEndings("\n");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(File.Exists(Path.Combine(dir.FullName, "LANG.md")) && File.Exists(Path.Combine(dir.FullName, "Flagstaff.slnx"))))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (LANG.md + Flagstaff.slnx).");
    }

    // ── (a) keywords ───────────────────────────────────────────────

    public static TheoryData<string> Keywords() => new(ElementSchema.All.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Keywords))]
    public void Every_keyword_is_documented_as_code(string keyword)
    {
        var pattern = new Regex("`" + Regex.Escape(keyword) + @"[\s;`]");
        Assert.True(pattern.IsMatch(LangMd), $"LANG.md never shows `{keyword}` as code.");
    }

    // ── (b) props ──────────────────────────────────────────────────

    public static TheoryData<string> Props() => new(
        ElementSchema.All.Values.SelectMany(s => s.Props.Keys)
            .Concat(ElementSchema.Universal.Keys)
            .Concat(ElementSchema.Theme.Keys)
            .Distinct()
            .Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Props))]
    public void Every_prop_is_documented(string prop)
    {
        var spec = ElementSchema.Universal.TryGetValue(prop, out var u) ? u
            : ElementSchema.Theme.TryGetValue(prop, out var t) ? t
            : ElementSchema.All.Values.Select(s => s.Props.GetValueOrDefault(prop)).First(p => p is not null)!;
        // Flags are written bare (`bold`), everything else as `key:`.
        var pattern = spec.Bare
            ? new Regex("`" + Regex.Escape(prop) + "`|[ (`]" + Regex.Escape(prop) + "[;` ]")
            : new Regex("`" + Regex.Escape(prop) + ":");
        Assert.True(pattern.IsMatch(LangMd), $"LANG.md never documents the prop '{prop}'.");
    }

    // ── (c) diagnostic codes ───────────────────────────────────────

    [GeneratedRegex("\"(FS\\d{4})\"")]
    private static partial Regex CodeLiteral();

    private static List<string> CodeList()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Flagstaff.Compiler");
        return Directory.EnumerateFiles(dir, "*.cs")
            .SelectMany(f => CodeLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static TheoryData<string> Codes() => new(CodeList());

    [Fact]
    public void Compiler_uses_diagnostic_codes()
    {
        Assert.True(CodeList().Count >= 30);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_diagnostic_code_is_in_the_diagnostics_table(string code)
    {
        var section = Section("Diagnostics");
        Assert.Matches(new Regex(@"^\| " + code + @" \| (error|warning) \| ", RegexOptions.Multiline), section);
    }

    [Fact]
    public void Diagnostics_table_lists_no_unknown_codes()
    {
        var used = CodeList().ToHashSet();
        var documented = Regex.Matches(Section("Diagnostics"), @"^\| (FS\d{4}) \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value);
        Assert.DoesNotContain(documented, c => !used.Contains(c));
    }

    // ── (d) examples ───────────────────────────────────────────────

    public sealed record FlagBlock(int Line, string Info, string Code)
    {
        public override string ToString() => $"LANG.md:{Line} ```{Info}";
    }

    private static List<FlagBlock> FlagBlocks()
    {
        var blocks = new List<FlagBlock>();
        var lines = LangMd.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("```flag", StringComparison.Ordinal))
                continue;
            var start = i;
            var body = new List<string>();
            for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
                body.Add(lines[i]);
            blocks.Add(new FlagBlock(start + 1, lines[start][3..].Trim(), string.Join('\n', body)));
        }
        return blocks;
    }

    public static TheoryData<FlagBlock> Blocks() => new(FlagBlocks());

    [Fact]
    public void Lang_md_has_flag_examples()
    {
        var blocks = FlagBlocks();
        Assert.True(blocks.Count(b => b.Info == "flag") >= 10);
        Assert.True(blocks.Count(b => b.Info.StartsWith("flag wrong ", StringComparison.Ordinal)) >= 20);
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void Flag_example_compiles_as_documented(FlagBlock block)
    {
        var info = block.Info.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (info)
        {
            case ["flag"]:
                AssertClean(block.Code, allowWarnings: false);
                break;
            case ["flag", "warn"]:
                AssertClean(block.Code, allowWarnings: true);
                break;
            case ["flag", "wrong", var code]:
                var parts = Regex.Split(block.Code, @"^// RIGHT.*$", RegexOptions.Multiline);
                Assert.True(parts.Length <= 2, "a wrong block has at most one // RIGHT line");
                var wrong = Compile(parts[0]);
                Assert.True(wrong.Diagnostics.Any(d => d.Code == code),
                    $"expected {code}, got: {Describe(wrong)}");
                if (parts.Length == 2)
                    AssertClean(parts[1], allowWarnings: false);
                break;
            default:
                Assert.Fail($"Unknown fence info '{block.Info}'; use flag, flag warn, or flag wrong FSxxxx.");
                break;
        }
    }

    [Fact]
    public void Readme_example_compiles_clean()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md")).ReplaceLineEndings("\n");
        var blocks = Regex.Matches(readme, @"^```flag\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.NotEmpty(blocks);
        foreach (Match block in blocks)
            AssertClean(block.Groups[1].Value, allowWarnings: false);
    }

    [Fact]
    public void Every_wrong_example_names_a_documented_code()
    {
        var table = Section("Diagnostics");
        foreach (var block in FlagBlocks().Where(b => b.Info.StartsWith("flag wrong ", StringComparison.Ordinal)))
            Assert.Contains("| " + block.Info.Split(' ')[2] + " |", table);
    }

    [GeneratedRegex(@"^\s*(app|view)\s+\w+\s*\{", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    private static CompilationResult Compile(string code)
    {
        var source = Declaration().IsMatch(code)
            ? code
            : "view DocView {\n    bind vm: DocViewModel;\n" + code + "\n}\n";
        return new FlagstaffCompiler().CompileAll([new SourceFile("doc.flag", source)]);
    }

    private static void AssertClean(string code, bool allowWarnings)
    {
        var result = Compile(code);
        var problems = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error || !allowWarnings)
            .ToList();
        Assert.True(problems.Count == 0, Describe(result));
        Assert.NotEmpty(result.Files);
    }

    private static string Describe(CompilationResult result) =>
        result.Diagnostics.Count == 0 ? "no diagnostics" : string.Join("\n", result.Diagnostics.Select(d => d.ToMsBuildString()));

    // ── spec sections ──────────────────────────────────────────────

    private static string Section(string title)
    {
        var match = Regex.Match(LangMd, @"^## " + Regex.Escape(title) + @"\n(.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"LANG.md has no '## {title}' section.");
        return match.Groups[1].Value;
    }

    [Fact]
    public void Sections_are_the_stable_spec_keys()
    {
        var titles = Regex.Matches(LangMd, @"^## (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(
        [
            "Overview", "Universal Element Form", "App", "View", "Layout", "Display", "Inputs", "Buttons and Commands",
            "Tables", "Lists", "Overlays", "Conditions", "Universal Props", "Format", "Type Inference",
            "ViewModel Conventions", "Navigation", "Generic Elements", "Pass-Through Props", "Locality Rules", "Events",
            "Resources and States", "XAML Escape", "Diagnostics", "Common Mistakes", "CLI", "Full Example",
        ], titles);
    }

    // ── generic elements and the control manifest ──────────────────

    private static JsonObject Overlay() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "Flagstaff.Compiler", "Manifest", "overlay.json")))!.AsObject();

    public static TheoryData<string, string> KeywordEquivalents()
    {
        var data = new TheoryData<string, string>();
        foreach (var (type, keyword) in Overlay()["keywordFor"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
            data.Add(type, (string)keyword!);
        return data;
    }

    [Theory]
    [MemberData(nameof(KeywordEquivalents))]
    public void Every_keyword_equivalent_is_a_documented_keyword(string type, string keyword)
    {
        Assert.True(ElementSchema.All.ContainsKey(keyword), $"overlay.json maps {type} to '{keyword}', which is not a keyword.");
        Assert.Contains($"`{type}` → `{keyword}`", Section("Generic Elements"));
    }

    public static TheoryData<string> AttachedFamilies() => new(
        Overlay()["attachedLocality"]!.AsObject().Select(p => p.Key[..p.Key.IndexOf('.')]).Distinct().Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(AttachedFamilies))]
    public void Every_attached_property_family_is_in_the_locality_section(string family)
    {
        Assert.Contains($"`{family}.", Section("Locality Rules"));
    }

    [Fact]
    public void Locality_section_names_the_core_attached_families()
    {
        var section = Section("Locality Rules");
        foreach (var family in new[] { "`AutomationProperties.*`", "`ToolTipService.*`", "`Canvas.*`", "`ScrollViewer.*`", "`Grid.Row`", "`RelativePanel." })
            Assert.Contains(family, section);
    }
}
