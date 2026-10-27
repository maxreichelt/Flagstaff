using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// <c>on Event -> target;</c> clauses: <c>Event="OnEvent_n"</c> on the root tag, a
/// typed handler in the code-behind that runs the command / navigates / cancels, the command in the ViewModel, and the
/// validation codes FS1034 (unknown event), FS1043 (duplicate), FS2015 (not a command), FS2007 ((item) outside a row),
/// FS1024 (cancel outside a dialog).
/// </summary>
public class EventTests
{
    private static CompilationResult Compile(string body) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile($$"""
            view V {
                bind vm: VModel;
            {{body}}
            }
            """, "test.flag");

    private static string File(CompilationResult result, string name) =>
        Assert.Single(result.Files, f => f.Name == name).Content;

    private static string Xaml(CompilationResult result) => File(result, "V.xaml");
    private static string CodeBehind(CompilationResult result) => File(result, "V.xaml.cs");
    private static string ViewModel(CompilationResult result) => File(result, "VModel.g.cs");

    private static string Line(string text, string startsWith) =>
        Assert.Single(text.Split('\n'), l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal)).Trim();

    private static void NoErrors(CompilationResult result) =>
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(CompilationResult result, string code) =>
        Assert.Single(result.Diagnostics, d => d.Code == code);

    // ── Emission ───────────────────────────────────────────────────

    [Fact]
    public void Click_on_a_generic_button_wires_a_typed_handler_that_runs_the_command()
    {
        var result = Compile("""    Button Content: "Save" on Click -> vm.SaveCommand;""");
        NoErrors(result);
        Assert.Equal("<Button Content=\"Save\" Click=\"OnEvent_1\" />", Line(Xaml(result), "<Button"));
        var cs = CodeBehind(result);
        Assert.Contains("private void OnEvent_1(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)", cs);
        Assert.Contains("if (ViewModel.SaveCommand.CanExecute(null)) ViewModel.SaveCommand.Execute(null);", cs);
        Assert.Contains("[RelayCommand] private async Task SaveAsync() { await OnSaveAsync(); }", ViewModel(result));
    }

    [Fact]
    public void Target_without_Command_suffix_normalizes_like_an_arrow()
    {
        var result = Compile("""    Border on Tapped -> vm.Refresh { text "x"; }""");
        NoErrors(result);
        Assert.Contains("ViewModel.RefreshCommand.Execute(null)", CodeBehind(result));
        Assert.Contains("private async Task RefreshAsync()", ViewModel(result));
        Assert.Contains("Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e", CodeBehind(result));
    }

    [Fact]
    public void Keyword_elements_take_events_on_their_root_tag()
    {
        var result = Compile("""
                textbox "Search" vm.Query on KeyDown -> vm.SubmitCommand;
                select "Status" vm.Status options: ["Open", "Closed"] on SelectionChanged -> vm.FilterCommand;
            """);
        NoErrors(result);
        Assert.Contains("KeyDown=\"OnEvent_1\"", Line(Xaml(result), "<TextBox"));
        Assert.Contains("SelectionChanged=\"OnEvent_2\"", Line(Xaml(result), "<ComboBox"));
        var cs = CodeBehind(result);
        Assert.Contains("OnEvent_1(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)", cs);
        Assert.Contains("OnEvent_2(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)", cs);
    }

    [Fact]
    public void Event_lines_inside_a_block_are_emitted_like_header_clauses()
    {
        var result = Compile("""
                card "Hover" {
                    on PointerEntered -> vm.Hover;
                    text "x";
                }
            """);
        NoErrors(result);
        Assert.Contains("PointerEntered=\"OnEvent_1\"", Line(Xaml(result), "<Border Style=\"{StaticResource FlagstaffCard}\""));
    }

    [Fact]
    public void Navigate_target_navigates_in_the_handler()
    {
        var result = Compile("""    Border on DoubleTapped -> navigate(DetailView) { text "x"; }""");
        NoErrors(result);
        Assert.Contains("Frame.Navigate(typeof(DetailView));", CodeBehind(result));
    }

    [Fact]
    public void Item_in_a_list_template_is_the_row_data_context()
    {
        var result = Compile("""
                list items: vm.Items of Row {
                    Button Content: "Open" on Click -> vm.OpenCommand(item);
                }
            """);
        NoErrors(result);
        Assert.Contains(
            "if (sender is Microsoft.UI.Xaml.FrameworkElement { DataContext: ViewModels.Row item } && ViewModel.OpenCommand.CanExecute(item)) ViewModel.OpenCommand.Execute(item);",
            CodeBehind(result));
        Assert.Contains("private async Task OpenAsync(Row item)", ViewModel(result));
    }

    [Fact]
    public void Item_on_SelectionChanged_is_the_added_item()
    {
        var result = Compile("""    select "Owner" vm.Owner items: vm.Owners of Person on SelectionChanged -> vm.AssignCommand(item);""");
        NoErrors(result);
        Assert.Contains("e.AddedItems.Count > 0 && e.AddedItems[0] is ViewModels.Person item", CodeBehind(result));
        Assert.Contains("private async Task AssignAsync(Person item)", ViewModel(result));
    }

    [Fact]
    public void ItemClick_on_a_generic_ListView_enables_item_click()
    {
        var result = Compile("""    ListView ItemsSource: vm.Rows of Row on ItemClick -> vm.OpenCommand(item);""");
        NoErrors(result);
        Assert.Contains("ItemClick=\"OnEvent_1\" IsItemClickEnabled=\"True\"", Line(Xaml(result), "<ListView"));
        Assert.Contains("e.ClickedItem is ViewModels.Row item", CodeBehind(result));
    }

    [Fact]
    public void ItemClick_does_not_repeat_an_explicit_IsItemClickEnabled()
    {
        var result = Compile("""    ListView ItemsSource: vm.Rows IsItemClickEnabled: true on ItemClick -> vm.OpenCommand;""");
        NoErrors(result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Line(Xaml(result), "<ListView"), "IsItemClickEnabled"));
    }

    [Fact]
    public void Table_and_list_carry_events_on_their_ListView()
    {
        var result = Compile("""
                table items: vm.Orders of Order on SelectionChanged -> vm.PreviewCommand(item) {
                    col "Number" item.Number;
                }
            """);
        NoErrors(result);
        Assert.Contains("SelectionChanged=\"OnEvent_1\"", Line(Xaml(result), "<ListView "));
        Assert.Contains("e.AddedItems[0] is ViewModels.Order item", CodeBehind(result));
    }

    [Fact]
    public void Cancel_inside_a_dialog_runs_the_generated_cancel_command()
    {
        var result = Compile("""
                dialog "Edit" when(vm.IsEditing) {
                    textbox "Name" vm.Name on KeyDown -> cancel;
                    button "Save" -> vm.SaveCommand;
                }
            """);
        NoErrors(result);
        Assert.Contains("ViewModel.CancelIsEditingCommand.Execute(null);", CodeBehind(result));
        Assert.Contains("[RelayCommand] private void CancelIsEditing() => IsEditing = false;", ViewModel(result));
    }

    [Fact]
    public void Button_with_arrow_and_on_Click_is_allowed_and_not_FS1023()
    {
        var result = Compile("""
                button "Save" -> vm.SaveCommand on Click -> vm.LogCommand;
                button "Log" on Click -> vm.LogCommand;
            """);
        NoErrors(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS1023");
        Assert.Contains("Command=\"{x:Bind ViewModel.SaveCommand, Mode=OneWay}\" Click=\"OnEvent_1\"", Xaml(result));
    }

    [Fact]
    public void Handler_numbering_is_per_view_in_document_order_and_deterministic()
    {
        const string body = """
                Slider Value: vm.Volume on ValueChanged -> vm.VolumeCommand;
                TextBox Text: vm.Query on TextChanged -> vm.SearchCommand on GotFocus -> vm.FocusCommand;
                list items: vm.Items of Row {
                    Button Content: "Open" on Click -> vm.OpenCommand(item);
                }
            """;
        var first = Compile(body);
        var second = Compile(body);
        Assert.Equal(CodeBehind(first), CodeBehind(second));
        Assert.Equal(Xaml(first), Xaml(second));
        var cs = CodeBehind(first);
        Assert.True(cs.IndexOf("OnEvent_1(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs", StringComparison.Ordinal) >= 0);
        Assert.Contains("OnEvent_2(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs", cs);
        Assert.Contains("OnEvent_3(object sender, Microsoft.UI.Xaml.RoutedEventArgs", cs);
        Assert.Contains("OnEvent_4(object sender, Microsoft.UI.Xaml.RoutedEventArgs", cs);
        Assert.DoesNotContain("OnEvent_5", cs);
    }

    [Fact]
    public void Third_party_elements_get_an_object_args_handler()
    {
        var result = new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile("""
            use "LiveChartsCore.SkiaSharpView.WinUI" as lvc;
            view V {
                bind vm: VModel;
                lvc.CartesianChart on DataPointerDown -> vm.PickCommand;
            }
            """, "test.flag");
        NoErrors(result);
        Assert.Contains("OnEvent_1(object sender, object e)", CodeBehind(result));
    }

    // ── Diagnostics ────────────────────────────────────────────────

    [Fact]
    public void Unknown_event_is_FS1034_with_a_suggestion()
    {
        var result = Compile("""    Button Content: "Save" on Clcik -> vm.SaveCommand;""");
        var d = Error(result, "FS1034");
        Assert.Contains("'Button' has no event 'Clcik'; did you mean 'Click'?", d.Message);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void Unknown_event_on_a_keyword_names_its_root_type_and_lists_events()
    {
        var result = Compile("""    textbox "Name" vm.Name on Zzzzzzzz -> vm.XCommand;""");
        var d = Error(result, "FS1034");
        Assert.Contains("'textbox' (TextBox) has no event 'Zzzzzzzz'", d.Message);
        Assert.StartsWith("'TextBox' events: BeforeTextChanging, ", d.Hint);
    }

    [Fact]
    public void Event_on_a_keyword_without_a_root_tag_is_FS1034()
    {
        var result = Compile("""
                table items: vm.Orders of Order {
                    col "Number" item.Number on Tapped -> vm.XCommand;
                }
            """);
        Assert.Contains("'col' has no XAML element of its own", Error(result, "FS1034").Message);
    }

    [Fact]
    public void Same_event_twice_is_FS1043()
    {
        var result = Compile("""    Button Content: "Save" on Click -> vm.SaveCommand on Click -> vm.LogCommand;""");
        Assert.Contains("'on Click' is written twice on 'Button'", Error(result, "FS1043").Message);
    }

    [Fact]
    public void Event_already_wired_by_the_arrow_is_FS1043()
    {
        var result = Compile("""
                button "Go" -> navigate(DetailView) on Click -> vm.LogCommand;
                list items: vm.Items of Row -> vm.OpenCommand(item) on ItemClick -> vm.LogCommand { text item.Name; }
            """);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "FS1043"));
    }

    [Fact]
    public void Item_outside_a_template_is_FS2007()
    {
        var result = Compile("""    Button Content: "Open" on Click -> vm.OpenCommand(item);""");
        Assert.Contains("only valid inside a table or list", Error(result, "FS2007").Message);
    }

    [Fact]
    public void Item_on_SelectionChanged_without_of_is_FS2007()
    {
        var result = Compile("""    select "Status" vm.Status items: vm.Statuses on SelectionChanged -> vm.FilterCommand(item);""");
        Error(result, "FS2007");
    }

    [Fact]
    public void Target_that_is_a_bound_property_is_FS2015()
    {
        var result = Compile("""
                textbox "Title" vm.Title;
                Button Content: "x" on Click -> vm.Title;
            """);
        Assert.Contains("targets a property", Error(result, "FS2015").Message);
    }

    [Fact]
    public void Cancel_outside_a_dialog_is_FS1024()
    {
        var result = Compile("""    textbox "Name" vm.Name on KeyDown -> cancel;""");
        Assert.Contains("on KeyDown -> cancel", Error(result, "FS1024").Message);
    }

    // ── Events and pass-through on variant tags ────────────────────

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
}
