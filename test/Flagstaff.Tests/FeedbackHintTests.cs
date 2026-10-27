using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Hints for the mistakes the LLM benchmark surfaced (bench/results.md): <c>xaml fill { }</c>, UWP-only controls,
/// <c>when</c> written as an element, XAML property synonyms, <c>bold</c> on <c>text</c>, unknown icon names, and the
/// contract comments in the generated partials.
/// </summary>
public class FeedbackHintTests
{
    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "test.flag");

    private static Diagnostic Single(string source, string code) =>
        Assert.Single(Compile(source).Diagnostics, d => d.Code == code);

    [Theory]
    [InlineData("xaml fill { <local:Sketch /> }", "stack fill { xaml { ... } }")]
    [InlineData("xaml width: 200 { <local:Sketch /> }", "stack width: ... { xaml { ... } }")]
    [InlineData("xaml when(vm.Show) { <local:Sketch /> }", "stack when(...) { xaml { ... } }")]
    public void Props_on_xaml_say_to_wrap_the_escape(string line, string wrapped)
    {
        var d = Single($"view V {{\n    bind vm: VViewModel;\n    {line}\n}}", "FS1051");
        Assert.StartsWith("'xaml' takes no props or conditions", d.Message);
        Assert.Equal($"wrap the escape instead: {wrapped}", d.Hint);
    }

    [Theory]
    [InlineData("InkCanvas", "Canvas")]
    [InlineData("InkToolbar", "AppBarToggleButton")]
    [InlineData("InkToolbarBallpointPenButton", "AppBarToggleButton")]
    [InlineData("MediaElement", "MediaPlayerElement")]
    [InlineData("WebView", "WebView2")]
    [InlineData("CaptureElement", "MediaPlayerElement")]
    public void Uwp_only_controls_name_the_winui3_replacement(string type, string replacement)
    {
        var d = Single($"view V {{\n    {type};\n}}", "FS1030");
        Assert.Equal($"'{type}' is a UWP control: WinUI 3 (Windows App SDK) has no {type}", d.Message);
        Assert.Contains(replacement, d.Hint);
        Assert.DoesNotContain("use \"<assembly>\"", d.Hint);
    }

    [Fact]
    public void Uwp_only_table_names_no_type_the_manifest_has()
    {
        foreach (var type in GenericValidator.UwpOnlyControls.Keys)
            Assert.False(ControlManifest.Default.TryGetType(type, out _), type);
    }

    [Theory]
    [InlineData("when(vm.IsBusy) { text \"Busy\"; }", "when(vm.CanSave)")]
    [InlineData("enabled(vm.CanSave) { button \"Save\" -> vm.Save; }", "enabled(vm.CanSave)")]
    public void Condition_written_as_an_element_says_where_it_goes(string line, string clause)
    {
        var d = Single($"view V {{\n    bind vm: VViewModel;\n    {line}\n}}", "FS1000");
        Assert.StartsWith("'", d.Message);
        Assert.Contains("is not an element", d.Message);
        Assert.Contains($"button \"Save\" -> vm.Save {clause};", d.Hint);
    }

    [Fact]
    public void The_hinted_condition_placement_compiles()
    {
        var result = Compile("view V {\n    bind vm: VViewModel;\n    button \"Save\" -> vm.Save when(vm.CanSave);\n    stack when(vm.X) { text \"a\"; }\n}");
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
    }

    [Theory]
    [InlineData("MenuBar { MenuBarItem Header: \"File\" { MenuFlyoutItem Text: \"Open\"; } }", "MenuBarItem", "Header", "Title")]
    [InlineData("AppBarButton Text: \"Go\";", "AppBarButton", "Text", "Label")]
    [InlineData("NavigationView Items: vm.Rows;", "NavigationView", "Items", "MenuItems")]
    [InlineData("Slider Min: 0;", "Slider", "Min", "Minimum")]
    [InlineData("TextBox Placeholder: \"Search\";", "TextBox", "Placeholder", "PlaceholderText")]
    public void Xaml_name_synonyms_are_suggested_first(string line, string type, string key, string suggestion)
    {
        var d = Single($"view V {{\n    bind vm: VViewModel;\n    {line}\n}}", "FS1031");
        Assert.Equal($"'{type}' has no property '{key}'; did you mean '{suggestion}'?", d.Message);
    }

    [Fact]
    public void Synonym_suggestions_keep_only_properties_the_type_has()
    {
        Assert.True(ControlManifest.Default.TryGetType("TextBlock", out var textBlock));
        Assert.Equal("Text", ControlManifest.Default.SuggestProperties(textBlock, "Content")[0]);
        Assert.True(ControlManifest.Default.TryGetType("Button", out var button));
        Assert.Equal("Content", ControlManifest.Default.SuggestProperties(button, "Text")[0]);
    }

    [Fact]
    public void Text_accepts_bold()
    {
        var result = Compile("view V {\n    bind vm: VViewModel;\n    text \"Total\" bold;\n    text vm.Status bold;\n}");
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var xaml = result.Files.Single(f => f.Name == "V.xaml").Content;
        Assert.Contains("<TextBlock Text=\"Total\" FontWeight=\"Bold\"", xaml);
        Assert.Contains("FontWeight=\"Bold\"", xaml[xaml.IndexOf("ViewModel.Status", StringComparison.Ordinal)..]);
    }

    [Theory]
    [InlineData("previous-track", "previous")]
    [InlineData("settings", "setting")]
    [InlineData("hom", "home")]
    [InlineData("search", "find")]
    public void Unknown_keyword_icon_is_FS1048_with_the_closest_symbol(string icon, string suggestion)
    {
        var d = Single($"view V {{\n    bind vm: VViewModel;\n    button \"Go\" icon: {icon} -> vm.Go;\n}}", "FS1048");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.EndsWith($"did you mean '{suggestion}'?", d.Message);
        Assert.Equal(new SourceLocation("test.flag", 3, 17), d.Location);
    }

    [Fact]
    public void Every_manifest_symbol_is_a_valid_icon()
    {
        Assert.True(ControlManifest.Default.TryGetValueType("Symbol", out var symbol));
        foreach (var member in symbol.EnumMembers!)
            Assert.Null(SemanticValidator.UnknownIcon(SemanticValidator.Kebab(member)));
        Assert.Null(SemanticValidator.UnknownIcon("outline-star"));
        Assert.Null(SemanticValidator.UnknownIcon("home"));
    }

    [Fact]
    public void Generic_icon_value_suggests_a_symbol_member()
    {
        var d = Single("view V {\n    AppBarButton Label: \"Back\" Icon: \"PreviousTrack\";\n}", "FS1032");
        Assert.Equal("Symbol has no 'PreviousTrack'; did you mean Icon: Previous?", d.Hint);
    }

    [Fact]
    public void Generated_partials_carry_the_contract()
    {
        var result = Compile("""
            view DashboardView {
                bind vm: DashboardViewModel;
                onload -> vm.Load;
                stat "Open" vm.OpenCount;
                table items: vm.Orders of Order -> vm.Edit(item) {
                    col "Total" bind: item.Total format: currency;
                }
            }
            """);
        var vm = result.Files.Single(f => f.Name == "DashboardViewModel.g.cs").Content;
        Assert.Contains("// Contract (from test.flag): your own 'partial class DashboardViewModel' must NOT declare these again.", vm);
        Assert.Contains("//     string OpenCount  <- stat \"Open\" vm.OpenCount", vm);
        Assert.Contains("//     ObservableCollection<Order> Orders  <- table items: vm.Orders", vm);
        Assert.Contains("//     private partial Task OnEditAsync(Order item);", vm);
        var page = result.Files.Single(f => f.Name == "DashboardView.xaml.cs").Content;
        Assert.Contains("// Contract: this partial declares the constructors DashboardView() and DashboardView(ViewModels.DashboardViewModel)", page);

        var contract = Assert.Single(result.Contracts);
        Assert.Equal(["OpenCount", "Orders"], contract.Properties.Select(p => p.Name));
        Assert.Equal(["OnLoadAsync", "OnEditAsync"], contract.Delegates.Select(c => c.Delegate));
    }
}
