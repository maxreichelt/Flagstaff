using Flagstaff.Compiler;

namespace Flagstaff.Tests;

public class FeedbackTests
{
    private static CompilationResult CompileView(string viewSource, string appName = "TestApp")
    {
        var compiler = new FlagstaffCompiler { AppNamespace = appName };
        return compiler.Compile(viewSource, "test.flag");
    }

    /// <summary>Compiles one view, then the scaffold (item types such as <c>Order.g.cs</c> are emitted there).</summary>
    private static CompilationResult CompileViewWithScaffold(string viewSource, string appName = "TestApp")
    {
        var compiler = new FlagstaffCompiler { AppNamespace = appName };
        var result = compiler.Compile(viewSource, "test.flag");
        result.Files.AddRange(compiler.GenerateAppScaffold().Files);
        return result;
    }

    private static string CompileViewXaml(string viewSource, string appName = "TestApp")
    {
        var result = CompileView(viewSource, appName);
        return result.Files.First(f => f.Name.EndsWith(".xaml") && !f.Name.EndsWith(".xaml.cs")).Content;
    }

    // ── Bug 1: Namespace consistency ──────────────────────────────

    [Fact]
    public void Namespace_is_consistent_across_multifile_compile()
    {
        var compiler = new FlagstaffCompiler();

        var appSource = """
            app MyApp {
                theme { accent: #0078D4; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        compiler.Compile(appSource, "app.flag");

        var viewSource = """
            view HomeView {
                bind vm: HomeViewModel;
                textbox "Name" vm.UserName;
            }
            """;
        var viewResult = compiler.Compile(viewSource, "home.flag");

        var viewXaml = viewResult.Files.First(f => f.Name == "HomeView.xaml");
        Assert.Contains("x:Class=\"MyApp.HomeView\"", viewXaml.Content);

        var vmCs = viewResult.Files.First(f => f.Name == "HomeViewModel.g.cs");
        Assert.Contains("namespace MyApp.ViewModels;", vmCs.Content);
    }

    [Fact]
    public void Namespace_override_takes_precedence()
    {
        var compiler = new FlagstaffCompiler
        {
            AppNamespace = "Custom.Namespace",
            NamespaceIsOverridden = true
        };

        var appSource = """
            app ShouldBeIgnored {
                theme { accent: #0078D4; }
                nav top { brand "Test"; link "Home" icon: home -> HomeView; }
            }
            """;
        compiler.Compile(appSource, "app.flag");

        var viewSource = """
            view HomeView {
                bind vm: HomeViewModel;
                textbox "Name" vm.UserName;
            }
            """;
        var result = compiler.Compile(viewSource, "home.flag");

        var viewXaml = result.Files.First(f => f.Name == "HomeView.xaml");
        Assert.Contains("x:Class=\"Custom.Namespace.HomeView\"", viewXaml.Content);

        var vmCs = result.Files.First(f => f.Name == "HomeViewModel.g.cs");
        Assert.Contains("namespace Custom.Namespace.ViewModels;", vmCs.Content);
    }

    // ── Bug 2: Root container ─────────────────────────────────────

    [Fact]
    public void View_has_single_root_container()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                text "A";
                text "B";
            }
            """);

        Assert.Contains("<ScrollViewer Grid.Row=\"1\">", xaml);
        Assert.Contains("<StackPanel Spacing=\"16\" Padding=\"24\">", xaml);
    }

    // ── Bug 3: DataTemplate binding paths ─────────────────────────

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

    // ── Bug 4: Toolbar overlap ────────────────────────────────────

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

    // ── Bug 5: Text binding inside cards ──────────────────────────

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

    // ── Bug 6: when() comparison operators ────────────────────────

    [Fact]
    public void When_with_comparison_uses_converter_function()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                stat "Needs Itemized" vm.NeedsItemized variant: danger when(vm.NeedsItemized > 0);
            }
            """);

        Assert.Contains("local:V.When_1(ViewModel.NeedsItemized), Mode=OneWay", xaml);
        Assert.DoesNotContain("BoolToVisibility", xaml.Split("NeedsItemized")[1]);
    }

    [Fact]
    public void When_without_comparison_uses_bool_converter()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                alert when(vm.HasWarning) variant: warning { "msg"; }
            }
            """);

        Assert.Contains("BoolToVisibility", xaml);
    }

    // ── Bug 7: Select range type ──────────────────────────────────

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

    // ── Bug 8: Format in table columns ────────────────────────────

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

    // ── Bug 9: Async commands ─────────────────────────────────────

    [Fact]
    public void Commands_are_generated_as_async()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand;
            }
            """);

        var vmCs = result.Files.First(f => f.Name == "VM.g.cs");
        Assert.Contains("async Task", vmCs.Content);
        Assert.Contains("SaveAsync", vmCs.Content);
        Assert.Contains("using System.Threading.Tasks;", vmCs.Content);
        Assert.DoesNotContain("private partial void", vmCs.Content);
    }

    // ── Feature 12: Dynamic select items ──────────────────────────

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

    // ── Feature 13: Navigation wiring ─────────────────────────────

    [Fact]
    public void App_codebehind_has_navigation_handler()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
                nav top {
                    brand "Test";
                    link "Home" icon: home -> HomeView;
                }
            }
            """, "app.flag");

        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs");
        Assert.Contains("NavView.SelectionChanged", cs.Content);
        Assert.Contains("OnNavigationChanged", cs.Content);
        Assert.Contains("ContentFrame.Navigate", cs.Content);
        Assert.Contains("Type.GetType", cs.Content);
    }

    [Fact]
    public void App_without_nav_has_no_navigation_handler()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
            }
            """, "app.flag");

        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs");
        Assert.DoesNotContain("OnNavigationChanged", cs.Content);
    }

    // ── Feature 14: Navigate button wiring ────────────────────────

    [Fact]
    public void Navigate_button_emits_tag_and_click()
    {
        var xaml = CompileViewXaml("""
            view V {
                button "Go" -> navigate(OtherView);
            }
            """);

        Assert.Contains("Tag=\"OtherView\"", xaml);
        Assert.Contains("Click=\"OnNavigateButtonClick\"", xaml);
    }

    [Fact]
    public void View_with_navigate_buttons_has_click_handler()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                button "Go" -> navigate(OtherView);
            }
            """);

        var cs = result.Files.First(f => f.Name == "V.xaml.cs");
        Assert.Contains("OnNavigateButtonClick", cs.Content);
        Assert.Contains("Frame.Navigate", cs.Content);
    }

    // ── Feature 15: Table row selection ───────────────────────────

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
    public void Table_selected_infers_item_type()
    {
        var tokens = new Lexer("""
            view V {
                bind vm: VM;
                table items: vm.Expenses selected: vm.SelectedExpense of Expense {
                    col "Vendor" bind: item.Vendor;
                }
            }
            """).Tokenize();
        var file = new Parser(tokens).Parse();
        var view = (ViewDecl)file.Declarations[0];
        var members = TypeInference.InferMembers(view);

        var selected = members.First(m => m.Name == "SelectedExpense");
        Assert.Equal("Expense?", selected.CSharpType);
    }

    // ── Feature 16: Confirm overlay ───────────────────────────────

    [Fact]
    public void Confirm_emits_overlay_with_backdrop()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                confirm "Delete?" message: "This cannot be undone." when(vm.IsDeleteVisible) {
                    button "Cancel" -> vm.CancelDeleteCommand;
                    button "Delete" -> vm.ConfirmDeleteCommand;
                }
            }
            """);

        Assert.Contains("Style=\"{StaticResource FlagstaffDialogBackdrop}\"", xaml);
        Assert.Contains("Delete?", xaml);
        Assert.Contains("This cannot be undone.", xaml);
        Assert.Contains("ViewModel.CancelDeleteCommand", xaml);
        Assert.Contains("ViewModel.ConfirmDeleteCommand", xaml);
        Assert.Contains("BoolToVisibility", xaml);
    }

    // ── Feature 17: Toast ─────────────────────────────────────────

    [Fact]
    public void Toast_emits_infobar_with_isopen_binding()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                toast "Expense saved." when(vm.ShowSaveToast) variant: success;
            }
            """);

        Assert.Contains("<InfoBar", xaml);
        Assert.Contains("IsOpen=\"{x:Bind ViewModel.ShowSaveToast, Mode=TwoWay}\"", xaml);
        Assert.Contains("Severity=\"Success\"", xaml);
        Assert.Contains("Expense saved.", xaml);
        Assert.Contains("IsClosable=\"True\"", xaml);
    }

    // ── Feature 18: FilePicker binding ────────────────────────────

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

    // ══════════════════════════════════════════════════════════════
    // v2 Feedback Tests
    // ══════════════════════════════════════════════════════════════

    // ── v2 Bug: Namespace override applies to MainWindow ────────

    [Fact]
    public void Namespace_override_applies_to_mainwindow_xaml()
    {
        var compiler = new FlagstaffCompiler
        {
            AppNamespace = "Contoso.App",
            NamespaceIsOverridden = true
        };
        var result = compiler.Compile("""
            app ExpenseApp {
                theme { accent: #c0392b; }
                nav top { brand "SE"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");

        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml");
        Assert.Contains("x:Class=\"Contoso.App.MainWindow\"", xaml.Content);
        Assert.Contains("xmlns:local=\"using:Contoso.App\"", xaml.Content);
        Assert.DoesNotContain("x:Class=\"ExpenseApp", xaml.Content);
    }

    [Fact]
    public void Namespace_override_applies_to_mainwindow_codebehind()
    {
        var compiler = new FlagstaffCompiler
        {
            AppNamespace = "Contoso.App",
            NamespaceIsOverridden = true
        };
        var result = compiler.Compile("""
            app ExpenseApp {
                theme { accent: #c0392b; }
                nav top { brand "SE"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");

        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs");
        Assert.Contains("namespace Contoso.App;", cs.Content);
        Assert.Contains("Contoso.App.{tag}", cs.Content);
        Assert.DoesNotContain("ExpenseApp", cs.Content);
    }

    // ── v2 Bug: when() on text, card, split, group ──────────────

    [Fact]
    public void Text_with_when_emits_visibility()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                text "No items." when(vm.IsEmpty);
            }
            """);

        Assert.Contains("Visibility=\"{x:Bind ViewModel.IsEmpty", xaml);
        Assert.Contains("BoolToVisibility", xaml);
    }

    [Fact]
    public void Text_binding_with_when_emits_visibility()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                text vm.Notes when(vm.HasNotes);
            }
            """);

        Assert.Contains("Visibility=\"{x:Bind ViewModel.HasNotes", xaml);
        Assert.Contains("{x:Bind ViewModel.Notes, Mode=OneWay}", xaml);
    }

    [Fact]
    public void Card_with_when_emits_visibility()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                card "Done" when(vm.QueueEmpty) {
                    text "All caught up!";
                }
            }
            """);

        Assert.Contains("<Border Style=\"{StaticResource FlagstaffCard}\" Visibility=\"{x:Bind ViewModel.QueueEmpty", xaml);
    }

    [Fact]
    public void Split_with_when_emits_visibility()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                split when(vm.HasCurrent) {
                    text "left";
                    text "right";
                }
            }
            """);

        Assert.Contains("<Grid ColumnSpacing=\"16\" Visibility=\"{x:Bind ViewModel.HasCurrent", xaml);
    }

    [Fact]
    public void Group_with_when_emits_visibility()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                group "Admin" when(vm.IsAdmin) {
                    text "Admin content";
                }
            }
            """);

        Assert.Contains("<StackPanel Spacing=\"4\" Visibility=\"{x:Bind ViewModel.IsAdmin", xaml);
    }

    // ── v2 Bug: Badge type inference ────────────────────────────

    [Fact]
    public void Badge_infers_string_type()
    {
        var tokens = new Lexer("""
            view V {
                bind vm: VM;
                badge vm.RemainingCount;
            }
            """).Tokenize();
        var file = new Parser(tokens).Parse();
        var view = (ViewDecl)file.Declarations[0];
        var members = TypeInference.InferMembers(view);

        var badge = members.First(m => m.Name == "RemainingCount");
        Assert.Equal("string", badge.CSharpType);
    }

    // ── v2 Bug: format: date in table columns ───────────────────

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

    // ── v2 Observation: Parameterless DI constructor ────────────

    [Fact]
    public void View_codebehind_has_parameterless_constructor()
    {
        var result = CompileView("""
            view DashboardView {
                bind vm: DashboardViewModel;
                text "hello";
            }
            """);

        var cs = result.Files.First(f => f.Name == "DashboardView.xaml.cs");
        Assert.Contains("public DashboardView() : this(App.GetService<ViewModels.DashboardViewModel>()) { }", cs.Content);
        Assert.Contains("public DashboardView(ViewModels.DashboardViewModel viewModel)", cs.Content);
    }

    [Fact]
    public void View_without_vm_has_simple_constructor()
    {
        var result = CompileView("""
            view PlainView {
                text "hello";
            }
            """);

        var cs = result.Files.First(f => f.Name == "PlainView.xaml.cs");
        Assert.Contains("public PlainView()", cs.Content);
        Assert.DoesNotContain("App.GetService", cs.Content);
    }

    // ══════════════════════════════════════════════════════════════
    // v3 Feedback Tests
    // ══════════════════════════════════════════════════════════════

    // ── v3 Fix 1: FontIcon -> SymbolIcon ────────────────────────

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
    public void Nav_link_icon_emits_symbol_attribute()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
                nav top {
                    brand "Test";
                    link "Home" icon: home -> HomeView;
                    link "Mail" icon: mail -> MailView;
                }
            }
            """, "app.flag");

        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml");
        Assert.Contains("Icon=\"Home\"", xaml.Content);
        Assert.Contains("Icon=\"Mail\"", xaml.Content);
        Assert.DoesNotContain("FontIcon", xaml.Content);
    }

    [Fact]
    public void Icon_name_normalizes_hyphenated_to_pascal_case()
    {
        Assert.Equal("CalendarDay", XamlGenerator.NormalizeIconName("calendar-day"));
        Assert.Equal("Home", XamlGenerator.NormalizeIconName("home"));
        Assert.Equal("MailForward", XamlGenerator.NormalizeIconName("mail-forward"));
    }

    // ── v3 Fix 2: RelayCommand delegation ───────────────────────

    [Fact]
    public void Commands_delegate_to_on_method()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                button "Save" -> vm.SaveCommand;
                button "Delete" -> vm.DeleteCommand;
            }
            """);

        var vmCs = result.Files.First(f => f.Name == "VM.g.cs");
        Assert.Contains("await OnSaveAsync()", vmCs.Content);
        Assert.Contains("await OnDeleteAsync()", vmCs.Content);
        Assert.DoesNotContain("{ }", vmCs.Content);
    }

    // ── v3 Fix 3: Item type generation ──────────────────────────

    [Fact]
    public void Table_with_of_type_generates_item_class()
    {
        var result = CompileViewWithScaffold("""
            view V {
                bind vm: VM;
                table items: vm.Expenses of Expense {
                    col "Vendor" bind: item.Vendor;
                    col "Amount" bind: item.Amount format: currency;
                    col "Date" bind: item.ExpenseDate format: date;
                    col "Status" bind: item.StatusDisplay;
                }
            }
            """);

        var itemType = result.Files.FirstOrDefault(f => f.Name == "Expense.g.cs");
        Assert.NotNull(itemType);
        Assert.Contains("public partial class Expense", itemType.Content);
        Assert.Contains("public string Vendor { get; set; }", itemType.Content);
        Assert.Contains("public decimal Amount { get; set; }", itemType.Content);
        Assert.Contains("public DateTimeOffset? ExpenseDate { get; set; }", itemType.Content);
        Assert.Contains("public string StatusDisplay { get; set; }", itemType.Content);
    }

    [Fact]
    public void Item_type_generated_in_viewmodels_namespace()
    {
        var result = CompileViewWithScaffold("""
            view V {
                bind vm: VM;
                table items: vm.Items of MyRow {
                    col "Name" bind: item.Name;
                }
            }
            """);

        var itemType = result.Files.First(f => f.Name == "MyRow.g.cs");
        Assert.Contains("namespace TestApp.ViewModels;", itemType.Content);
    }

    [Fact]
    public void Duplicate_table_types_are_deduplicated()
    {
        var result = CompileViewWithScaffold("""
            view V {
                bind vm: VM;
                table items: vm.ListA of Shared {
                    col "A" bind: item.A;
                }
                table items: vm.ListB of Shared {
                    col "B" bind: item.B;
                }
            }
            """);

        var itemFiles = result.Files.Where(f => f.Name == "Shared.g.cs").ToList();
        Assert.Single(itemFiles);
        Assert.Contains("public string A { get; set; }", itemFiles[0].Content);
        Assert.Contains("public string B { get; set; }", itemFiles[0].Content);
    }

    [Fact]
    public void Item_types_are_not_emitted_per_view()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                table items: vm.Items of MyRow {
                    col "Name" bind: item.Name;
                }
            }
            """);

        Assert.DoesNotContain(result.Files, f => f.Name == "MyRow.g.cs");
    }

    // ── XAML Linter Tests ───────────────────────────────────────

    [Fact]
    public void Linter_catches_malformed_xml()
    {
        var diags = XamlLinter.Lint("test.xaml", "<Page><Unclosed>", "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Malformed XML"));
    }

    [Fact]
    public void Linter_passes_valid_xaml()
    {
        var xaml = CompileViewXaml("""
            view V {
                bind vm: VM;
                text "Hello";
            }
            """);

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.DoesNotContain(diags, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Linter_accepts_fonticon_with_glyph_and_rejects_one_without()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <FontIcon Glyph="&#xE10F;" />
                <FontIcon />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp").Where(d => d.Code == "FS3002").ToList();
        var only = Assert.Single(diags);
        Assert.Equal(DiagnosticSeverity.Error, only.Severity);
        Assert.Equal(5, only.Location.Line);
    }

    [Fact]
    public void Generic_FontIcon_with_glyph_passes_the_linter()
    {
        var xaml = CompileViewXaml("""
            view V {
                FontIcon Glyph: "";
            }
            """);

        Assert.Contains("<FontIcon Glyph=", xaml);
        Assert.DoesNotContain(XamlLinter.Lint("V.xaml", xaml, "TestApp"), d => d.Code == "FS3002");
    }

    [Fact]
    public void Linter_catches_invalid_symbol_value()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <SymbolIcon Symbol="Bogus" />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("Bogus"));
    }

    [Fact]
    public void Linter_accepts_valid_symbol_value()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <SymbolIcon Symbol="Home" />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.DoesNotContain(diags, d => d.Message.Contains("Symbol"));
    }

    [Fact]
    public void Linter_catches_duplicate_xname()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <TextBlock x:Name="Foo" />
                <TextBlock x:Name="Foo" />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Duplicate x:Name"));
    }

    [Fact]
    public void Linter_catches_xclass_namespace_mismatch()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="WrongNamespace.V">
                <TextBlock Text="test" />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("namespace"));
    }

    [Fact]
    public void Linter_catches_datatemplate_viewmodel_binding()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <ListView>
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <TextBlock Text="{x:Bind ViewModel.Name}" />
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("DataTemplate"));
    }

    [Fact]
    public void Linter_warns_unknown_static_resource()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <TextBlock Style="{StaticResource UnknownStyle}" />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("UnknownStyle"));
    }

    [Fact]
    public void Linter_accepts_known_static_resources()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <TextBlock Style="{StaticResource SubtitleTextBlockStyle}" />
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.DoesNotContain(diags, d => d.Message.Contains("SubtitleTextBlockStyle"));
    }

    [Fact]
    public void Linter_integrated_into_compiler_pipeline()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                text "Hello";
            }
            """);

        Assert.NotNull(result.Diagnostics);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // ══════════════════════════════════════════════════════════════
    // v4 Feedback Tests
    // ══════════════════════════════════════════════════════════════

    // ── v4 Critical: Window.Resources replaced with content element Resources

    [Fact]
    public void App_with_nav_theme_resources_in_app_xaml()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #c0392b; }
                nav top {
                    brand "Test";
                    link "Home" icon: home -> HomeView;
                }
            }
            """, "app.flag");

        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("SystemAccentColor", appXaml);
        Assert.Contains("FlagstaffAccentBrush", appXaml);
    }

    [Fact]
    public void App_without_nav_theme_resources_in_app_xaml()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #2980b9; }
            }
            """, "app.flag");

        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml");
        Assert.DoesNotContain("Window.Resources", xaml.Content);
        Assert.DoesNotContain("Grid.Resources", xaml.Content);
        Assert.Contains("ContentFrame", xaml.Content);

        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("SystemAccentColor", appXaml);
    }

    [Fact]
    public void App_without_theme_has_no_resources_block()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                nav top {
                    brand "Test";
                    link "Home" icon: home -> HomeView;
                }
            }
            """, "app.flag");

        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml");
        Assert.DoesNotContain(".Resources>", xaml.Content);
    }

    // ── v4 Critical: x:DataType on DataTemplate

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

    // ── v4 Minor: using Microsoft.UI.Xaml in MainWindow code-behind

    [Fact]
    public void MainWindow_codebehind_has_xaml_using()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
                nav top {
                    brand "Test";
                    link "Home" icon: home -> HomeView;
                }
            }
            """, "app.flag");

        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs");
        Assert.Contains("using Microsoft.UI.Xaml;", cs.Content);
        Assert.Contains("using Microsoft.UI.Xaml.Controls;", cs.Content);
    }

    [Fact]
    public void MainWindow_codebehind_without_nav_has_xaml_using()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
            }
            """, "app.flag");

        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs");
        Assert.Contains("using Microsoft.UI.Xaml;", cs.Content);
    }

    // ── v4 Minor: badge infers string for non-numeric use

    [Fact]
    public void Badge_binding_works_for_string_values()
    {
        var result = CompileView("""
            view V {
                bind vm: VM;
                badge vm.StatusDisplay;
            }
            """);

        var vmCs = result.Files.First(f => f.Name == "VM.g.cs");
        Assert.Contains("string _statusDisplay", vmCs.Content);
    }

    // ── v4 Linter: Window.Resources check

    [Fact]
    public void Linter_catches_window_resources()
    {
        var xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="TestApp.MainWindow">
                <Window.Resources>
                    <ResourceDictionary />
                </Window.Resources>
            </Window>
            """;

        var diags = XamlLinter.Lint("MainWindow.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Window.Resources"));
    }

    // ── v4 Linter: DataTemplate missing x:DataType

    [Fact]
    public void Linter_catches_datatemplate_missing_x_data_type()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <ListView>
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <TextBlock Text="{x:Bind Name}" />
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.Contains(diags, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("x:DataType"));
    }

    [Fact]
    public void Linter_accepts_datatemplate_with_x_data_type()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V"
                  xmlns:vm="using:TestApp.ViewModels">
                <ListView>
                    <ListView.ItemTemplate>
                        <DataTemplate x:DataType="vm:Expense">
                            <TextBlock Text="{x:Bind Name}" />
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.DoesNotContain(diags, d => d.Message.Contains("x:DataType"));
    }

    [Fact]
    public void Linter_ignores_datatemplate_without_x_bind()
    {
        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="TestApp.V">
                <ListView>
                    <ListView.HeaderTemplate>
                        <DataTemplate>
                            <TextBlock Text="Header" />
                        </DataTemplate>
                    </ListView.HeaderTemplate>
                </ListView>
            </Page>
            """;

        var diags = XamlLinter.Lint("V.xaml", xaml, "TestApp");
        Assert.DoesNotContain(diags, d => d.Message.Contains("x:DataType"));
    }

    // ── Styles source: default fallback ─────────────────────────────

    [Fact]
    public void Styles_source_default_uses_stdlib_path()
    {
        var source = """
            app MyApp {
                theme { accent: #c0392b; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        compiler.Compile(source, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("ms-appx:///Flagstaff.Stdlib/Styles.xaml", appXaml);
    }

    // ── Styles source: DSL override ─────────────────────────────────

    [Fact]
    public void Styles_source_dsl_theme_overrides_default()
    {
        var source = """
            app MyApp {
                theme { accent: #c0392b; styles: "Styles/FlagstaffStyles.xaml"; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        compiler.Compile(source, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("Styles/FlagstaffStyles.xaml", appXaml);
        Assert.DoesNotContain("Flagstaff.Stdlib", appXaml);
    }

    // ── Styles source: CLI override ─────────────────────────────────

    [Fact]
    public void Styles_source_cli_overrides_default()
    {
        var source = """
            app MyApp {
                theme { accent: #c0392b; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler { StylesSource = "Assets/Custom.xaml" };
        compiler.Compile(source, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("Assets/Custom.xaml", appXaml);
        Assert.DoesNotContain("Flagstaff.Stdlib", appXaml);
    }

    // ── Styles source: CLI beats DSL ────────────────────────────────

    [Fact]
    public void Styles_source_cli_takes_precedence_over_dsl()
    {
        var source = """
            app MyApp {
                theme { accent: #c0392b; styles: "Styles/FlagstaffStyles.xaml"; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler { StylesSource = "Assets/Custom.xaml" };
        compiler.Compile(source, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("Assets/Custom.xaml", appXaml);
        Assert.DoesNotContain("FlagstaffStyles.xaml", appXaml);
    }

    // ── Onload: XAML contains Loaded attribute ──────────────────────

    [Fact]
    public void Onload_emits_loaded_attribute_in_xaml()
    {
        var source = """
            view DashboardView {
                bind vm: DashboardViewModel;
                onload -> vm.LoadAsync;
                text "Hello";
            }
            """;
        var xaml = CompileViewXaml(source);
        Assert.Contains("Loaded=\"OnViewLoaded\"", xaml);
    }

    // ── Onload: code-behind contains handler ────────────────────────

    [Fact]
    public void Onload_emits_handler_in_codebehind()
    {
        var source = """
            view DashboardView {
                bind vm: DashboardViewModel;
                onload -> vm.LoadAsync;
                text "Hello";
            }
            """;
        var result = CompileView(source);
        var cs = result.Files.First(f => f.Name == "DashboardView.xaml.cs").Content;
        Assert.Contains("private async void OnViewLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)", cs);
        Assert.Contains("await ViewModel.LoadCommand.ExecuteAsync(null);", cs);
    }

    // ── Onload: view without onload has no handler ──────────────────

    [Fact]
    public void View_without_onload_has_no_loaded_handler()
    {
        var source = """
            view PlainView {
                bind vm: PlainViewModel;
                text "Hello";
            }
            """;
        var result = CompileView(source);
        var xaml = result.Files.First(f => f.Name == "PlainView.xaml").Content;
        var cs = result.Files.First(f => f.Name == "PlainView.xaml.cs").Content;
        Assert.DoesNotContain("Loaded=", xaml);
        Assert.DoesNotContain("OnViewLoaded", cs);
    }

    // ── Onload: parser round-trips target correctly ─────────────────

    [Fact]
    public void Onload_parser_captures_target_method()
    {
        var source = """
            view TestView {
                bind vm: TestViewModel;
                onload -> vm.InitializeAsync;
                text "Test";
            }
            """;
        var lexer = new Lexer(source, "test.flag");
        var tokens = lexer.Tokenize();
        var parser = new Parser(tokens);
        var file = parser.Parse();
        var view = file.Declarations.OfType<ViewDecl>().Single();
        Assert.Equal("vm.InitializeAsync", view.OnLoadTarget);
    }

    // ── Window chrome: Mica default ─────────────────────────────────

    [Fact]
    public void Theme_emits_mica_backdrop_by_default()
    {
        var source = """
            app MyApp {
                theme { accent: #0078D4; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        Assert.Contains("<MicaBackdrop />", xaml);
        Assert.Contains("Window.SystemBackdrop", xaml);
    }

    // ── Window chrome: Acrylic override ─────────────────────────────

    [Fact]
    public void Theme_backdrop_acrylic_emits_desktop_acrylic()
    {
        var source = """
            app MyApp {
                theme { accent: #0078D4; backdrop: acrylic; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        Assert.Contains("<DesktopAcrylicBackdrop />", xaml);
        Assert.DoesNotContain("MicaBackdrop", xaml);
    }

    // ── Window chrome: Backdrop none ────────────────────────────────

    [Fact]
    public void Theme_backdrop_none_omits_system_backdrop()
    {
        var source = """
            app MyApp {
                theme { accent: #0078D4; backdrop: none; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        Assert.DoesNotContain("SystemBackdrop", xaml);
        Assert.DoesNotContain("MicaBackdrop", xaml);
    }

    // ── Window chrome: Extended title bar default ───────────────────

    [Fact]
    public void Theme_emits_extended_titlebar_by_default()
    {
        var source = """
            app MyApp {
                theme { accent: #0078D4; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs").Content;
        Assert.Contains("x:Name=\"AppTitleBar\"", xaml);
        Assert.Contains("Grid.Row=\"1\"", xaml);
        Assert.Contains("ExtendsContentIntoTitleBar = true;", cs);
        Assert.Contains("SetTitleBar(AppTitleBar);", cs);
    }

    // ── Window chrome: Title bar opt-out ────────────────────────────

    [Fact]
    public void Theme_titlebar_default_omits_extended_titlebar()
    {
        var source = """
            app MyApp {
                theme { accent: #0078D4; titlebar: default; }
                nav top { brand "MyApp"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs").Content;
        Assert.DoesNotContain("AppTitleBar", xaml);
        Assert.DoesNotContain("ExtendsContentIntoTitleBar", cs);
    }

    // ── Window chrome: Page background ──────────────────────────────

    [Fact]
    public void View_always_has_themed_page_background()
    {
        var source = """
            view SimpleView {
                bind vm: SimpleViewModel;
                text "Hello";
            }
            """;
        var xaml = CompileViewXaml(source);
        Assert.Contains("ApplicationPageBackgroundThemeBrush", xaml);
    }

    // ── Window chrome: No theme backward compat ─────────────────────

    [Fact]
    public void App_without_theme_has_no_backdrop_or_titlebar()
    {
        var source = """
            app BareApp {
                nav top { brand "Bare"; link "Home" icon: home -> HomeView; }
            }
            """;
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile(source, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        var cs = result.Files.First(f => f.Name == "MainWindow.xaml.cs").Content;
        Assert.DoesNotContain("SystemBackdrop", xaml);
        Assert.DoesNotContain("AppTitleBar", xaml);
        Assert.DoesNotContain("ExtendsContentIntoTitleBar", cs);
    }

    // ══════════════════════════════════════════════════════════════
    // v5 Feedback Tests
    // ══════════════════════════════════════════════════════════════

    // ── v5 Badge: int emits InfoBadge ───────────────────────────────

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

    // ── v5 Badge: string emits styled pill ──────────────────────────

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

    // ── v5 SystemAccentColor: accent produces family ────────────────

    [Fact]
    public void Accent_produces_system_accent_color_family()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #c0392b; }
                nav top { brand "Test"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("SystemAccentColor", appXaml);
        Assert.Contains("SystemAccentColorLight1", appXaml);
        Assert.Contains("SystemAccentColorLight2", appXaml);
        Assert.Contains("SystemAccentColorLight3", appXaml);
        Assert.Contains("SystemAccentColorDark1", appXaml);
        Assert.Contains("SystemAccentColorDark2", appXaml);
        Assert.Contains("SystemAccentColorDark3", appXaml);
    }

    // ── v5 App.xaml: contains converters and resources ──────────────

    [Fact]
    public void App_xaml_contains_converters_and_resources()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
                nav top { brand "Test"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appXaml = scaffold.Files.First(f => f.Name == "App.xaml").Content;
        Assert.Contains("XamlControlsResources", appXaml);
        Assert.Contains("BoolToVisibility", appXaml);
        Assert.Contains("CurrencyConverter", appXaml);
        Assert.Contains("PercentConverter", appXaml);
        Assert.Contains("DateConverter", appXaml);
    }

    // ── v5 App.xaml.cs: contains DI and ViewModel registrations ─────

    [Fact]
    public void App_xaml_cs_contains_di_and_viewmodel_registrations()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
                nav top { brand "Test"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");
        compiler.Compile("""
            view HomeView {
                bind vm: HomeViewModel;
                text "Hello";
            }
            """, "home.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appCs = scaffold.Files.First(f => f.Name == "App.xaml.cs").Content;
        Assert.Contains("GetService<T>", appCs);
        Assert.Contains("ServiceCollection", appCs);
        Assert.Contains("HomeViewModel", appCs);
        Assert.Contains("MainWindow", appCs);
    }

    // ── v5 Converter classes are generated ───────────────────────────

    [Fact]
    public void Scaffold_generates_converter_classes()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
            }
            """, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        Assert.Contains(scaffold.Files, f => f.Name == "BoolToVisibilityConverter.g.cs");
        Assert.Contains(scaffold.Files, f => f.Name == "CurrencyConverter.g.cs");
        Assert.Contains(scaffold.Files, f => f.Name == "PercentConverter.g.cs");
        Assert.Contains(scaffold.Files, f => f.Name == "DateConverter.g.cs");
        Assert.Contains(scaffold.Files, f => f.Name == "Converters.g.cs");

        var converters = scaffold.Files.First(f => f.Name == "Converters.g.cs").Content;
        Assert.Contains("IsPositive", converters);
        Assert.Contains("IsNonZero", converters);
    }

    // ── v5 MainWindow no longer has .Resources block ────────────────

    [Fact]
    public void MainWindow_no_longer_has_resources_block()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.Compile("""
            app TestApp {
                theme { accent: #c0392b; }
                nav top { brand "Test"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");
        var xaml = result.Files.First(f => f.Name == "MainWindow.xaml").Content;
        Assert.DoesNotContain("NavigationView.Resources", xaml);
        Assert.DoesNotContain("ResourceDictionary", xaml);
    }

    // ══════════════════════════════════════════════════════════════
    // v6 Feedback Tests
    // ══════════════════════════════════════════════════════════════

    // ── v6 App.xaml.cs: partial method hooks for extensibility ──────

    [Fact]
    public void App_xaml_cs_has_partial_method_hooks()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
                nav top { brand "Test"; link "Home" icon: home -> HomeView; }
            }
            """, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appCs = scaffold.Files.First(f => f.Name == "App.xaml.cs").Content;
        Assert.Contains("partial void ConfigureServices(IServiceCollection services);", appCs);
        Assert.Contains("partial void OnAppLaunched();", appCs);
        Assert.Contains("ConfigureServices(services);", appCs);
        Assert.Contains("OnAppLaunched();", appCs);
    }

    // ── v6 App.xaml.cs: static MainWindow property ──────────────────

    [Fact]
    public void App_xaml_cs_exposes_static_main_window()
    {
        var compiler = new FlagstaffCompiler();
        compiler.Compile("""
            app TestApp {
                theme { accent: #0078D4; }
            }
            """, "app.flag");
        var scaffold = compiler.GenerateAppScaffold();
        var appCs = scaffold.Files.First(f => f.Name == "App.xaml.cs").Content;
        Assert.Contains("public static MainWindow? MainWindow { get; private set; }", appCs);
        Assert.Contains("MainWindow = new MainWindow();", appCs);
        Assert.Contains("MainWindow.Activate();", appCs);
    }

    // ── Sprint 3 fixes: xaml {} escape hatch ──────────────────────

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

    // ── Sprint 3 fixes: toggle keyword ────────────────────────────

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
    public void Toggle_infers_bool_type_in_viewmodel()
    {
        var result = CompileView("""
            view SettingsView {
                bind vm: SettingsViewModel;
                toggle "Batch Mode" vm.IsBatchMode;
            }
            """);
        var vmCs = result.Files.First(f => f.Name == "SettingsViewModel.g.cs").Content;
        Assert.Contains("bool", vmCs);
        Assert.Contains("_isBatchMode", vmCs);
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

    // ── Sprint 3 fixes: stat with format: ─────────────────────────

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

    // ── Sprint 3 fixes: select with of Type ───────────────────────

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
}
