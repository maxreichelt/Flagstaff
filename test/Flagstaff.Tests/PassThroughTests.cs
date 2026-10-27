using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// Pass-through attributes and property elements on keyword elements:
/// <c>Margin:</c> / <c>AutomationProperties.*</c> land on the keyword's root tag, <c>Flyout: MenuFlyout { }</c> becomes
/// <c>&lt;Button.Flyout&gt;</c>, view-level <c>Page.Resources:</c> goes on the Page, and anything a keyword cannot carry
/// is FS1033.
/// </summary>
public class PassThroughTests
{
    private static CompilationResult Compile(string body) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile($$"""
            view V {
                bind vm: VModel;
            {{body}}
            }
            """, "test.flag");

    private static string Xaml(CompilationResult result) =>
        Assert.Single(result.Files, f => f.Name == "V.xaml").Content;

    private static string Line(string xaml, string startsWith) =>
        Assert.Single(xaml.Split('\n'), l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal)).Trim();

    private static void NoErrors(CompilationResult result) =>
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

    // ── RootType ───────────────────────────────────────────────────

    [Fact]
    public void Every_emitting_keyword_has_a_manifest_root_type()
    {
        string[] none = ["xaml", "col", "empty"];
        foreach (var schema in ElementSchema.All.Values)
        {
            if (none.Contains(schema.Keyword))
            {
                Assert.Null(schema.RootType);
                continue;
            }
            Assert.NotNull(schema.RootType);
            Assert.True(ControlManifest.Default.TryGetType(schema.RootType!, out _), $"{schema.Keyword}: {schema.RootType}");
        }
    }

    [Theory]
    [InlineData("textbox", "TextBox")]
    [InlineData("number", "NumberBox")]
    [InlineData("select", "ComboBox")]
    [InlineData("datepicker", "CalendarDatePicker")]
    [InlineData("filePicker", "Button")]
    [InlineData("link", "HyperlinkButton")]
    [InlineData("stat", "Border")]
    [InlineData("table", "ListView")]
    [InlineData("dialog", "Grid")]
    [InlineData("toolbar", "Grid")]
    public void Root_types_match_the_emitted_tag(string keyword, string rootType) =>
        Assert.Equal(rootType, ElementSchema.All[keyword].RootType);

    // ── scalar attributes ──────────────────────────────────────────

    [Fact]
    public void Textbox_margin_and_max_length_land_on_the_TextBox_and_keep_the_two_way_text()
    {
        var result = Compile("""textbox "Name" vm.Name Margin: "0,0,0,12" MaxLength: 80;""");
        NoErrors(result);
        Assert.Equal(
            """<TextBox Header="Name" Text="{x:Bind ViewModel.Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,12" MaxLength="80" />""",
            Line(Xaml(result), "<TextBox"));
    }

    [Fact]
    public void Local_attached_props_pass_through()
    {
        var result = Compile("""button "Save" -> vm.SaveCommand AutomationProperties.Name: "Save now";""");
        NoErrors(result);
        Assert.Contains("AutomationProperties.Name=\"Save now\"", Line(Xaml(result), "<Button"));
    }

    [Fact]
    public void Grid_placement_on_a_keyword_outside_a_generic_grid_is_FS1038()
    {
        // Locality rule: Grid.Row only means something in a generic Grid with RowDefinitions:; keyword layouts place
        // children by position. Not silently dropped: an error, so no XAML is generated.
        var result = Compile("""button "Save" -> vm.SaveCommand AutomationProperties.Name: "Save now" Grid.Row: 3;""");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1038");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("Grid.Row", d.Message);
        Assert.DoesNotContain(result.Files, f => f.Name == "V.xaml");
    }

    [Fact]
    public void Grid_placement_on_a_keyword_in_a_generic_grid_lands_on_its_root_tag()
    {
        var result = Compile("""
                Grid RowDefinitions: { RowDefinition Height: "Auto"; RowDefinition Height: "*"; } {
                    textbox "A" vm.A Grid.Row: 1;
                    table items: vm.Orders of Order Grid.Row: 0 { col "Client" bind: item.Client; empty "None."; }
                }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.EndsWith("""Grid.Row="1" />""", Line(xaml, "<TextBox"));
        // With an empty state the table is a wrapper Grid around the ListView: the wrapper takes the cell.
        Assert.Contains("""<Grid Grid.Row="0">""", xaml);
        Assert.DoesNotContain("Grid.Row", Line(xaml, "<ListView "));
    }

    [Fact]
    public void Binding_values_use_the_manifest_mode_and_item_bindings_bind_to_the_row()
    {
        var result = Compile("""
                select "Status" vm.Status options: ["Open", "Closed"] SelectedIndex: vm.StatusIndex IsEnabled: vm.CanEdit;
                list items: vm.Orders of Order {
                    text item.Client Opacity: item.Fade;
                }
            """);
        var xaml = Xaml(result);
        var combo = Line(xaml, "<ComboBox");
        Assert.Contains("SelectedIndex=\"{x:Bind ViewModel.StatusIndex, Mode=TwoWay}\"", combo);
        Assert.Contains("IsEnabled=\"{x:Bind ViewModel.CanEdit, Mode=OneWay}\"", combo);
        Assert.Contains("Opacity=\"{x:Bind Fade}\"", Line(xaml, "<TextBlock Text=\"{x:Bind Client}\""));
    }

    [Fact]
    public void Value_forms_render_as_markup_extensions_and_enum_spelling()
    {
        var result = Compile("""text "Hint" Foreground: theme(TextFillColorSecondaryBrush) TextWrapping: wrap Tag: null IsTextSelectionEnabled: true;""");
        Assert.Equal(
            """<TextBlock Text="Hint" Foreground="{ThemeResource TextFillColorSecondaryBrush}" TextWrapping="Wrap" Tag="{x:Null}" IsTextSelectionEnabled="True" />""",
            Line(Xaml(result), "<TextBlock"));
    }

    [Fact]
    public void A_pass_through_attribute_replaces_the_emitters_own()
    {
        var result = Compile("""stat "Total" vm.Total variant: success Style: resource(CompactStat);""");
        var border = Line(Xaml(result), "<Border");
        Assert.Equal("""<Border Style="{StaticResource CompactStat}">""", border);
    }

    // ── property elements ──────────────────────────────────────────

    [Fact]
    public void Button_flyout_becomes_a_property_element()
    {
        var result = Compile("""
                button "More" Margin: "0,8,0,0" AutomationProperties.Name: "More actions" -> vm.MoreCommand {
                    Flyout: MenuFlyout {
                        MenuFlyoutItem Text: "Duplicate" -> vm.DuplicateCommand;
                    }
                }
                button "Plain" -> vm.PlainCommand;
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("""<Button Content="More" Command="{x:Bind ViewModel.MoreCommand, Mode=OneWay}" Margin="0,8,0,0" AutomationProperties.Name="More actions">""", xaml);
        Assert.Contains("<Button.Flyout>", xaml);
        Assert.Contains("</Button.Flyout>\r\n", xaml.ReplaceLineEndings("\r\n"));
        // No property elements: self-closing.
        Assert.Equal("""<Button Content="Plain" Command="{x:Bind ViewModel.PlainCommand, Mode=OneWay}" />""", Line(xaml, "<Button Content=\"Plain\""));
    }

    [Fact]
    public void Keyboard_accelerators_and_context_flyouts_attach_to_containers_and_collections()
    {
        var result = Compile("""
                stack {
                    KeyboardAccelerators: { KeyboardAccelerator Key: "S" Modifiers: "Control"; }
                    text "a";
                }
                table items: vm.Orders of Order {
                    col "Client" bind: item.Client;
                    ContextFlyout: MenuFlyout { MenuFlyoutItem Text: "Open"; }
                }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("<StackPanel.KeyboardAccelerators>", xaml);
        // A table/list ContextFlyout belongs to the row: it is on the row template root, not the ListView.
        Assert.DoesNotContain("<ListView.ContextFlyout>", xaml);
        Assert.True(xaml.IndexOf("<ListView.ItemTemplate>", StringComparison.Ordinal) < xaml.IndexOf("<Grid.ContextFlyout>", StringComparison.Ordinal));
    }

    [Fact]
    public void Row_context_flyout_binds_commands_and_conditions_to_the_row()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = compiler.Compile("""
            view V {
                bind vm: VModel;
                list items: vm.Orders of Order {
                    ContextFlyout: MenuFlyout {
                        MenuFlyoutItem Text: item.Client -> vm.OpenCommand(item) enabled(item.CanOpen);
                    }
                    KeyboardAccelerators: { KeyboardAccelerator Key: "F5"; }
                    text item.Client;
                }
            }
            """, "test.flag");
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Equal("""<StackPanel Spacing="4" Padding="0,8" Background="Transparent">""", Line(xaml, "<StackPanel Spacing=\"4\""));
        Assert.Contains("<StackPanel.ContextFlyout>", xaml);
        Assert.Equal(
            """<MenuFlyoutItem Text="{x:Bind Client}" Command="{Binding ElementName=FlagstaffPage, Path=ViewModel.OpenCommand}" CommandParameter="{x:Bind}" IsEnabled="{x:Bind CanOpen}" />""",
            Line(xaml, "<MenuFlyoutItem"));
        // KeyboardAccelerators stay on the ListView.
        Assert.Contains("<ListView.KeyboardAccelerators>", xaml);
        var vm = Assert.Single(result.Files, f => f.Name == "VModel.g.cs").Content;
        Assert.Contains("OpenAsync(Order item)", vm);
        var order = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Order.g.cs").Content;
        Assert.Contains("public bool CanOpen { get; set; }", order);
    }

    [Fact]
    public void Item_command_in_a_list_view_level_property_element_is_FS2007()
    {
        var result = Compile("""
                list items: vm.Orders of Order {
                    Header: StackPanel { Button Content: "All" -> vm.OpenCommand(item); }
                    text item.Client;
                }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS2007");
        Assert.Contains("outside the row", d.Message);
    }

    [Fact]
    public void Ghost_row_action_copies_do_not_repeat_the_flyout()
    {
        var result = Compile("""
                table items: vm.Orders of Order {
                    col "Client" bind: item.Client;
                    button "More" when(item.CanAct) -> vm.ActCommand(item) Flyout: MenuFlyout { MenuFlyoutItem Text: "A"; };
                }
            """);
        var xaml = Xaml(result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xaml, "<Button.Flyout>"));
    }

    // ── view level ─────────────────────────────────────────────────

    [Fact]
    public void Page_resources_precede_the_root_grid_and_visual_states_go_inside_it()
    {
        var result = Compile("""
                Page.Resources: {
                    Style TargetType: Button { Setter Property: "Padding" Value: "12,8"; }
                }
                VisualStateManager.VisualStateGroups: { VisualStateGroup name: Layout; }
                text "Hello";
            """);
        NoErrors(result);
        var xaml = Xaml(result).ReplaceLineEndings("\n");
        Assert.Contains("    <Page.Resources>\n", xaml);
        var resources = xaml.IndexOf("<Page.Resources>", StringComparison.Ordinal);
        var grid = xaml.IndexOf("    <Grid>\n", StringComparison.Ordinal);
        var states = xaml.IndexOf("        <VisualStateManager.VisualStateGroups>\n", StringComparison.Ordinal);
        var rows = xaml.IndexOf("<Grid.RowDefinitions>", StringComparison.Ordinal);
        Assert.True(resources < grid && grid < states && states < rows, xaml);
    }

    // ── FS1033 ─────────────────────────────────────────────────────

    [Fact]
    public void Property_element_on_a_keyword_that_cannot_carry_it_is_FS1033()
    {
        var result = Compile("""
                dialog "Edit" when(vm.ShowEdit) {
                    ContextFlyout: MenuFlyout { MenuFlyoutItem Text: "x"; }
                    textbox "Name" vm.Name;
                }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1033");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("'dialog'", d.Message);
        Assert.Contains("'ContextFlyout'", d.Message);
        Assert.Equal(4, d.Location.Line);
    }

    [Fact]
    public void Attribute_on_a_structural_spacer_is_FS1033()
    {
        var result = Compile("""
                toolbar {
                    button "A" -> vm.ACommand;
                    spacer Margin: 8;
                    button "B" -> vm.BCommand;
                }
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1033");
        Assert.Contains("'spacer'", d.Message);
        Assert.Contains("'Margin'", d.Message);
    }

    [Fact]
    public void Overlay_roots_take_scalar_attributes_without_FS1033()
    {
        var result = Compile("""
                toast "Saved" when(vm.ShowSaved) Margin: 4;
                confirm "Delete?" when(vm.ShowDelete) -> vm.DeleteCommand AutomationProperties.Name: "Delete";
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS1033");
        var xaml = Xaml(result);
        Assert.Contains("""IsClosable="True" Margin="4" />""", xaml);
        Assert.Contains("""AutomationProperties.Name="Delete">""", xaml);
    }

    [Fact]
    public void Unknown_pass_through_prop_on_a_keyword_is_FS1031()
    {
        var result = Compile("""stat "X" vm.X Nonsense: 1;""");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1031");
        Assert.Contains("Nonsense", d.Message);
        Assert.Contains("Border", d.Message);
    }

    [Fact]
    public void Keyword_pass_through_values_are_shape_checked_and_refs_resolve_against_keyword_targets()
    {
        Assert.Single(Compile("""textbox "A" vm.A MaxLength: "lots";""").Diagnostics, d => d.Code == "FS1032");
        var result = Compile("""
                textbox "Name" vm.Name name: NameBox;
                text "Echo" Text: ref(NameBox).Text;
                text "Bad" Text: ref(NameBox).Txet;
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1031");
        Assert.Contains("'TextBox' (NameBox) has no property 'Txet'", d.Message);
    }

    [Fact]
    public void A_prop_the_variant_tag_lacks_is_FS1031()
    {
        // heading with icon: is a StackPanel; TextWrapping is a TextBlock property.
        var result = Compile("""heading "Orders" icon: home TextWrapping: wrap;""");
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1031");
        Assert.Contains("StackPanel", d.Message);
        Assert.Contains("TextWrapping", d.Message);
        // Margin is on both tags.
        NoErrors(Compile("""heading "Orders" icon: home Margin: "0,0,0,8";"""));
    }

    // ── property values are walked everywhere ─────────────────────

    [Fact]
    public void Navigation_inside_flyouts_generates_its_click_handlers()
    {
        var result = Compile("""
                button "More" {
                    Flyout: MenuFlyout { MenuFlyoutItem Text: "Open" -> navigate(DetailView); }
                }
                button "Go" {
                    Flyout: Flyout { button "Details" -> navigate(DetailView); }
                }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        var codeBehind = Assert.Single(result.Files, f => f.Name == "V.xaml.cs").Content;
        Assert.Contains("""<MenuFlyoutItem Text="Open" Click="OnNavigate_1" />""", xaml);
        Assert.Contains("private void OnNavigate_1(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)", codeBehind);
        Assert.Contains("""Tag="DetailView" Click="OnNavigateButtonClick""", xaml);
        Assert.Contains("private void OnNavigateButtonClick(", codeBehind);
    }

    [Fact]
    public void Item_bindings_inside_property_elements_in_a_row_template_type_the_row()
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = compiler.Compile("""
            view V {
                bind vm: VModel;
                list items: vm.Orders of Order {
                    Expander Header: item.Name {
                        text item.Notes;
                    }
                    button "More" {
                        Flyout: MenuFlyout { MenuFlyoutItem Text: item.Label enabled(item.CanAct); }
                    }
                }
            }
            """, "test.flag");
        NoErrors(result);
        var order = Assert.Single(compiler.GenerateAppScaffold().Files, f => f.Name == "Order.g.cs").Content;
        Assert.Contains("public string Name { get; set; }", order);
        Assert.Contains("public string Label { get; set; }", order);
        Assert.Contains("public bool CanAct { get; set; }", order);
        Assert.Contains("""<MenuFlyoutItem Text="{x:Bind Label}" IsEnabled="{x:Bind CanAct}" />""", Xaml(result));
    }

    [Fact]
    public void Type_name_properties_render_as_attribute_strings()
    {
        var result = Compile("""
                Page.Resources: {
                    Style TargetType: Button { Setter Property: Padding Value: "12,8"; Setter Property: "Margin" Value: "4"; }
                }
                text "x";
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("""<Style TargetType="Button">""", xaml);
        Assert.Contains("""<Setter Property="Padding" Value="12,8" />""", xaml);
        Assert.Contains("""<Setter Property="Margin" Value="4" />""", xaml);
        Assert.DoesNotContain("Style.TargetType", xaml);
    }

    [Fact]
    public void A_flyout_or_navigate_uri_is_an_action_so_no_FS1023()
    {
        var result = Compile("""
                button "More" { Flyout: MenuFlyout { MenuFlyoutItem Text: "A" -> vm.ACommand; } }
                link "Docs" NavigateUri: "https://example.com/docs";
                button "Nothing";
            """);
        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1023");
        Assert.Equal(5, d.Location.Line); // button "Nothing"
        Assert.Contains("""<HyperlinkButton Content="Docs" NavigateUri="https://example.com/docs" />""", Xaml(result));
    }
}
