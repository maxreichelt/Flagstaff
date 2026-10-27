using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Generic elements: manifest validation (FS1030/1031/1032/1035/1004/2014/4001/4002),
/// the bare-enum parser repair, emission (<c>XamlGenerator.Generic.cs</c>) and type inference: collection typing and
/// <c>of T</c> on items controls and third-party elements (FS4003, FS1047), <c>(item)</c> scope in nested lists,
/// condition numbering inside property values, nested binding paths (FS1051) and Windows.* enum values.
/// Locality (Grid.Row, ref()) is in <see cref="LocalityTests"/>.
/// </summary>
public class GenericTests
{
    internal static CompilationResult Compile(string body, string uses = "", FlagstaffCompiler? compiler = null) =>
        (compiler ?? new FlagstaffCompiler { AppNamespace = "TestApp" }).Compile(
            uses + "view V {\n    bind vm: VViewModel;\n" + body + "\n}\n", "test.flag");

    internal static string Xaml(CompilationResult result) =>
        Assert.Single(result.Files, f => f.Name == "V.xaml").Content;

    internal static string ViewModel(CompilationResult result) =>
        Assert.Single(result.Files, f => f.Name == "VViewModel.g.cs").Content;

    internal static Diagnostic Single(CompilationResult result, string code) =>
        Assert.Single(result.Diagnostics, d => d.Code == code);

    internal static void Clean(CompilationResult result) =>
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

    private static string File(CompilationResult result, string name) =>
        Assert.Single(result.Files, f => f.Name == name).Content;

    private const string Collection = "System.Collections.ObjectModel.ObservableCollection";

    // ── Emission ───────────────────────────────────────────────────

    [Fact]
    public void Generic_inputs_bind_two_way_and_nest_keywords()
    {
        var result = Compile("""
            Expander Header: "Advanced" IsExpanded: vm.ShowAdvanced {
                toggle "Telemetry" vm.Telemetry;
                Slider Header: "Cache size (MB)" Minimum: 64 Maximum: 2048 Value: vm.CacheMb;
            }
            """);
        Assert.Empty(result.Diagnostics);
        var xaml = Xaml(result);
        Assert.Contains("<Expander Header=\"Advanced\" IsExpanded=\"{x:Bind ViewModel.ShowAdvanced, Mode=TwoWay}\">", xaml);
        // Expander.Content holds one object: two children are wrapped.
        Assert.Contains("<StackPanel Spacing=\"8\">", xaml);
        Assert.Contains("<ToggleSwitch Header=\"Telemetry\"", xaml);
        Assert.Contains("<Slider Header=\"Cache size (MB)\" Minimum=\"64\" Maximum=\"2048\" Value=\"{x:Bind ViewModel.CacheMb, Mode=TwoWay}\" />", xaml);

        var vm = ViewModel(result);
        Assert.Contains("private bool _showAdvanced;", vm);
        Assert.Contains("private double _cacheMb;", vm);
    }

    [Fact]
    public void Non_input_properties_bind_one_way_and_object_properties_infer_string()
    {
        var result = Compile("Expander Header: vm.Title IsEnabled: vm.Ready;");
        var xaml = Xaml(result);
        Assert.Contains("Header=\"{x:Bind ViewModel.Title, Mode=OneWay}\"", xaml);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.Ready, Mode=OneWay}\"", xaml);
        var vm = ViewModel(result);
        Assert.Contains("private string _title = \"\";", vm);
        Assert.Contains("private bool _ready;", vm);
    }

    [Fact]
    public void A_string_child_and_a_label_set_the_content_property()
    {
        var xaml = Xaml(Compile("""
            Button { "Save"; }
            Button "Open";
            MenuFlyoutItem "Copy";
            """));
        Assert.Contains("<Button Content=\"Save\" />", xaml);
        Assert.Contains("<Button Content=\"Open\" />", xaml);
        Assert.Contains("<MenuFlyoutItem Text=\"Copy\" />", xaml);
    }

    [Fact]
    public void Positional_binding_binds_the_input_property()
    {
        var result = Compile("Slider vm.Volume Maximum: 100;");
        Assert.Contains("<Slider Value=\"{x:Bind ViewModel.Volume, Mode=TwoWay}\" Maximum=\"100\" />", Xaml(result));
        Assert.Contains("private double _volume;", ViewModel(result));
    }

    [Fact]
    public void Property_elements_and_bare_type_values()
    {
        var xaml = Xaml(Compile("""
            Button Content: "More" {
                Flyout: MenuFlyout {
                    MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
                }
            }
            AppBarButton Label: "Undo" Icon: SymbolIcon;
            AppBarButton Label: "Redo" Icon: Redo;
            """));
        Assert.Contains("<Button.Flyout>", xaml);
        Assert.Contains("<MenuFlyoutItem Text=\"Duplicate\" Command=\"{x:Bind ViewModel.DuplicateCommand, Mode=OneWay}\" />", xaml);
        Assert.Contains("<AppBarButton.Icon>", xaml);
        Assert.Contains("<SymbolIcon />", xaml);
        Assert.Contains("<AppBarButton Label=\"Redo\" Icon=\"Redo\" />", xaml);
    }

    [Fact]
    public void Value_forms_render_as_markup_extensions_and_literals()
    {
        var result = Compile("""
            Border Background: theme(CardBackgroundFillColorDefaultBrush) BorderBrush: Red Margin: 8 Padding: "4,8" CornerRadius: 4 load: load(vm.ShowCard) Tag: null {
                text "x";
            }
            """);
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"", xaml);
        Assert.Contains("BorderBrush=\"Red\"", xaml);
        Assert.Contains("Margin=\"8\" Padding=\"4,8\" CornerRadius=\"4\"", xaml);
        Assert.Contains("x:Load=\"{x:Bind ViewModel.ShowCard, Mode=OneWay}\"", xaml);
        Assert.Contains("Tag=\"{x:Null}\"", xaml);
        Assert.Contains("private bool _showCard;", ViewModel(result));
    }

    [Fact]
    public void Lowercase_universal_props_and_conditions_apply()
    {
        var xaml = Xaml(Compile("Slider name: Volume width: 200 align: center Value: vm.Level when(vm.Show) enabled(vm.CanEdit);"));
        Assert.Contains("Visibility=\"{x:Bind ViewModel.Show, Mode=OneWay, Converter={StaticResource BoolToVisibility}}\"", xaml);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanEdit, Mode=OneWay}\"", xaml);
        Assert.Contains("x:Name=\"Volume\" Width=\"200\" HorizontalAlignment=\"Center\"", xaml);
    }

    [Fact]
    public void Generic_elements_inside_a_list_bind_to_the_row()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = compiler.Compile("""
            view V {
                bind vm: VViewModel;
                list items: vm.Projects of Project {
                    Expander Header: item.Name IsExpanded: item.IsOpen { text item.Notes; }
                    Button Content: "Open" -> vm.OpenCommand(item);
                }
            }
            """, "test.flag");
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<Expander Header=\"{x:Bind Name}\" IsExpanded=\"{x:Bind IsOpen}\">", xaml);
        Assert.Contains("Command=\"{Binding ElementName=FlagstaffPage, Path=ViewModel.OpenCommand}\" CommandParameter=\"{x:Bind}\"", xaml);
        var row = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Project.g.cs").Content;
        Assert.Contains("public bool IsOpen { get; set; }", row);
    }

    [Fact]
    public void Navigate_arrow_generates_a_typed_click_handler()
    {
        var result = Compile("MenuFlyoutItem Text: \"Settings\" -> navigate(SettingsView);");
        Clean(result);
        Assert.Contains("Click=\"OnNavigate_1\"", Xaml(result));
        var codeBehind = Assert.Single(result.Files, f => f.Name == "V.xaml.cs").Content;
        Assert.Contains("private void OnNavigate_1(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)", codeBehind);
        Assert.Contains("Frame.Navigate(typeof(SettingsView));", codeBehind);
    }

    [Fact]
    public void Third_party_element_gets_an_xmlns_and_FS4002()
    {
        var result = Compile("lvc.CartesianChart Series: vm.Series Height: 400 Whatever: \"x\";",
            "use \"LiveChartsCore.SkiaSharpView.WinUI\" as lvc;\n");
        var d = Single(result, "FS4002");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Clean(result); // own properties are unvalidated
        var xaml = Xaml(result);
        Assert.Contains("xmlns:lvc=\"using:LiveChartsCore.SkiaSharpView.WinUI\"", xaml);
        Assert.Contains("<lvc:CartesianChart Series=\"{x:Bind ViewModel.Series, Mode=OneWay}\" Height=\"400\" Whatever=\"x\" />", xaml);
    }

    // ── Parser repair: bare enum values ────────────────────────────

    [Fact]
    public void Bare_enum_followed_by_keys_and_a_block_stays_on_the_owner()
    {
        var result = Compile("""
            StackPanel Orientation: Horizontal Spacing: 8 {
                text "a";
            }
            SplitView DisplayMode: Overlay OpenPaneLength: 280;
            """);
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">", xaml);
        Assert.Contains("<SplitView DisplayMode=\"Overlay\" OpenPaneLength=\"280\" />", xaml);
    }

    [Fact]
    public void TryRepairBareEnum_moves_the_value_elements_props_back()
    {
        var loc = new SourceLocation("t.flag", 1, 1);
        var horizontal = new Element("Horizontal", [new PropPair("Spacing", new IntValue(8), loc)], new EmptyBody(), null, null, null, null, loc)
        {
            IsGeneric = true,
        };
        var prop = new PropPair("Orientation", new ElementValue(horizontal), loc);
        var owner = new Element("StackPanel", [prop], new EmptyBody(), null, null, null, null, loc) { IsGeneric = true };

        Assert.True(GenericValidator.TryRepairBareEnum(owner, prop, ControlManifest.Default, out var repaired, out var moved));
        Assert.Equal(new IdentValue("Horizontal"), repaired.Value);
        Assert.Equal("Spacing", Assert.Single(moved).Key);

        var notAMember = prop with { Value = new ElementValue(horizontal with { Keyword = "Sideways" }) };
        Assert.False(GenericValidator.TryRepairBareEnum(owner, notAMember, ControlManifest.Default, out _, out _));
    }

    [Fact]
    public void Misspelled_bare_enum_is_FS1032_with_a_suggestion()
    {
        var result = Compile("StackPanel Orientation: Horizonal Spacing: 8;");
        var d = Single(result, "FS1032");
        Assert.Contains("'Horizonal'", d.Message);
        Assert.Equal("did you mean 'Horizontal'?", d.Hint);
        Assert.DoesNotContain(result.Diagnostics, x => x.Code == "FS1030");
    }

    // ── Validation ─────────────────────────────────────────────────

    [Fact]
    public void Unknown_type_is_FS1030_with_a_suggestion()
    {
        var d = Single(Compile("Expandr Header: \"x\";"), "FS1030");
        Assert.Contains("'Expandr'", d.Message);
        Assert.Contains("did you mean 'Expander'?", d.Message);
        Assert.Contains("use \"<assembly>\" as prefix;", d.Hint);
    }

    [Fact]
    public void Abstract_type_is_FS1030()
    {
        Assert.Contains("abstract", Single(Compile("Panel;"), "FS1030").Message);
    }

    [Fact]
    public void Unknown_property_is_FS1031_with_a_suggestion()
    {
        var d = Single(Compile("Expander IsExpandd: true;"), "FS1031");
        Assert.Equal("'Expander' has no property 'IsExpandd'; did you mean 'IsExpanded'?", d.Message);
        Assert.Equal(new SourceLocation("test.flag", 3, 10), d.Location);
    }

    [Fact]
    public void Wrong_value_shape_is_FS1032()
    {
        var d = Single(Compile("Expander IsExpanded: \"yes\";"), "FS1032");
        Assert.Equal("'Expander.IsExpanded' expects bool (true or false), got \"yes\"", d.Message);
        Assert.Equal("write IsExpanded: true;", d.Hint);

        Assert.Contains("one of", Single(Compile("StackPanel Orientation: \"Diagonal\";"), "FS1032").Message);
        Single(Compile("Border Margin: \"wide\";"), "FS1032");
        Single(Compile("Button Content: \"x\" Flyout: Button { \"no\"; };"), "FS1032");
        Single(Compile("Border Tag: load(vm.X);"), "FS1032");
    }

    [Fact]
    public void Keyword_equivalent_is_an_FS4001_warning()
    {
        var result = Compile("TextBlock Text: \"x\";");
        var d = Single(result, "FS4001");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("'TextBlock' has a keyword: use 'text'", d.Message);
        Clean(result);
    }

    [Fact]
    public void Children_need_a_content_property_of_the_right_kind()
    {
        Assert.Contains("no content property", Single(Compile("Slider { text \"x\"; }"), "FS1035").Message);
        Assert.Contains("holds MenuFlyoutItemBase items", Single(Compile("MenuFlyout { text \"x\"; }"), "FS1035").Message);
        Assert.Contains("'Slider' is not one", Single(Compile("MenuFlyout { Slider; }"), "FS1035").Message);
        Clean(Compile("Border { text \"a\"; text \"b\"; }")); // UIElement content: wrapped in a StackPanel
        Assert.Contains("takes text only", Single(Compile("MenuFlyoutItem { Slider; }"), "FS1035").Message);
    }

    [Fact]
    public void Label_and_binding_need_a_matching_property()
    {
        var label = Single(Compile("Border \"x\";"), "FS1004");
        Assert.Contains("does not take a \"label\"", label.Message);
        var binding = Single(Compile("Border vm.X;"), "FS1004");
        Assert.Contains("positional binding", binding.Message);
        Assert.StartsWith("use Key: vm.X", binding.Hint);
    }

    [Fact]
    public void Arrow_needs_a_command_property_or_click_event()
    {
        Assert.Contains("no Command property", Single(Compile("Expander -> vm.GoCommand;"), "FS2014").Message);
        Assert.Contains("no Click event", Single(Compile("Expander -> navigate(OtherView);"), "FS2014").Message);
        Clean(Compile("Button Content: \"Go\" -> vm.GoCommand;"));
    }

    [Fact]
    public void Enabled_on_a_non_control_is_an_FS1012_warning()
    {
        var result = Compile("Border enabled(vm.X) { text \"x\"; }");
        Assert.Equal(DiagnosticSeverity.Warning, Single(result, "FS1012").Severity);
        Assert.DoesNotContain("IsEnabled", Xaml(result));
    }

    [Fact]
    public void Bindings_inside_a_generic_template_are_FS2005()
    {
        var d = Single(Compile("ListView ItemTemplate: DataTemplate { TextBlock Text: vm.Name; };"), "FS2005");
        Assert.Contains("template written as a generic element", d.Message);
    }

    // ── Collection typing and `of T` on items controls ─────────────

    [Fact]
    public void Collection_properties_infer_an_observable_collection()
    {
        var result = Compile("""
            ListView ItemsSource: vm.Rows;
            NavigationView MenuItemsSource: vm.Pages;
            """);
        Clean(result);
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
        Clean(result);
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
        Clean(result);
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

    [Fact]
    public void Of_T_that_types_nothing_is_FS1047()
    {
        Assert.Equal("FS1047", Assert.Single(Compile("Border of Photo;").Diagnostics, d => d.Severity == DiagnosticSeverity.Error).Code);
        var result = Compile("GridView ItemsSource: vm.Photos of Photo;");
        Clean(result);
        Assert.Contains($"private {Collection}<Photo> _photos = new();", ViewModel(result));
    }

    // ── Third-party bindings ───────────────────────────────────────

    private const string Lvc = "use \"LiveChartsCore.SkiaSharpView.WinUI\" as lvc;\n";

    [Fact]
    public void Of_T_types_each_binding_on_a_third_party_element()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = Compile("lvc.CartesianChart Series: vm.Series of ISeries Value: vm.Volume of double;", Lvc, compiler);
        Clean(result);
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
        Clean(result);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS4003");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("infers object; add `of <Type>`", d.Message);
        Assert.Contains("XAxes: vm.Axes of <Type>", d.Hint);
        Assert.Contains("private object? _axes;", ViewModel(result));
    }

    // ── Item classes ───────────────────────────────────────────────

    [Fact]
    public void Select_of_T_without_display_generates_an_empty_partial_class()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        Clean(Compile("select \"Owner\" vm.Owner items: vm.People of Person;", compiler: compiler));
        var person = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Person.g.cs").Content;
        Assert.Contains("public partial class Person\n{\n}", person.ReplaceLineEndings("\n"));
    }

    // ── (item) in a nested list's property element ─────────────────

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
        Clean(result);
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
    public void Item_in_a_top_level_list_header_is_FS2007(string header)
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

    // ── Conditions inside property values ──────────────────────────

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
        Clean(result);
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
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("local:V.When_1(ViewModel.Count)", xaml[..xaml.IndexOf("body", StringComparison.Ordinal)]);
        Assert.Contains("<TextBlock Text=\"body\" Visibility=\"{x:Bind local:V.When_2(ViewModel.Count), Mode=OneWay}\" />", xaml);
    }

    // ── Nested binding paths (FS1051) ──────────────────────────────

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

    // ── Non-UI elements as content (FS1035) ────────────────────────

    [Fact]
    public void Row_definition_in_the_body_is_FS1035()
    {
        var result = Compile("RowDefinition Height: Auto;");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1035");
        Assert.Contains("not a UI element", d.Message);
        Assert.Contains("RowDefinition belongs in Grid RowDefinitions:", d.Hint);
        Assert.Contains("FS1035", Compile("stack { SolidColorBrush Color: Red; }").Diagnostics.Select(d => d.Code));
        // Property elements and resources take non-UI elements.
        Clean(Compile("""
            Page.Resources: { SolidColorBrush key: Accent Color: Red; }
            Grid RowDefinitions: { RowDefinition Height: Auto; } { text "x"; }
            """));
    }

    // ── Windows.* enum values ──────────────────────────────────────

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
        Clean(result);
        var xaml = Xaml(result);
        Assert.Contains("<KeyboardAccelerator Key=\"S\" Modifiers=\"Control\" />", xaml);
        Assert.Contains("<KeyboardAccelerator Modifiers=\"Control\" Key=\"Number1\" />", xaml);
        Assert.Contains("FirstDayOfWeek=\"Monday\"", xaml);
        Assert.Contains("FontWeight=\"SemiBold\"", xaml);
        Assert.Contains("FS1032", Compile("KeyboardAccelerator Key: Banana;").Diagnostics.Select(d => d.Code).Concat(
            Compile("KeyboardAccelerator Key: \"Banana\";").Diagnostics.Select(d => d.Code)));
    }
}
