using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>Regression tests for the Wave 2 fix pass (bugs reported by TaskTrack and an external 0.2 app).</summary>
public class FixPassTests
{
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

    // ── 1. Stable output ──────────────────────────────────────────

    [Fact]
    public void Output_does_not_depend_on_the_source_path_prefix()
    {
        const string app = """
            app StableApp { nav side { link "Settings" -> SettingsView; } }
            """;
        const string settings = """
            view SettingsView {
                bind vm: SettingsViewModel;
                onload -> vm.Load;
                text "a" when(vm.Count > 2);
                list items: vm.Rows of Row { text item.Name format: currency; }
                list items: vm.Rows of Row { text item.Name format: date; }
                toast "Saved" when(vm.ShowSaved) duration: 3000;
            }
            """;

        static CompilationResult Run(string dir) =>
            new FlagstaffCompiler().CompileAll(
            [
                new SourceFile(Path.Combine(dir, "app.flag"), app),
                new SourceFile(Path.Combine(dir, "settings.flag"), settings),
            ]);

        var a = Run(Path.Combine("src", "App", "Flags"));
        var b = Run("Flags");

        Assert.Equal(a.Files.Select(f => f.Name), b.Files.Select(f => f.Name));
        foreach (var (fa, fb) in a.Files.Zip(b.Files))
            Assert.Equal(fa.Content, fb.Content);
        Assert.Contains("at settings.flag(", File(a, "SettingsView.xaml.cs"));
        // Diagnostic messages (not locations) are path-independent too (FS2010 names the other usage).
        Assert.Contains(a.Diagnostics, d => d.Code == "FS2010");
        Assert.Equal(a.Diagnostics.Select(d => d.Message), b.Diagnostics.Select(d => d.Message));
    }

    // ── 2. onload ─────────────────────────────────────────────────

    [Fact]
    public void Onload_command_flows_exceptions_to_the_awaited_task_and_debug_rethrows()
    {
        var result = Compile("""
            view V { bind vm: VM; onload -> vm.Load; button "Refresh" -> vm.RefreshCommand; }
            """);
        var vm = File(result, "VM.g.cs");
        Assert.Contains("[RelayCommand(FlowExceptionsToTaskScheduler = true)] private async Task LoadAsync()", vm);
        Assert.Contains("[RelayCommand] private async Task RefreshAsync()", vm);
        var cs = File(result, "V.xaml.cs");
        Assert.Matches(@"catch \(Exception ex\)\s*\{\s*System\.Diagnostics\.Debug\.WriteLine\(.*\);\s*#if DEBUG\s*throw;", cs);
    }

    // ── 3. Hoisted overlays take no slot ──────────────────────────

    [Fact]
    public void Hoisted_overlays_get_no_row_in_a_fill_grid()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row fill { col "A" bind: item.A; }
                dialog "Edit" when(vm.IsOpen) { button "Close" -> cancel; }
                toast "Saved" when(vm.ShowSaved);
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        var body = xaml[xaml.IndexOf("<Grid RowSpacing=\"16\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"FlagstaffOverlay\"", StringComparison.Ordinal)];
        var rows = body[..body.IndexOf("</Grid.RowDefinitions>", StringComparison.Ordinal)];
        Assert.Single(Regex.Matches(rows, "<RowDefinition "));             // only the table's * row
        Assert.Single(Regex.Matches(body, "<Border Grid.Row="));           // no wrapper for the dialog / toast
        Assert.Contains("<!-- dialog \"Edit\": rendered in FlagstaffOverlay -->", body);
        Assert.Contains("<!-- toast \"Saved\": rendered in FlagstaffOverlay -->", body);
    }

    [Fact]
    public void Hoisted_overlays_get_no_column_in_row_or_split()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                row { textbox "A" vm.A; toast "Saved" when(vm.ShowSaved); textbox "B" vm.B; }
                split { text "L"; confirm "Sure?" when(vm.Ask) -> vm.DoCommand; text "R"; }
            }
            """);
        NoErrors(result);
        var xaml = Xaml(result);
        Assert.DoesNotContain("Grid.Column=\"2\"", xaml);
        Assert.Equal(4, Regex.Matches(xaml, "<ColumnDefinition Width=\"\\*\" />").Count);
    }

    // ── 4. Dead buttons, -> cancel ────────────────────────────────

    [Fact]
    public void Button_filepicker_and_link_without_arrow_warn_fs1023()
    {
        var result = Compile("""
            view V { button "Cancel"; filePicker "Pick"; link "Docs"; button "Ok" -> navigate(V); }
            """);
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == "FS1023" && d.Severity == DiagnosticSeverity.Warning));
        Assert.Contains(result.Diagnostics, d => d.Message == "button has no action; add -> vm.XCommand or -> navigate(View)");
    }

    [Fact]
    public void Arrow_cancel_in_a_dialog_binds_the_generated_cancel_command()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                dialog "Edit" when(vm.IsEditOpen) {
                    textbox "Name" vm.Name;
                    button "Cancel" -> cancel;
                    button "Save" -> vm.SaveCommand;
                }
            }
            """);
        NoErrors(result);
        Assert.Contains("<Button Content=\"Cancel\" Command=\"{x:Bind ViewModel.CancelIsEditOpenCommand, Mode=OneWay}\" />", Xaml(result));
        Assert.Contains("[RelayCommand] private void CancelIsEditOpen() => IsEditOpen = false;", File(result, "VM.g.cs"));
        Assert.DoesNotContain("OnNavigateButtonClick", File(result, "V.xaml.cs"));
    }

    [Fact]
    public void Arrow_cancel_in_a_confirm_with_own_buttons_reuses_the_cancel_command()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                confirm "Sure?" when(vm.Ask) { button "No" -> cancel; button "Yes" -> vm.DoCommand; }
            }
            """);
        NoErrors(result);
        Assert.Contains("Command=\"{x:Bind ViewModel.CancelAskCommand, Mode=OneWay}\"", Xaml(result));
        Assert.Single(Regex.Matches(File(result, "VM.g.cs"), "private void CancelAsk\\(\\)"));
    }

    [Fact]
    public void Arrow_cancel_outside_a_dialog_is_fs1024()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                button "Cancel" -> cancel;
                dialog "D" when(vm.Open) { link "x" -> cancel; button "Close" -> cancel; }
            }
            """);
        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == "FS1024" && d.Severity == DiagnosticSeverity.Error));
    }

    // ── 5. Async startup hook ─────────────────────────────────────

    [Fact]
    public void App_scaffold_runs_async_startup_steps_before_the_main_window()
    {
        var cs = AppScaffoldGenerator.GenerateAppXamlCs("TestApp", ["VM"]).Content;
        Assert.Contains("protected override async void OnLaunched(LaunchActivatedEventArgs args)", cs);
        Assert.Contains("partial void OnAppStarting(List<Func<Task>> steps);", cs);
        Assert.Contains("partial void OnAppLaunched();", cs);
        var build = cs.IndexOf("Services = services.BuildServiceProvider();", StringComparison.Ordinal);
        var starting = cs.IndexOf("OnAppStarting(steps);", StringComparison.Ordinal);
        var await_ = cs.IndexOf("await step();", StringComparison.Ordinal);
        var window = cs.IndexOf("MainWindow = new MainWindow();", StringComparison.Ordinal);
        var launched = cs.IndexOf("OnAppLaunched();", StringComparison.Ordinal);
        Assert.True(build < starting && starting < await_ && await_ < window && window < launched);
    }

    // ── 7./8. Item types qualified, item bindings OneTime ─────────

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

    [Fact]
    public void Condition_functions_qualify_item_types_with_the_viewmodels_namespace()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                list items: vm.Rows selected: vm.Selected of Row { text item.Name; }
                text "x" when(vm.Selected != null && vm.Rows != null);
            }
            """);
        NoErrors(result);
        Assert.Contains(
            "When_1(ViewModels.Row? selected, System.Collections.ObjectModel.ObservableCollection<ViewModels.Row> rows)",
            File(result, "V.xaml.cs"));
    }

    // ── 9. progress ───────────────────────────────────────────────

    [Fact]
    public void Progress_binding_is_double()
    {
        var members = TypeInference.InferMembers(Parse("view V { progress vm.Percent; }"));
        var m = Assert.Single(members);
        Assert.Equal("double", m.CSharpType);
        Assert.Equal(InferenceConfidence.Explicit, m.Confidence);
    }

    // ── 10./17. Star widths everywhere ────────────────────────────

    [Theory]
    [InlineData("*", "*")]
    [InlineData("2*", "2*")]
    [InlineData("3*", "3*")]
    [InlineData("10*", "10*")]
    [InlineData("\"3*\"", "3*")]
    [InlineData("auto", "Auto")]
    [InlineData("120", "120")]
    public void Star_widths_work_on_col_and_layout_children(string width, string xamlWidth)
    {
        var result = Compile($$"""
            view V {
                bind vm: VM;
                table items: vm.Rows of Row { col "A" bind: item.A width: {{width}}; col "B" bind: item.B; }
                row { text "a" width: {{width}}; text "b"; }
                split { text "l" width: {{width}}; text "r"; }
            }
            """);
        NoErrors(result);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "FS1005");
        var xaml = Xaml(result);
        // table header + row template, row, split
        Assert.Equal(4, Regex.Matches(xaml, $"<ColumnDefinition Width=\"{Regex.Escape(xamlWidth)}\" />").Count
                        - (xamlWidth == "*" ? 4 : 0));
    }

    [Fact]
    public void Invalid_col_width_is_reported_once()
    {
        var result = Compile("""
            view V { bind vm: VM; table items: vm.Rows of Row { col "A" bind: item.A width: wide; } }
            """);
        Assert.Single(result.Diagnostics, d => d.Code == "FS1005");
    }

    // ── 11. Row actions keep their width ──────────────────────────

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

    // ── 12. empty keeps fill ──────────────────────────────────────

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

    // ── 13./14./15. TextBox trigger, badge, icon ──────────────────

    [Fact]
    public void Textbox_and_textarea_update_the_source_on_every_keystroke()
    {
        var xaml = Xaml(Compile("view V { bind vm: VM; textbox \"T\" vm.Title; textarea \"N\" vm.Notes; }"));
        Assert.Equal(2, Regex.Matches(xaml, Regex.Escape("Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}")).Count);
    }

    [Fact]
    public void Badge_hugs_its_text_unless_align_is_set()
    {
        var xaml = Xaml(Compile("view V { badge \"New\"; badge \"Right\" align: right; }"));
        Assert.Contains("VerticalAlignment=\"Center\" HorizontalAlignment=\"Left\">", xaml);
        Assert.Contains("VerticalAlignment=\"Center\" HorizontalAlignment=\"Right\">", xaml);
        Assert.Single(Regex.Matches(xaml, "HorizontalAlignment=\"Left\""));
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

    // ── 16. -> vm.X without the Command suffix ────────────────────

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

    // ── 18. link ──────────────────────────────────────────────────

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

    // ── 19. Row-action props ──────────────────────────────────────

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

    // ── 20. format: number on stat / line / text ──────────────────

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

    // ── 21. select common attributes ──────────────────────────────

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

    private static ViewDecl Parse(string source) =>
        new Parser(new Lexer(source, "test.flag").Tokenize()).Parse().Declarations.OfType<ViewDecl>().Single();
}
