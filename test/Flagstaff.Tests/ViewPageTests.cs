using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// The generated page of a view: its root container and background, constructors, the <c>onload</c> handler, and
/// the click handler behind <c>-> navigate(View)</c> buttons.
/// </summary>
public class ViewPageTests
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

    private static string File(CompilationResult result, string name) => result.Files.Single(f => f.Name == name).Content;

    // ── Page structure ─────────────────────────────────────────────

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

    // ── Constructors ───────────────────────────────────────────────

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

    // ── onload ─────────────────────────────────────────────────────

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

    // ── navigate() buttons ─────────────────────────────────────────

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
}
