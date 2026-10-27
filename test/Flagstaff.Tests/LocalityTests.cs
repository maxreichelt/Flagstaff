using System.Text.RegularExpressions;
using Flagstaff.Compiler;
using static Flagstaff.Tests.GenericTests;

namespace Flagstaff.Tests;

/// <summary>
/// The locality rule (docs/universal-element.md, section 1 and decision 6): an attribute passes through only when its
/// correctness is local to the element. Grid placement is checked against the enclosing generic Grid (FS1038), element
/// references must be <c>ref(Name)</c> (FS1040) and resolve (FS1039), and attached properties outside the overlay
/// allowlist are rejected (FS1041).
/// </summary>
public partial class LocalityTests
{
    private const string TwoRowGrid = """
        Grid RowDefinitions: { RowDefinition Height: "Auto"; RowDefinition Height: "*"; } {
            textbox "A" vm.A Grid.Row: 1;
            Button Grid.Row: {0} { "Go"; }
        }
        """;

    // ── Grid placement (FS1038) ─────────────────────────────────────

    [Fact]
    public void Grid_row_inside_the_declared_rows_is_accepted()
    {
        var result = Compile(TwoRowGrid.Replace("{0}", "0"));
        Clean(result);
        Assert.Contains("<Button Content=\"Go\" Grid.Row=\"0\" />", Xaml(result));
        Assert.Contains("<RowDefinition Height=\"Auto\" />", Xaml(result));
    }

    [Fact]
    public void Grid_row_past_the_declared_rows_is_FS1038()
    {
        var d = Single(Compile(TwoRowGrid.Replace("{0}", "5")), "FS1038");
        Assert.Equal("Grid.Row: 5 is past the last row; the Grid at line 3 declares 2 rows (0–1)", d.Message);
        Assert.Equal(5, d.Location.Line);
    }

    [Fact]
    public void Grid_row_inside_a_keyword_layout_is_FS1038()
    {
        var d = Single(Compile("card \"Box\" { Button Grid.Row: 1 { \"Go\"; } }"), "FS1038");
        Assert.Contains("only applies to a direct child of a generic Grid with RowDefinitions:", d.Message);
        Assert.Contains("inside 'card'", d.Message);

        // Keyword elements are checked too, even though their root type is not known yet.
        Single(Compile("card \"Box\" { textbox \"A\" vm.A Grid.Row: 1; }"), "FS1038");
        Single(Compile("Button Grid.Column: 0 { \"Top level\"; }"), "FS1038");
    }

    [Fact]
    public void Grid_without_definitions_and_overflowing_spans_are_FS1038()
    {
        Assert.Contains("declares none", Single(Compile("Grid { Button Grid.Row: 0 { \"x\"; } }"), "FS1038").Message);
        var span = Single(Compile(TwoRowGrid.Replace("{0}", "1 Grid.RowSpan: 2")), "FS1038");
        Assert.Contains("Grid.RowSpan: 2 from row 1 overflows", span.Message);
        Assert.Contains("literal integer", Single(Compile(TwoRowGrid.Replace("{0}", "vm.Row")), "FS1038").Message);
        Clean(Compile("""
            Grid ColumnDefinitions: { ColumnDefinition Width: "*"; ColumnDefinition Width: "2*"; } {
                Button Grid.Column: 1 { "Right"; }
            }
            """));
    }

    // ── References (FS1039 / FS1040) ────────────────────────────────

    private const string Visual = """
        Grid {
            VisualStateManager.VisualStateGroups: {
                VisualStateGroup {
                    VisualState name: Wide {
                        Setters: { Setter Target: {0} Value: "Inline"; }
                    }
                }
            }
            SplitView name: Nav DisplayMode: Overlay { text "content"; }
        }
        """;

    [Fact]
    public void Setter_target_ref_resolves_and_emits_a_target_path()
    {
        var result = Compile(Visual.Replace("{0}", "ref(Nav).DisplayMode"));
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<VisualStateManager.VisualStateGroups>", xaml);
        Assert.Contains("<Setter Target=\"Nav.DisplayMode\" Value=\"Inline\" />", xaml);
    }

    [Fact]
    public void String_reference_is_FS1040()
    {
        var d = Single(Compile(Visual.Replace("{0}", "\"Nav.DisplayMode\"")), "FS1040");
        Assert.Contains("write ref(Name)", d.Message);
        Assert.StartsWith("write Target: ref(Nav).DisplayMode", d.Hint);
        Single(Compile(Visual.Replace("{0}", "ref(Nav)")), "FS1040"); // a target path needs the member
    }

    [Fact]
    public void Unknown_ref_name_is_FS1039()
    {
        var d = Single(Compile(Visual.Replace("{0}", "ref(Nope).X")), "FS1039");
        Assert.Equal("ref(Nope) names no element in this view", d.Message);
        Assert.Contains("did you mean 'Nav'?", Single(Compile(Visual.Replace("{0}", "ref(Nva).DisplayMode")), "FS1039").Message);
    }

    [Fact]
    public void Ref_member_is_checked_against_the_named_type()
    {
        var d = Single(Compile(Visual.Replace("{0}", "ref(Nav).DisplayMod")), "FS1031");
        Assert.Contains("'SplitView' (Nav) has no property 'DisplayMod'; did you mean 'DisplayMode'?", d.Message);
    }

    [Fact]
    public void Element_references_emit_names_and_element_bindings()
    {
        var result = Compile("""
            RelativePanel {
                TextBox name: Query PlaceholderText: "Search";
                TextBlock Text: ref(Query).Text RelativePanel.Below: ref(Query);
                TeachingTip Target: ref(Query) Title: "Tip";
            }
            """);
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<TextBlock Text=\"{x:Bind Query.Text, Mode=OneWay}\" RelativePanel.Below=\"Query\" />", xaml);
        Assert.Contains("<TeachingTip Target=\"{x:Bind Query}\" Title=\"Tip\" />", xaml);

        Single(Compile("RelativePanel { TextBox name: Query; TextBlock RelativePanel.Below: \"Query\"; }"), "FS1040");
    }

    // ── Allowlist (FS1041 / FS1037) ─────────────────────────────────

    [Fact]
    public void Attached_property_outside_the_allowlist_is_FS1041()
    {
        var d = Single(Compile("Border SomePanel.Weight: 1 { text \"x\"; }"), "FS1041");
        Assert.Contains("'SomePanel.Weight' is not in the locality allowlist", d.Message);
        Assert.Contains("overlay.json", d.Hint);

        var m = ControlManifest.Default;
        var unlisted = m.AttachedProperties.First(a => m.Locality($"{a.Owner}.{a.Name}") == AttachedLocality.Unknown);
        Single(Compile($"Border {unlisted.Owner}.{unlisted.Name}: 1 {{ text \"x\"; }}"), "FS1041");
    }

    [Fact]
    public void Misspelled_attached_owner_is_FS1037()
    {
        var d = Single(Compile("Border Gird.Row: 1 { text \"x\"; }"), "FS1037");
        Assert.Contains("did you mean 'Grid.Row'?", d.Message);
    }

    [Fact]
    public void Local_attached_properties_pass_through()
    {
        var result = Compile("Button Content: \"Save\" AutomationProperties.Name: \"Save file\" ToolTipService.ToolTip: \"Saves\";");
        Clean(result);
        Assert.Contains("AutomationProperties.Name=\"Save file\" ToolTipService.ToolTip=\"Saves\"", Xaml(result));
    }

    // ── No unchecked references anywhere in the snapshot output ───

    [GeneratedRegex(@"ElementName=(\w+)|TargetName=""(\w+)""|\sTarget=""(?:\{x:Bind |\{Binding ElementName=)?(\w+)")]
    private static partial Regex ReferenceAttribute();

    [Fact]
    public void Every_emitted_element_reference_names_a_declared_element()
    {
        var casesDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Cases");
        var checkedReferences = 0;
        foreach (var dir in Directory.GetDirectories(casesDir))
        {
            var compiler = new FlagstaffCompiler();
            var names = new HashSet<string>(StringComparer.Ordinal) { "FlagstaffPage" }; // the generated page root
            var files = Directory.GetFiles(dir, "*.flag").OrderBy(f => Path.GetFileName(f) == "app.flag" ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal);
            var xaml = new List<string>();
            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                var parser = new Parser(new Lexer(source, Path.GetFileName(file)).Tokenize(tolerant: true));
                foreach (var view in parser.Parse().Declarations.OfType<ViewDecl>())
                    names.UnionWith(view.NamedElements.Select(n => n.Name));
                // Hand-written xaml { } blocks are the escape hatch; only compiler output is held to the rule.
                if (source.Contains("xaml {", StringComparison.Ordinal))
                    continue;
                xaml.AddRange(compiler.Compile(source, Path.GetFileName(file)).Files
                    .Where(f => f.Name.EndsWith(".xaml", StringComparison.Ordinal)).Select(f => f.Content));
            }
            foreach (Match match in xaml.SelectMany(x => ReferenceAttribute().Matches(x)))
            {
                var name = match.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                Assert.True(names.Contains(name), $"{Path.GetFileName(dir)}: '{match.Value}' references '{name}', which no name: declares");
                checkedReferences++;
            }
        }
        Assert.True(checkedReferences > 0, "the snapshot cases should contain element references");
    }
}
