using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Wave D compiler carry-forwards: collection typing and <c>of T</c> on generic elements (item templates, third-party
/// bindings, FS4003), empty item classes, <c>(item)</c> in a nested list's property element, events on variant tags,
/// condition numbering inside property values, and the legacy <c>Load:</c> spelling (FS1046, in <see cref="ResourceTests"/>).
/// </summary>
public class CarryForwardTests
{
    private static CompilationResult Compile(string body, string uses = "", FlagstaffCompiler? compiler = null) =>
        (compiler ?? new FlagstaffCompiler { AppNamespace = "TestApp" }).Compile(
            uses + "view V {\n    bind vm: VViewModel;\n" + body + "\n}\n", "test.flag");

    private static string File(CompilationResult result, string name) =>
        Assert.Single(result.Files, f => f.Name == name).Content;

    private static string Xaml(CompilationResult result) => File(result, "V.xaml");
    private static string ViewModel(CompilationResult result) => File(result, "VViewModel.g.cs");

    private static void NoErrors(CompilationResult result) =>
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

    private const string Collection = "System.Collections.ObjectModel.ObservableCollection";

    // ── Item 4: collection properties and `of T` on WinUI items controls ──

    [Fact]
    public void Collection_properties_infer_an_observable_collection()
    {
        var result = Compile("""
            ListView ItemsSource: vm.Rows;
            NavigationView MenuItemsSource: vm.Pages;
            """);
        NoErrors(result);
        var vm = ViewModel(result);
        Assert.Contains($"private {Collection}<object> _rows = new();", vm);
        Assert.Contains($"private {Collection}<object> _pages = new();", vm);
    }

    [Fact]
    public void Of_T_on_a_generic_items_control_types_items_and_selection_and_templates_the_body()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = Compile("""
            ListView ItemsSource: vm.Rows of Row SelectedItem: vm.Selected {
                text item.Title;
                button "Open" -> vm.OpenCommand(item);
            }
            """, compiler: compiler);
        NoErrors(result);
        var vm = ViewModel(result);
        Assert.Contains($"private {Collection}<Row> _rows = new();", vm);
        Assert.Contains("private Row? _selected;", vm);
        Assert.Contains("private async Task OpenAsync(Row item)", vm);

        var xaml = Xaml(result);
        Assert.Contains("<ListView.ItemTemplate>", xaml);
        Assert.Contains("<DataTemplate x:DataType=\"vm:Row\">", xaml);
        Assert.Contains("<TextBlock Text=\"{x:Bind Title}\" />", xaml);
        Assert.Contains("CommandParameter=\"{x:Bind}\"", xaml);

        var row = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Row.g.cs").Content;
        Assert.Contains("public string Title { get; set; } = \"\";", row);
    }

    [Fact]
    public void Of_T_after_selected_item_types_the_collection_too()
    {
        var result = Compile("ListView ItemsSource: vm.Rows SelectedItem: vm.Selected of Row;");
        NoErrors(result);
        Assert.Contains($"private {Collection}<Row> _rows = new();", ViewModel(result));
        Assert.Contains("private Row? _selected;", ViewModel(result));
    }

    [Fact]
    public void Vm_binding_inside_a_generic_item_template_is_FS2005()
    {
        var result = Compile("""
            ListView ItemsSource: vm.Rows of Row {
                text vm.Title;
            }
            """);
        Assert.Contains(result.Diagnostics, d => d.Code == "FS2005");
    }

    // ── Item 5: third-party bindings ──

    private const string Lvc = "use \"LiveChartsCore.SkiaSharpView.WinUI\" as lvc;\n";

    [Fact]
    public void Of_T_types_each_binding_on_a_third_party_element()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = Compile("lvc.CartesianChart Series: vm.Series of ISeries Value: vm.Volume of double;", Lvc, compiler);
        NoErrors(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS4003");
        var vm = ViewModel(result);
        Assert.Contains($"private {Collection}<ISeries> _series = new();", vm);
        Assert.Contains("private double _volume;", vm);
        Assert.Contains("Series=\"{x:Bind ViewModel.Series, Mode=OneWay}\" Value=\"{x:Bind ViewModel.Volume, Mode=OneWay}\"", Xaml(result));
        // A library type is not an item class.
        Assert.DoesNotContain(compiler.GenerateAppScaffold().Files, f => f.Name is "ISeries.g.cs" or "double.g.cs");
    }

    [Fact]
    public void Untyped_binding_on_a_third_party_element_is_object_and_FS4003()
    {
        var result = Compile("lvc.CartesianChart Series: vm.Series of ISeries XAxes: vm.Axes Height: 400;", Lvc);
        NoErrors(result);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS4003");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("infers object; add `of <Type>`", d.Message);
        Assert.Contains("XAxes: vm.Axes of <Type>", d.Hint);
        Assert.Contains("private object? _axes;", ViewModel(result));
    }

    // ── Item 6: empty item classes ──

    [Fact]
    public void Select_of_T_without_display_generates_an_empty_partial_class()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        NoErrors(Compile("select \"Owner\" vm.Owner items: vm.People of Person;", compiler: compiler));
        var person = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Person.g.cs").Content;
        Assert.Contains("public partial class Person\n{\n}", person.ReplaceLineEndings("\n"));
    }

    // ── Item 7: (item) in a nested list's property element ──

    // A keyword list's items: must be vm.X, so the inner list is a generic items control over a row member.
    private const string NestedLists = """
        list items: vm.Groups of Group {
            text item.Name;
            ListView ItemsSource: item.Members of Member {
                Header: StackPanel { button "Add member" -> vm.AddMemberCommand(item); }
                text item.Name;
            }
        }
        """;

    [Fact]
    public void Item_in_an_inner_list_header_is_the_outer_row()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = Compile(NestedLists, compiler: compiler);
        NoErrors(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS2007");
        Assert.Contains("private async Task AddMemberAsync(Group item)", ViewModel(result));
        Assert.Contains("Path=ViewModel.AddMemberCommand}\" CommandParameter=\"{x:Bind}\"", Xaml(result));
        var group = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Group.g.cs").Content;
        Assert.Contains($"public {Collection}<Member> Members {{ get; set; }}", group);
        Assert.Contains("<DataTemplate x:DataType=\"vm:Member\">", Xaml(result));
    }

    [Theory]
    [InlineData("list items: vm.Members of Member")]
    [InlineData("ListView ItemsSource: vm.Members of Member")]
    public void Item_in_a_top_level_list_header_is_still_FS2007(string header)
    {
        var result = Compile(header + """
             {
                Header: StackPanel { button "Add" -> vm.AddCommand(item); }
                text item.Name;
            }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS2007");
        Assert.Contains("outside the row", d.Message);
    }

    // ── Item 8: events and pass-through on variant tags ──

    [Fact]
    public void Heading_with_icon_takes_margin_and_tapped_on_its_stack_panel()
    {
        var result = Compile("heading \"X\" icon: home Margin: \"0,4\" on Tapped -> vm.X;");
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("<StackPanel Orientation=\"Horizontal\" Spacing=\"8\" Margin=\"0,4\" Tapped=\"OnEvent_1\">", xaml);
        Assert.Contains("OnEvent_1(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void An_event_the_variant_tag_lacks_is_FS1034_naming_the_tag()
    {
        // SelectionChanged is a TextBlock event; heading with icon: is a StackPanel.
        var result = Compile("heading \"X\" icon: home on SelectionChanged -> vm.Pick;");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1034");
        Assert.Contains("written as a StackPanel", d.Message);
        Assert.Contains("'heading' is a StackPanel here", d.Hint);
        NoErrors(Compile("heading \"X\" on SelectionChanged -> vm.Pick;"));
    }

    // ── Item 9: conditions inside property values ──

    private const string FlyoutConditions = """
        text "before" when(vm.Count > 0);
        button "More" -> vm.MoreCommand {
            Flyout: MenuFlyout {
                MenuFlyoutItem Text: "A" when(vm.Count > 1) -> vm.ACommand;
                MenuFlyoutItem Text: "B" enabled(vm.Count > 2) -> vm.BCommand;
                MenuFlyoutItem Text: "C" when(vm.Count > 3 && vm.Mode == "x") -> vm.CCommand;
            }
        }
        text "after" when(vm.Count > 4);
        """;

    [Fact]
    public void Conditions_in_flyout_items_get_functions_numbered_in_document_order()
    {
        var result = Compile(FlyoutConditions);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("<TextBlock Text=\"before\" Visibility=\"{x:Bind local:V.When_1(ViewModel.Count), Mode=OneWay}\" />", xaml);
        Assert.Matches(@"<MenuFlyoutItem Text=""A""[^>]*Visibility=""\{x:Bind local:V\.When_2\(ViewModel\.Count\), Mode=OneWay\}""", xaml);
        Assert.Contains("local:V.Enabled_1(ViewModel.Count)", xaml);
        Assert.Contains("local:V.When_3(ViewModel.Count, ViewModel.Mode)", xaml);
        Assert.Contains("<TextBlock Text=\"after\" Visibility=\"{x:Bind local:V.When_4(ViewModel.Count), Mode=OneWay}\" />", xaml);

        var codeBehind = File(result, "V.xaml.cs");
        var names = Regex.Matches(codeBehind, @"public static \S+ (\w+)\(").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["When_1", "When_2", "Enabled_1", "When_3", "When_4"], names);

        // Deterministic: a second compilation numbers identically.
        Assert.Equal(xaml, Xaml(Compile(FlyoutConditions)));
        Assert.Equal(codeBehind, File(Compile(FlyoutConditions), "V.xaml.cs"));
    }

    [Fact]
    public void View_level_property_elements_are_numbered_before_the_body()
    {
        var result = Compile("""
            Page.BottomAppBar: CommandBar {
                AppBarButton Label: "A" when(vm.Count > 1) -> vm.ACommand;
            }
            text "body" when(vm.Count > 2);
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("local:V.When_1(ViewModel.Count)", xaml[..xaml.IndexOf("body", StringComparison.Ordinal)]);
        Assert.Contains("<TextBlock Text=\"body\" Visibility=\"{x:Bind local:V.When_2(ViewModel.Count), Mode=OneWay}\" />", xaml);
    }

    // ── Item 12: nested binding paths fail loudly ──

    [Theory]
    [InlineData("text vm.Customer.Name;", "expose `Name` on the ViewModel")]
    [InlineData("text \"x\" when(vm.Customer.IsVip);", "expose `IsVip` on the ViewModel")]
    public void Nested_binding_path_is_FS1051_with_a_hint(string body, string hint)
    {
        var d = Assert.Single(Compile(body).Diagnostics, d => d.Code == "FS1051");
        Assert.Contains("Nested binding path", d.Message);
        Assert.Contains("nested paths are not supported", d.Hint);
        Assert.Contains(hint, d.Hint);
    }

    // ── Item 13: non-UI elements as content ──

    [Fact]
    public void Row_definition_in_the_body_is_FS1035()
    {
        var result = Compile("RowDefinition Height: Auto;");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1035");
        Assert.Contains("not a UI element", d.Message);
        Assert.Contains("RowDefinition belongs in Grid RowDefinitions:", d.Hint);
        Assert.Contains("FS1035", Compile("stack { SolidColorBrush Color: Red; }").Diagnostics.Select(d => d.Code));
        // Property elements and resources take non-UI elements.
        NoErrors(Compile("""
            Page.Resources: { SolidColorBrush key: Accent Color: Red; }
            Grid RowDefinitions: { RowDefinition Height: Auto; } { text "x"; }
            """));
    }

    // ── Item 14: `of T` never ignored ──

    [Fact]
    public void Of_T_that_types_nothing_is_FS1047()
    {
        Assert.Equal("FS1047", Assert.Single(Compile("Border of Photo;").Diagnostics, d => d.Severity == DiagnosticSeverity.Error).Code);
        var result = Compile("GridView ItemsSource: vm.Photos of Photo;");
        NoErrors(result);
        Assert.Contains($"private {Collection}<Photo> _photos = new();", ViewModel(result));
    }

    // ── Item 15: Windows.* enums ──

    [Fact]
    public void Virtual_key_enums_are_bare_values()
    {
        var result = Compile("""
            button "Save" -> vm.Save {
                KeyboardAccelerators: {
                    KeyboardAccelerator Key: S Modifiers: Control;
                    KeyboardAccelerator Modifiers: Control Key: Number1;
                }
            }
            CalendarView FirstDayOfWeek: Monday;
            TextBlock Text: "x" FontWeight: "SemiBold";
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("<KeyboardAccelerator Key=\"S\" Modifiers=\"Control\" />", xaml);
        Assert.Contains("<KeyboardAccelerator Modifiers=\"Control\" Key=\"Number1\" />", xaml);
        Assert.Contains("FirstDayOfWeek=\"Monday\"", xaml);
        Assert.Contains("FontWeight=\"SemiBold\"", xaml);
        Assert.Contains("FS1032", Compile("KeyboardAccelerator Key: Banana;").Diagnostics.Select(d => d.Code).Concat(
            Compile("KeyboardAccelerator Key: \"Banana\";").Diagnostics.Select(d => d.Code)));
    }

    // ── Item 17: FS1002 hint on hoisted overlays ──

    [Fact]
    public void Unsupported_prop_on_an_overlay_does_not_suggest_wrapping()
    {
        var d = Assert.Single(Compile("toast \"Saved\" when(vm.Saved) uid: \"SavedToast\";").Diagnostics, d => d.Code == "FS1002");
        Assert.Contains("not supported on overlays", d.Hint);
        Assert.DoesNotContain("wrap it", d.Hint);
    }
}
