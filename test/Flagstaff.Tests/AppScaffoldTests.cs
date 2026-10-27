using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// App-level output: the app namespace, MainWindow and its navigation, theme resources in App.xaml, App.xaml.cs
/// (DI, hooks, async startup), the styles source and the window chrome (backdrop, title bar).
/// </summary>
public class AppScaffoldTests
{
    // ── Namespace ──────────────────────────────────────────────────

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

    // ── Navigation ─────────────────────────────────────────────────

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

    // ── MainWindow ─────────────────────────────────────────────────

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

    [Fact]
    public void MainWindow_has_no_resources_block()
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

    // ── App.xaml: theme resources and converters ───────────────────

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

    // ── App.xaml.cs: DI, hooks and startup ─────────────────────────

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

    // ── Styles source: stdlib default, theme styles:, CLI override (CLI wins) ───

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

    // ── Window chrome: backdrop and title bar ──────────────────────

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
}
