using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>
/// <see cref="XamlLinter"/>: checks run over the generated XAML (well-formedness, icons, names, x:Class,
/// Window.Resources, DataTemplate typing, StaticResource keys) and its place in the compiler pipeline.
/// </summary>
public class XamlLinterTests
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

    // ── Well-formedness and the pipeline ───────────────────────────

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

    // ── Icons ──────────────────────────────────────────────────────

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

    // ── Names and x:Class ──────────────────────────────────────────

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

    // ── Window.Resources ───────────────────────────────────────────

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

    // ── DataTemplates ──────────────────────────────────────────────

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

    // ── StaticResource keys ────────────────────────────────────────

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
}
