using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// XAML emitted by keyword elements: table, select, text/stat formats, textbox, toggle, badge, icons, buttons and
/// links, toolbar, and the <c>xaml { }</c> escape. Overlays are in <see cref="OverlayTests"/>, <c>when()</c> in
/// <see cref="ConditionTests"/>.
/// </summary>
public class KeywordElementTests
{
    private static CompilationResult CompileView(string viewSource, string appName = "TestApp")
    {
        var compiler = new FlagstaffCompiler { AppNamespace = appName };
        return compiler.Compile(viewSource, "test.flag");
    }

    private static string CompileViewXaml(string viewSource, string appName = "TestApp")
    {
        var result = CompileView(viewSource, appName);
        return result.Files.First(f => f.Name.EndsWith(".xaml") && !f.Name.EndsWith(".xaml.cs")).Content;
    }

    /// <summary>Compiles one view, then the scaffold.</summary>
    private static CompilationResult Compile(string viewSource)
    {
        var compiler = new FlagstaffCompiler { AppNamespace = "TestApp" };
        var result = compiler.Compile(viewSource, "test.flag");
        result.Files.AddRange(compiler.GenerateAppScaffold().Files);
        return result;
    }

    private static string Xaml(CompilationResult result) =>
        result.Files.First(f => f.Name.EndsWith(".xaml", StringComparison.Ordinal) && f.Name != "App.xaml").Content;

    private static string File(CompilationResult result, string name) => result.Files.Single(f => f.Name == name).Content;

    private static void NoErrors(CompilationResult result) =>
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

    // ── table ──────────────────────────────────────────────────────

    [Fact]
    public void Table_datatemplate_strips_item_prefix()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                table items: vm.Items of Expense {
                    col "Vendor" bind: item.Vendor;
                    col "Amount" bind: item.Amount;
                }
            }
            """);

        Assert.Contains("{x:Bind Vendor}", xaml);
        Assert.Contains("{x:Bind Amount}", xaml);
        Assert.DoesNotContain("ViewModel.item.", xaml);
    }

    [Fact]
    public void Table_column_applies_format_converter()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                table items: vm.Items of Expense {
                    col "Amount" bind: item.Amount format: currency;
                }
            }
            """);

        Assert.Contains("CurrencyConverter", xaml);
        Assert.Contains("{x:Bind Amount, Converter={StaticResource CurrencyConverter}}", xaml);
    }

    [Fact]
    public void Table_column_applies_date_converter()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                table items: vm.Items of Expense {
                    col "Date" bind: item.ExpenseDate format: date;
                }
            }
            """);

        Assert.Contains("DateConverter", xaml);
        Assert.Contains("{x:Bind ExpenseDate, Converter={StaticResource DateConverter}}", xaml);
    }

    [Fact]
    public void Table_selected_emits_selecteditem_binding()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                table items: vm.Expenses selected: vm.SelectedExpense of Expense {
                    col "Vendor" bind: item.Vendor;
                }
            }
            """);

        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.SelectedExpense, Mode=TwoWay}\"", xaml);
        Assert.Contains("SelectionMode=\"Single\"", xaml);
    }

    [Fact]
    public void Table_item_template_has_x_data_type()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                table items: vm.Expenses of Expense {
                    col "Vendor" bind: item.Vendor;
                    col "Amount" bind: item.Amount format: currency;
                }
            }
            """);

        Assert.Contains("x:DataType=\"vm:Expense\"", xaml);
    }

    [Fact]
    public void Table_header_template_has_no_data_type()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                table items: vm.Items of Row {
                    col "Name" bind: item.Name;
                }
            }
            """);

        var headerStart = xaml.IndexOf("HeaderTemplate");
        var headerEnd = xaml.IndexOf("/ListView.HeaderTemplate");
        var headerSection = xaml[headerStart..headerEnd];
        Assert.DoesNotContain("x:DataType", headerSection);
    }

    [Fact]
    public void Row_actions_reserve_their_width_in_header_and_rows()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row {
                    col "A" bind: item.A width: 2*;
                    button "Delete" -> vm.DeleteCommand(item) when(item.CanDelete) variant: danger;
                }
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        const string ghost = "<Button Content=\"Delete\" Style=\"{StaticResource FlagstaffDangerButton}\" Opacity=\"0\" IsHitTestVisible=\"False\" IsTabStop=\"False\" AutomationProperties.AccessibilityView=\"Raw\" />";
        Assert.Equal(2, Regex.Matches(xaml, Regex.Escape(ghost)).Count); // header + row
        Assert.Contains("Visibility=\"{x:Bind CanDelete, Converter={StaticResource BoolToVisibility}}\"", xaml);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS1025");
    }

    [Fact]
    public void Row_action_buttons_keep_variant_and_universal_props()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row {
                    col "A" bind: item.A;
                    button "Delete" -> vm.DeleteCommand(item) variant: danger tooltip: "Remove" name: DeleteButton;
                }
            }
            """);
        NoErrors(result);
        Assert.Contains(
            "<Button Content=\"Delete\" Command=\"{Binding ElementName=FlagstaffPage, Path=ViewModel.DeleteCommand}\" CommandParameter=\"{x:Bind}\" Style=\"{StaticResource FlagstaffDangerButton}\" x:Name=\"DeleteButton\" ToolTipService.ToolTip=\"Remove\" />",
            Xaml(result));
    }

    [Fact]
    public void Fill_table_with_empty_state_uses_a_single_cell_grid()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row fill { col "A" bind: item.A; empty "Nothing."; }
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.DoesNotContain("<StackPanel Spacing=\"8\">", xaml);
        Assert.Matches(@"<Border Grid.Row=""0"">\s*<Grid>\s*<ListView", xaml);
        Assert.Contains("Text=\"Nothing.\" Style=\"{StaticResource FlagstaffEmptyState}\" VerticalAlignment=\"Center\"", xaml);
    }

    // ── Item bindings in rows ──────────────────────────────────────

    [Fact]
    public void Item_bindings_and_item_conditions_have_no_mode_but_vm_bindings_stay_oneway()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                heading vm.Title;
                list items: vm.Rows of Row {
                    heading item.Name;
                    text item.Note when(item.IsVisible);
                    progress item.Done;
                    button "Go" -> vm.GoCommand(item) enabled(item.Count > 1);
                }
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("Text=\"{x:Bind ViewModel.Title, Mode=OneWay}\"", xaml);
        Assert.Contains("Text=\"{x:Bind Name}\"", xaml);
        Assert.Contains("Text=\"{x:Bind Note}\" Visibility=\"{x:Bind IsVisible, Converter={StaticResource BoolToVisibility}}\"", xaml);
        Assert.Contains("Value=\"{x:Bind Done}\"", xaml);
        Assert.Contains("IsEnabled=\"{x:Bind local:V.Enabled_1(Count)}\"", xaml);
    }

    // ── select ─────────────────────────────────────────────────────

    [Fact]
    public void Select_range_emits_typed_int_items()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                select vm.Year options: 2019..2021;
            }
            """);

        Assert.Contains("<x:Int32>2019</x:Int32>", xaml);
        Assert.Contains("<x:Int32>2020</x:Int32>", xaml);
        Assert.Contains("<x:Int32>2021</x:Int32>", xaml);
        Assert.DoesNotContain("ComboBoxItem", xaml);
    }

    [Fact]
    public void Select_with_items_emits_itemssource()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                select "Category" vm.Category items: vm.CategoryList;
            }
            """);

        Assert.Contains("ItemsSource=\"{x:Bind ViewModel.CategoryList, Mode=OneWay}\"", xaml);
        Assert.Contains("SelectedItem=\"{x:Bind ViewModel.Category, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void Select_with_of_type_emits_data_type_on_item_template()
    {
        var xaml = CompileViewXaml("""
            view ListPage {
                bind vm: ListViewModel;
                select "Tank" vm.SelectedTank items: vm.Tanks of AquariumItem;
            }
            """);
        Assert.Contains("x:DataType=\"vm:AquariumItem\"", xaml);
        Assert.Contains("ComboBox.ItemTemplate", xaml);
        Assert.Contains("DataTemplate", xaml);
    }

    [Fact]
    public void Select_without_of_type_renders_self_closing()
    {
        var xaml = CompileViewXaml("""
            view ListPage {
                bind vm: ListViewModel;
                select "Status" vm.SelectedStatus items: vm.Statuses;
            }
            """);
        Assert.DoesNotContain("ComboBox.ItemTemplate", xaml);
    }

    [Fact]
    public void Select_takes_common_attrs_placeholder_and_hint()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                select "Status" vm.Status options: ["A", "B"] placeholder: "Pick one" hint: "Required" tooltip: "Status" name: StatusBox width: 200 align: left;
            }
            """);
        NoErrors(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS1002");
        Assert.Contains(
            "<ComboBox Header=\"Status\" x:Name=\"StatusBox\" Width=\"200\" HorizontalAlignment=\"Left\" ToolTipService.ToolTip=\"Status\" PlaceholderText=\"Pick one\" Description=\"Required\" SelectedItem=\"{x:Bind ViewModel.Status, Mode=TwoWay}\">",
            Xaml(result));
    }

    // ── text, stat and format: ─────────────────────────────────────

    [Fact]
    public void Text_with_positional_binding_emits_xbind()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                card "Email" {
                    text vm.Subject;
                    text vm.SenderDisplay;
                }
            }
            """);

        Assert.Contains("{x:Bind ViewModel.Subject, Mode=OneWay}", xaml);
        Assert.Contains("{x:Bind ViewModel.SenderDisplay, Mode=OneWay}", xaml);
        Assert.DoesNotContain("Text=\"\" />", xaml);
    }

    [Fact]
    public void Stat_applies_currency_format_converter()
    {
        var xaml = CompileViewXaml("""
            view DashView {
                bind vm: DashViewModel;
                stat "Revenue" vm.Revenue format: currency;
            }
            """);
        Assert.Contains("Converter={StaticResource CurrencyConverter}", xaml);
    }

    [Fact]
    public void Stat_applies_percent_format_converter()
    {
        var xaml = CompileViewXaml("""
            view DashView {
                bind vm: DashViewModel;
                stat "Rate" vm.Rate format: percent;
            }
            """);
        Assert.Contains("Converter={StaticResource PercentConverter}", xaml);
    }

    [Fact]
    public void Stat_without_format_has_no_converter()
    {
        var xaml = CompileViewXaml("""
            view DashView {
                bind vm: DashViewModel;
                stat "Count" vm.Total;
            }
            """);
        Assert.DoesNotContain("Converter=", xaml);
    }

    [Fact]
    public void Number_format_applies_the_number_converter_on_stat_line_and_text()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                stat "Rate" vm.Rate format: number(2);
                summary { line "Hours" vm.Hours format: number(1); }
                text vm.Score format: number;
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("{x:Bind ViewModel.Rate, Mode=OneWay, Converter={StaticResource NumberConverter}, ConverterParameter=F2}", xaml);
        Assert.Contains("{x:Bind ViewModel.Hours, Mode=OneWay, Converter={StaticResource NumberConverter}, ConverterParameter=F1}", xaml);
        Assert.Contains("{x:Bind ViewModel.Score, Mode=OneWay, Converter={StaticResource NumberConverter}}", xaml);
    }

    // ── textbox and textarea ───────────────────────────────────────

    [Fact]
    public void Textbox_and_textarea_update_the_source_on_every_keystroke()
    {
        var xaml = Xaml(Compile("view V { bind vm: VM; textbox \"T\" vm.Title; textarea \"N\" vm.Notes; }"));
        Assert.Equal(2, Regex.Matches(xaml, Regex.Escape("Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}")).Count);
    }

    // ── toggle ─────────────────────────────────────────────────────

    [Fact]
    public void Toggle_emits_toggle_switch_with_binding()
    {
        var xaml = CompileViewXaml("""
            view SettingsView {
                bind vm: SettingsViewModel;
                toggle "Dark Mode" vm.IsDarkMode;
            }
            """);
        Assert.Contains("ToggleSwitch", xaml);
        Assert.Contains("Header=\"Dark Mode\"", xaml);
        Assert.Contains("IsOn=\"{x:Bind ViewModel.IsDarkMode, Mode=TwoWay}\"", xaml);
    }

    [Fact]
    public void Toggle_supports_when_visibility()
    {
        var xaml = CompileViewXaml("""
            view TestView {
                bind vm: TestViewModel;
                toggle "Extra" vm.ShowExtra when(vm.IsAdvanced);
            }
            """);
        Assert.Contains("ToggleSwitch", xaml);
        Assert.Contains("Visibility=", xaml);
    }

    // ── badge ──────────────────────────────────────────────────────

    [Fact]
    public void Badge_int_binding_emits_info_badge()
    {
        var source = """
            view TestView {
                bind vm: TestViewModel;
                stat "Count" vm.Count when(vm.Count > 0);
                badge vm.Count;
            }
            """;
        var xaml = CompileViewXaml(source);
        Assert.Contains("InfoBadge", xaml);
    }

    [Fact]
    public void Badge_string_binding_emits_styled_pill()
    {
        var source = """
            view TestView {
                bind vm: TestViewModel;
                badge vm.Status;
            }
            """;
        var xaml = CompileViewXaml(source);
        Assert.Contains("<Border", xaml);
        Assert.Contains("SystemAccentColor", xaml);
        Assert.Contains("TextBlock", xaml);
        Assert.DoesNotContain("InfoBadge", xaml);
    }

    [Fact]
    public void Badge_hugs_its_text_unless_align_is_set()
    {
        var xaml = Xaml(Compile("view V { badge \"New\"; badge \"Right\" align: right; }"));
        Assert.Contains("VerticalAlignment=\"Center\" HorizontalAlignment=\"Left\">", xaml);
        Assert.Contains("VerticalAlignment=\"Center\" HorizontalAlignment=\"Right\">", xaml);
        Assert.Single(Regex.Matches(xaml, "HorizontalAlignment=\"Left\""));
    }

    // ── Icons ──────────────────────────────────────────────────────

    [Fact]
    public void Heading_with_icon_emits_symbol_icon()
    {
        var xaml = CompileViewXaml("""
            view V {
                heading "Dashboard" icon: home;
            }
            """);

        Assert.Contains("<SymbolIcon Symbol=\"Home\" />", xaml);
        Assert.DoesNotContain("FontIcon", xaml);
    }

    [Fact]
    public void Card_with_icon_emits_symbol_icon()
    {
        var xaml = CompileViewXaml("""
            view V {
                card "Revenue" icon: globe {
                    text "content";
                }
            }
            """);

        Assert.Contains("<SymbolIcon Symbol=\"Globe\" />", xaml);
        Assert.DoesNotContain("FontIcon", xaml);
    }

    [Fact]
    public void Button_and_filepicker_icon_render_symbol_and_label()
    {
        var result = Compile("""
            view V { bind vm: VM; button "Add" -> vm.AddCommand icon: add; filePicker "Open" -> vm.OpenCommand icon: open-file; button "Plain" -> vm.PlainCommand; }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Matches(@"<Button Command=""\{x:Bind ViewModel.AddCommand, Mode=OneWay\}"">\s*<StackPanel Orientation=""Horizontal"" Spacing=""8"">\s*<SymbolIcon Symbol=""Add"" />\s*<TextBlock Text=""Add"" />", xaml);
        Assert.Contains("<SymbolIcon Symbol=\"OpenFile\" />", xaml);
        Assert.Contains("<Button Content=\"Plain\" Command=\"{x:Bind ViewModel.PlainCommand, Mode=OneWay}\" />", xaml);
    }

    [Fact]
    public void Icon_name_normalizes_hyphenated_to_pascal_case()
    {
        Assert.Equal("CalendarDay", XamlGenerator.NormalizeIconName("calendar-day"));
        Assert.Equal("Home", XamlGenerator.NormalizeIconName("home"));
        Assert.Equal("MailForward", XamlGenerator.NormalizeIconName("mail-forward"));
    }

    // ── Buttons, links and command targets ─────────────────────────

    [Fact]
    public void Command_target_without_suffix_binds_the_generated_command()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                button "Save" -> vm.Save;
                button "Save again" -> vm.SaveCommand;
                list items: vm.Rows of Row -> vm.Open(item) { button "Edit" -> vm.Edit(item); }
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("Command=\"{x:Bind ViewModel.SaveCommand, Mode=OneWay}\"", xaml);
        Assert.DoesNotContain("ViewModel.Save,", xaml);
        Assert.Contains("Path=ViewModel.EditCommand}", xaml);
        Assert.Contains("ViewModel.OpenCommand.Execute(item);", File(result, "V.xaml.cs"));
        var vm = File(result, "VM.g.cs");
        Assert.Single(Regex.Matches(vm, "private async Task SaveAsync\\(\\)"));
        Assert.Contains("private async Task EditAsync(Row item)", vm);
    }

    [Fact]
    public void Link_navigates_or_runs_a_command()
    {
        var result = Compile("""
            view V { bind vm: VM; link "Other" -> OtherView; link "Help" -> navigate(HelpView); link "Run" -> vm.RunCommand; }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.Contains("<HyperlinkButton Content=\"Other\" Tag=\"OtherView\" Click=\"OnNavigateButtonClick\" />", xaml);
        Assert.Contains("<HyperlinkButton Content=\"Help\" Tag=\"HelpView\" Click=\"OnNavigateButtonClick\" />", xaml);
        Assert.Contains("<HyperlinkButton Content=\"Run\" Command=\"{x:Bind ViewModel.RunCommand, Mode=OneWay}\" />", xaml);
        Assert.Contains("private void OnNavigateButtonClick(", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void FilePicker_with_command_emits_command_binding()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                filePicker "Attach File" -> vm.AttachFileCommand;
            }
            """);

        Assert.Contains("Command=\"{x:Bind ViewModel.AttachFileCommand, Mode=OneWay}\"", xaml);
        Assert.Contains("FlagstaffFilePickerButton", xaml);
    }

    // ── toolbar ────────────────────────────────────────────────────

    [Fact]
    public void Toolbar_groups_right_items_into_single_stack_panel()
    {
        var xaml = CompileViewXaml("""
            view V {
                toolbar {
                    heading "Title";
                    spacer;
                    select vm.Year options: 2019..2026;
                    button "Add";
                    button "Import";
                }
            }
            """);

        var rightPanelCount = xaml.Split("Grid.Column=\"2\"").Length - 1;
        Assert.Equal(1, rightPanelCount);
    }

    // ── xaml { } escape ────────────────────────────────────────────

    [Fact]
    public void Xaml_escape_hatch_preserves_raw_xaml_content()
    {
        var xaml = CompileViewXaml("""
            view ChartView {
                bind vm: ChartViewModel;
                xaml {
                    <CartesianChart Series="{x:Bind ViewModel.Series}" />
                }
            }
            """);
        Assert.Contains("<CartesianChart Series=\"{x:Bind ViewModel.Series}\" />", xaml);
    }

    [Fact]
    public void Xaml_escape_hatch_handles_nested_braces()
    {
        var xaml = CompileViewXaml("""
            view TestView {
                bind vm: TestViewModel;
                xaml {
                    <Grid>
                        <TextBlock Text="inner" />
                    </Grid>
                }
            }
            """);
        Assert.Contains("<Grid>", xaml);
        Assert.Contains("<TextBlock Text=\"inner\" />", xaml);
        Assert.Contains("</Grid>", xaml);
    }
}
