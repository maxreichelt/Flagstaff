using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Generic elements (docs/universal-element.md, section 2a): manifest validation (FS1030/1031/1032/1035/1004/2014/4001/4002),
/// the bare-enum parser repair, emission (<c>XamlGenerator.Generic.cs</c>) and type inference.
/// Locality (Grid.Row, ref()) is in <see cref="LocalityTests"/>.
/// </summary>
public class GenericTests
{
    internal static CompilationResult Compile(string body, string uses = "") =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(
            uses + "view V {\n    bind vm: VViewModel;\n" + body + "\n}\n", "test.flag");

    internal static string Xaml(CompilationResult result) =>
        Assert.Single(result.Files, f => f.Name == "V.xaml").Content;

    internal static string ViewModel(CompilationResult result) =>
        Assert.Single(result.Files, f => f.Name == "VViewModel.g.cs").Content;

    internal static Diagnostic Single(CompilationResult result, string code) =>
        Assert.Single(result.Diagnostics, d => d.Code == code);

    internal static void Clean(CompilationResult result) =>
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

    // ── Emission ───────────────────────────────────────────────────

    [Fact]
    public void Section_2a_binds_inputs_two_way_and_nests_keywords()
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

    // ── Parser repair: bare enum values ─────────────────────────────

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
}
