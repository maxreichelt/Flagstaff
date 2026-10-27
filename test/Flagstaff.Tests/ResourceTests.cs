using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flagstaff.Compiler;
using static Flagstaff.Tests.GenericTests;

namespace Flagstaff.Tests;

/// <summary>
/// Resources, styles, visual states, storyboards and the value forms (<c>theme()</c>, <c>resource()</c>,
/// <c>load:</c>, <c>uid:</c>, <c>key:</c>): <c>GenericValidator.ValidateResources</c> (FS1044/FS1045, typed setters,
/// storyboard targets), the linter's FS3010 key set, and overlays hoisted out of property elements.
/// </summary>
public partial class ResourceTests
{
    private static List<string> Codes(CompilationResult result, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        result.Diagnostics.Where(d => d.Severity == severity).Select(d => d.Code).ToList();

    // ── key: ───────────────────────────────────────────────────────

    [Fact]
    public void Key_emits_x_key_first_and_resolves_resource()
    {
        var result = Compile("""
            Page.Resources: {
                Style key: CompactStat TargetType: Border {
                    Setter Property: Padding Value: "12,8";
                    Setter Property: Background Value: theme(AccentFillColorDefaultBrush);
                }
                SolidColorBrush key: Accent Color: "#2563eb";
            }
            stat "Total" vm.Total Style: resource(CompactStat);
            Border BorderBrush: resource(Accent);
            """);
        Assert.Empty(result.Diagnostics); // no FS3010: both keys are declared in the view
        var xaml = Xaml(result);
        Assert.Contains("<Style x:Key=\"CompactStat\" TargetType=\"Border\">", xaml);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{ThemeResource AccentFillColorDefaultBrush}\" />", xaml);
        Assert.Contains("<SolidColorBrush x:Key=\"Accent\" Color=\"#2563eb\" />", xaml);
        Assert.Contains("<Border Style=\"{StaticResource CompactStat}\">", xaml);
    }

    [Fact]
    public void Key_outside_resources_is_FS1044()
    {
        var d = Single(Compile("Border key: Panel;"), "FS1044");
        Assert.Contains("resources block", d.Message);
    }

    [Fact]
    public void Key_on_a_keyword_in_resources_is_FS1044()
    {
        Single(Compile("Page.Resources: { card key: Panel; }"), "FS1044");
    }

    [Fact]
    public void Resource_without_key_is_FS1044_but_implicit_style_is_fine()
    {
        Single(Compile("Page.Resources: { SolidColorBrush Color: Red; }"), "FS1044");
        Clean(Compile("Page.Resources: { Style TargetType: Button { Setter Property: Padding Value: 8; } }"));
    }

    [Fact]
    public void Duplicate_key_is_FS1045()
    {
        var d = Single(Compile("""
            Page.Resources: {
                SolidColorBrush key: Accent Color: Red;
                SolidColorBrush key: Accent Color: Blue;
            }
            """), "FS1045");
        Assert.Contains("line 4", d.Message);
        Single(Compile("Page.Resources: { Style TargetType: Button; Style TargetType: Button; }"), "FS1045");
    }

    [Fact]
    public void Same_key_in_different_dictionaries_is_fine()
    {
        Clean(Compile("""
            Page.Resources: { SolidColorBrush key: Accent Color: Red; }
            Grid {
                Resources: { SolidColorBrush key: Accent Color: Blue; }
                text "x";
            }
            """));
    }

    // ── FS3010 ─────────────────────────────────────────────────────

    [Fact]
    public void Undeclared_resource_is_FS3010()
    {
        var result = Compile("Border Background: resource(Nope);");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS3010");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("'Nope'", d.Message);
    }

    [Fact]
    public void Linter_knows_keys_declared_in_the_page()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.V">
                <Page.Resources><SolidColorBrush x:Key="Mine" Color="Red" /></Page.Resources>
                <Border Background="{StaticResource Mine}" BorderBrush="{StaticResource Other}" />
            </Page>
            """;
        var d = Assert.Single(XamlLinter.Lint("V.xaml", xaml, "App"), d => d.Code == "FS3010");
        Assert.Contains("'Other'", d.Message);
    }

    [GeneratedRegex("x:Key=\"(\\w+)\"")]
    private static partial Regex XKey();

    [Fact]
    public void Every_App_and_stdlib_key_is_known_to_the_linter()
    {
        var app = new FlagstaffCompiler().CompileAll([new SourceFile("app.flag", """
            app A { theme { accent: #2563eb; font: "Segoe UI"; } nav top { link "Home" -> V; } }
            view V { text "x"; }
            """)]);
        var appXaml = Assert.Single(app.Files, f => f.Name == "App.xaml").Content;
        var styles = File.ReadAllText(Path.Combine(SnapshotTests.RepoRoot, "src", "Flagstaff.Stdlib", "Styles.xaml"));
        var keys = XKey().Matches(appXaml + styles).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.True(XamlLinter.IsKnownResource(k), $"x:Key '{k}' is not in XamlLinter.KnownResources"));
        Assert.DoesNotContain(app.Diagnostics, d => d.Code == "FS3010");
    }

    // ── Value forms ────────────────────────────────────────────────

    [Fact]
    public void Value_forms_render()
    {
        var result = Compile("""
            Page.Resources: { SolidColorBrush key: Accent Color: "#2563eb"; }
            Border Background: theme(CardBackgroundFillColorDefaultBrush) BorderBrush: resource(Accent) load: load(vm.ShowDetails) uid: "DetailsPane";
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            "<Border Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{StaticResource Accent}\" "
            + "x:Name=\"FlagstaffLoad_1\" x:Uid=\"DetailsPane\" x:Load=\"{x:Bind ViewModel.ShowDetails, Mode=OneWay}\" />", Xaml(result));
        Assert.Contains("private bool _showDetails;", ViewModel(result));
    }

    [Fact]
    public void Load_on_a_keyword_emits_x_load()
    {
        var result = Compile("card \"Lazy\" load: load(vm.ShowCard) { text \"x\"; }");
        Clean(result);
        Assert.Contains("x:Load=\"{x:Bind ViewModel.ShowCard, Mode=OneWay}\"", Xaml(result));
        Assert.Contains("private bool _showCard;", ViewModel(result));
    }

    [Fact]
    public void Load_needs_the_load_form()
    {
        Single(Compile("Border load: vm.ShowDetails;"), "FS1032");
        Single(Compile("Border load: true;"), "FS1032");
    }

    [Fact]
    public void Load_gets_a_generated_x_name_unless_named()
    {
        var result = Compile("""
            card "Tip" load: load(vm.ShowHint) { text "x"; }
            Border name: Details load: load(vm.ShowDetails);
            StackPanel load: load(vm.ShowMore);
            """);
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("x:Name=\"FlagstaffLoad_1\" x:Load=\"{x:Bind ViewModel.ShowHint, Mode=OneWay}\"", xaml);
        Assert.Contains("<Border x:Name=\"Details\" x:Load=\"{x:Bind ViewModel.ShowDetails, Mode=OneWay}\" />", xaml);
        Assert.Contains("<StackPanel x:Name=\"FlagstaffLoad_2\" x:Load=\"{x:Bind ViewModel.ShowMore, Mode=OneWay}\" />", xaml);
        Assert.DoesNotContain("FlagstaffLoad_3", xaml);
    }

    [Fact]
    public void X_load_without_x_name_is_FS3012()
    {
        var d = Assert.Single(XamlLinter.Lint("V.xaml",
            "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Border x:Load=\"False\" /></Page>",
            "TestApp"), d => d.Code == "FS3012");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void Legacy_Load_spelling_is_fs1046()
    {
        var result = Compile("StackPanel Load: load(vm.ShowDetails);");
        var d = Assert.Single(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("FS1046", d.Code);
        Assert.Contains("use the universal prop load:", d.Hint);
        Assert.Contains("load: load(vm.ShowDetails);", d.Hint);
        Assert.Equal("FS1046", Assert.Single(Compile("card \"x\" Load: load(vm.Show) { text \"x\"; }").Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error).Code);
        Single(Compile("StackPanel Load: true;"), "FS1032");
    }

    [Fact]
    public void Named_colors_and_hex_render_for_brush_and_color()
    {
        var result = Compile("""
            Border Background: Red BorderBrush: "#ff0000" BorderThickness: 1;
            Rectangle Fill: CornflowerBlue Stroke: #80000000 Height: 4;
            Page.Resources: { SolidColorBrush key: B Color: Teal; }
            """);
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<Border Background=\"Red\" BorderBrush=\"#ff0000\" BorderThickness=\"1\" />", xaml);
        Assert.Contains("Fill=\"CornflowerBlue\"", xaml);
        Assert.Contains("Stroke=\"#80000000\"", xaml);
        Assert.Contains("<SolidColorBrush x:Key=\"B\" Color=\"Teal\" />", xaml);
    }

    [Fact]
    public void Bad_color_is_FS1032()
    {
        Single(Compile("Border Background: \"reddish\";"), "FS1032");
    }

    // ── Styles ─────────────────────────────────────────────────────

    [Fact]
    public void Style_setter_value_shape_is_FS1032()
    {
        var d = Single(Compile("Page.Resources: { Style key: S TargetType: Button { Setter Property: Padding Value: true; } }"), "FS1032");
        Assert.Contains("Button.Padding", d.Message);
    }

    [Fact]
    public void Style_setter_unknown_property_is_FS1031()
    {
        var d = Single(Compile("Page.Resources: { Style key: S TargetType: Button { Setter Property: Paddin Value: 8; } }"), "FS1031");
        Assert.Contains("did you mean 'Padding'", d.Message);
    }

    [Fact]
    public void Style_setter_enum_and_string_literals_fit()
    {
        var result = Compile("""
            Page.Resources: {
                Style key: S TargetType: TextBlock {
                    Setter Property: TextWrapping Value: Wrap;
                    Setter Property: FontSize Value: "18";
                    Setter Property: IsTextSelectionEnabled Value: "True";
                    Setter Property: Margin Value: "0,8";
                }
            }
            """);
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<Setter Property=\"TextWrapping\" Value=\"Wrap\" />", xaml);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"18\" />", xaml);
        Single(Compile("Page.Resources: { Style key: S TargetType: TextBlock { Setter Property: TextWrapping Value: \"Sometimes\"; } }"), "FS1032");
    }

    [Fact]
    public void Based_on_renders_static_resource()
    {
        var result = Compile("""
            Page.Resources: {
                Style key: Base TargetType: Button;
                Style key: Wide TargetType: Button BasedOn: resource(Base) { Setter Property: MinWidth Value: 200; }
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Contains("<Style x:Key=\"Wide\" TargetType=\"Button\" BasedOn=\"{StaticResource Base}\">", Xaml(result));
    }

    // ── Visual states ──────────────────────────────────────────────

    private const string States = """
        VisualStateManager.VisualStateGroups: {
            VisualStateGroup {
                VisualState name: Wide {
                    StateTriggers: { AdaptiveTrigger MinWindowWidth: 720; }
                    Setters: { SETTER }
                }
            }
        }
        SplitView name: Nav DisplayMode: Overlay;
        card "Summary" name: Panel { text "x"; }
        """;

    private static CompilationResult CompileState(string setter) => Compile(States.Replace("SETTER", setter));

    [Fact]
    public void State_setter_renders_target_path_and_typed_value()
    {
        var result = CompileState("Setter Target: ref(Nav).DisplayMode Value: Inline; Setter Target: ref(Panel).Padding Value: \"24\";");
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<Setter Target=\"Nav.DisplayMode\" Value=\"Inline\" />", xaml);
        Assert.Contains("<Setter Target=\"Panel.Padding\" Value=\"24\" />", xaml);
        Assert.Contains("<VisualState x:Name=\"Wide\">", xaml);
        // View-level groups are the first child of the root Grid.
        Assert.Matches(@"<Grid>\s*<VisualStateManager.VisualStateGroups>", xaml);
    }

    [Fact]
    public void State_setter_value_shape_is_FS1032()
    {
        var d = Single(CompileState("Setter Target: ref(Nav).DisplayMode Value: \"Expanded\";"), "FS1032");
        Assert.Contains("SplitViewDisplayMode", d.Message);
    }

    [Fact]
    public void State_setter_unknown_member_on_keyword_target_is_FS1031()
    {
        Single(CompileState("Setter Target: ref(Panel).DisplayMode Value: \"Inline\";"), "FS1031");
    }

    [Fact]
    public void State_setter_string_target_is_FS1040()
    {
        Single(CompileState("Setter Target: \"Nav.X\" Value: \"1\";"), "FS1040");
    }

    // ── Storyboards ────────────────────────────────────────────────

    private static CompilationResult CompileStoryboard(string animations) => Compile($$"""
        Page.Resources: { Storyboard key: FadeIn { {{animations}} } }
        Border name: Panel { text "x"; }
        """);

    [Fact]
    public void Storyboard_renders_with_target_name_and_property()
    {
        var result = CompileStoryboard("DoubleAnimation Storyboard.TargetName: ref(Panel) Storyboard.TargetProperty: \"Opacity\" From: 0 To: 1 Duration: \"0:0:0.3\";");
        Assert.Empty(result.Diagnostics);
        Assert.Contains(
            "<DoubleAnimation Storyboard.TargetName=\"Panel\" Storyboard.TargetProperty=\"Opacity\" From=\"0\" To=\"1\" Duration=\"0:0:0.3\" />",
            Xaml(result));
    }

    [Fact]
    public void Storyboard_unknown_target_property_is_FS1031()
    {
        var d = Single(CompileStoryboard("DoubleAnimation Storyboard.TargetName: ref(Panel) Storyboard.TargetProperty: \"Opacityy\" To: 1;"), "FS1031");
        Assert.Contains("did you mean 'Opacity'", d.Message);
    }

    [Fact]
    public void Storyboard_target_name_is_inherited_from_the_storyboard()
    {
        var result = Compile("""
            Page.Resources: { Storyboard key: S Storyboard.TargetName: ref(Panel) { DoubleAnimation Storyboard.TargetProperty: "Opacityy" To: 1; } }
            Border name: Panel;
            """);
        Single(result, "FS1031");
    }

    [Fact]
    public void Storyboard_animation_type_mismatch_is_FS1032()
    {
        Single(CompileStoryboard("DoubleAnimation Storyboard.TargetName: ref(Panel) Storyboard.TargetProperty: \"Background\" To: 1;"), "FS1032");
    }

    [Fact]
    public void Storyboard_dotted_paths_pass()
    {
        Clean(CompileStoryboard("DoubleAnimation Storyboard.TargetName: ref(Panel) Storyboard.TargetProperty: \"(UIElement.Opacity)\" To: 1;"));
    }

    [Fact]
    public void Storyboard_unknown_target_name_is_FS1039()
    {
        Single(CompileStoryboard("DoubleAnimation Storyboard.TargetName: ref(Pannel) Storyboard.TargetProperty: \"Opacity\" To: 1;"), "FS1039");
    }

    // ── Overlays in property elements ──────────────────────────────

    [Fact]
    public void Toast_inside_a_flyout_is_hoisted()
    {
        var result = Compile("""button "x" { Flyout: Flyout { toast "Hi" when(vm.T); } }""");
        Clean(result);
        var xaml = Xaml(result);
        var doc = XDocument.Parse(xaml);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var overlay = Assert.Single(doc.Descendants(ns + "Grid"), g => (string?)g.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "FlagstaffOverlay");
        Assert.Contains(overlay.Descendants(ns + "InfoBar"), i => (string?)i.Attribute("Message") == "Hi");
        Assert.Contains("<!-- toast \"Hi\": rendered in FlagstaffOverlay -->", xaml);
        Assert.Contains("private bool _t;", ViewModel(result));
    }

    [Fact]
    public void Toast_in_a_row_context_flyout_is_FS1020()
    {
        var result = Compile("""
            table items: vm.Orders of Order {
                col "Client" bind: item.Client;
                ContextFlyout: MenuFlyout { MenuFlyoutItem Text: "Open" -> vm.OpenCommand(item); }
            }
            list items: vm.Orders of Order {
                text item.Client;
                ContextFlyout: Flyout { toast "Hi" when(vm.T); }
            }
            """);
        Single(result, "FS1020");
    }
}
