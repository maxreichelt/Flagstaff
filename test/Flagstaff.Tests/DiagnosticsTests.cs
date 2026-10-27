using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>Schema validation, parse-error diagnostics, codes and the CompileAll entry point.</summary>
public class DiagnosticsTests
{
    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "test.flag");

    private static Diagnostic Single(CompilationResult result, string code) =>
        Assert.Single(result.Diagnostics, d => d.Code == code);

    // ── FS1001 unknown prop / value: ───────────────────────────────

    [Fact]
    public void Stat_value_prop_is_unknown_with_positional_hint()
    {
        var result = Compile("""
            view V {
                stat "X" value: vm.Y;
            }
            """);

        var d = Single(result, "FS1001");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("use positional binding: stat \"X\" vm.Y;", d.Hint);
        Assert.Equal(new SourceLocation("test.flag", 2, 14), d.Location);
        Assert.True(result.HasErrors);
        Assert.Empty(result.Files); // no codegen after semantic errors
    }

    [Fact]
    public void Misspelled_prop_suggests_closest()
    {
        var result = Compile("""
            view V {
                row colz: 2 { text "a"; }
            }
            """);

        var d = Single(result, "FS1001");
        Assert.Equal("did you mean 'cols'?", d.Hint);
    }

    [Fact]
    public void Unknown_bare_flag_is_an_error()
    {
        var result = Compile("""
            view V {
                text "a" italic;
            }
            """);

        Single(result, "FS1001");
    }

    // ── FS1000 unknown keyword ─────────────────────────────────────

    [Fact]
    public void Unknown_keyword_reports_suggestion_and_recovers()
    {
        var result = Compile("""
            view V {
                buton "Save";
                card "C" {
                    textbx "Name" vm.Name;
                }
                text "ok";
            }
            """);

        var errors = result.Diagnostics.Where(d => d.Code == "FS1000").ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains("did you mean 'button'?", errors[0].Message);
        Assert.Equal(SemanticValidator.XamlEscapeHint, errors[0].Hint);
        Assert.Equal(new SourceLocation("test.flag", 2, 5), errors[0].Location);
        Assert.Contains("did you mean 'textbox'?", errors[1].Message);
        Assert.Equal(4, errors[1].Location.Line);
    }

    [Fact]
    public void Unknown_keyword_with_raw_xaml_body_is_fs1000_not_a_lexer_error()
    {
        var result = Compile("""
            view V {
                treeview { <TreeView ItemsSource="{x:Bind ViewModel.Nodes}" /> }
            }
            """);

        var d = Assert.Single(result.Diagnostics);
        Assert.Equal("FS1000", d.Code);
        Assert.Equal(SemanticValidator.XamlEscapeHint, d.Hint);
        Assert.Contains("PascalCase", d.Hint);
    }

    [Fact]
    public void Validator_reports_unknown_keyword_in_constructed_ast()
    {
        var loc = new SourceLocation("x.flag", 3, 7);
        var view = new ViewDecl("V", null, null,
            [new Element("tabcontrol", [], new EmptyBody(), null, null, null, null, loc)],
            new SourceLocation("x.flag", 1, 1));

        var d = Assert.Single(SemanticValidator.Validate(new FlagFile([view])));
        Assert.Equal("FS1000", d.Code);
        Assert.Equal(loc, d.Location);
    }

    // ── FS1002 – FS1005 ────────────────────────────────────────────

    [Fact]
    public void Prop_from_other_keyword_is_not_applicable()
    {
        var result = Compile("""
            view V {
                button "Go" cols: 3;
            }
            """);

        var d = Single(result, "FS1002");
        Assert.Contains("'cols'", d.Message);
        Assert.Contains("row", d.Message);
    }

    [Fact]
    public void Invalid_enum_value_lists_allowed_values()
    {
        var result = Compile("""
            view V {
                alert variant: dangr { "x"; }
            }
            """);

        var d = Single(result, "FS1003");
        Assert.Contains("warning, danger, info, success", d.Hint);
        Assert.Contains("did you mean 'danger'?", d.Hint);
    }

    [Fact]
    public void Invalid_format_kind_is_fs1003()
    {
        var result = Compile("""
            view V {
                line "Total" vm.Total format: money;
            }
            """);

        var d = Single(result, "FS1003");
        Assert.Contains("currency", d.Hint);
    }

    [Theory]
    [InlineData("spacer \"x\";", "label")]
    [InlineData("separator vm.Title;", "binding")]
    [InlineData("text \"a\" { text \"b\"; }", "block")]
    [InlineData("text \"a\" -> vm.GoCommand;", "arrow")]
    [InlineData("select item.Name;", "item.X")]
    public void Structural_parts_not_allowed_are_fs1004(string element, string what)
    {
        var result = Compile($$"""
            view V {
                bind vm: VM;
                {{element}}
            }
            """);

        var d = Single(result, "FS1004");
        Assert.Contains(what, d.Message);
        Assert.StartsWith("form: ", d.Hint);
    }

    [Fact]
    public void Wrong_value_shape_is_fs1005()
    {
        var result = Compile("""
            view V {
                row cols: "3" { text "a"; }
            }
            """);

        var d = Single(result, "FS1005");
        Assert.Contains("integer", d.Message);
    }

    [Fact]
    public void Theme_props_are_validated()
    {
        var result = Compile("""
            app A {
                theme { backdrop: glass; accnt: #fff000; }
            }
            """);

        Single(result, "FS1003");
        Assert.Equal("did you mean 'accent'?", Single(result, "FS1001").Hint);
    }

    // ── Lexer / parser codes ───────────────────────────────────────

    [Fact]
    public void Unexpected_character_is_fs1050()
    {
        var result = Compile("""
            view V {
                text "a" @;
            }
            """);

        var d = Single(result, "FS1050");
        Assert.Equal(new SourceLocation("test.flag", 2, 14), d.Location);
    }

    [Fact]
    public void Missing_semicolon_is_fs1051_with_hint()
    {
        var result = Compile("""
            view V {
                text "a"
                text "b";
            }
            """);

        var d = Single(result, "FS1051");
        Assert.Equal("every element without { } ends with ;", d.Hint);
        Assert.Equal(3, d.Location.Line);
    }

    [Fact]
    public void Lexer_still_throws_for_direct_callers()
    {
        var ex = Assert.Throws<FlagstaffParseException>(() => new Lexer("text @", "f.flag").Tokenize());
        Assert.Equal("FS1050", ex.Code);
    }

    [Fact]
    public void Command_argument_other_than_item_is_fs1052()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                button "Edit" -> vm.EditCommand(row);
            }
            """);

        Single(result, "FS1052");
    }

    // ── Linter codes ───────────────────────────────────────────────

    [Fact]
    public void Linter_diagnostics_have_fs30xx_codes()
    {
        Assert.Contains(XamlLinter.Lint("x.xaml", "<Page><Open>", "App"), d => d.Code == "FS3001");

        var xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  x:Class="Other.V">
                <FontIcon />
                <SymbolIcon Symbol="Bogus" />
                <Grid><Grid.ColumnDefinitions><ColumnDefinition /></Grid.ColumnDefinitions><TextBlock Grid.Column="3" /></Grid>
                <ListView><ListView.ItemTemplate><DataTemplate><TextBlock Text="{x:Bind ViewModel.Name}" /></DataTemplate></ListView.ItemTemplate></ListView>
                <TextBlock x:Name="A" /><TextBlock x:Name="A" Style="{StaticResource Nope}" />
            </Page>
            """;
        var codes = XamlLinter.Lint("x.xaml", xaml, "App").Select(d => d.Code).ToHashSet();
        foreach (var code in new[] { "FS3002", "FS3003", "FS3004", "FS3005", "FS3006", "FS3008", "FS3009", "FS3010" })
            Assert.Contains(code, codes);

        var window = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="App.MainWindow">
                <Window.Resources />
            </Window>
            """;
        Assert.Contains(XamlLinter.Lint("w.xaml", window, "App"), d => d.Code == "FS3007");
    }

    // ── Formatting ─────────────────────────────────────────────────

    [Fact]
    public void Msbuild_format_includes_code_and_hint()
    {
        var error = new Diagnostic(DiagnosticSeverity.Error, "FS1001", "Unknown property 'x'",
            new SourceLocation(@"views\a.flag", 3, 9), "did you mean 'y'?");
        Assert.Equal(@"views\a.flag(3,9): error FS1001: Unknown property 'x' [hint: did you mean 'y'?]", error.ToMsBuildString());

        var warning = new Diagnostic(DiagnosticSeverity.Warning, "FS2006", "legacy", new SourceLocation("a.flag", 1, 2));
        Assert.Equal("a.flag(1,2): warning FS2006: legacy", warning.ToMsBuildString());
    }

    // ── CompileAll ─────────────────────────────────────────────────

    [Fact]
    public void CompileAll_compiles_app_first_then_views_by_path_then_scaffold()
    {
        var compiler = new FlagstaffCompiler();
        var result = compiler.CompileAll(
        [
            new SourceFile("views/zeta.flag", "view ZetaView { bind vm: ZetaViewModel; textbox vm.Name; }"),
            new SourceFile("views/alpha.flag", "view AlphaView { bind vm: AlphaViewModel; textbox vm.Name; }"),
            new SourceFile("shell.flag", "app Shop { nav top { link \"Home\" icon: home -> AlphaView; } }"),
        ]);

        Assert.False(result.HasErrors);
        var names = result.Files.Select(f => f.Name).ToList();
        Assert.Equal("MainWindow.xaml", names[0]);
        Assert.True(names.IndexOf("AlphaView.xaml") < names.IndexOf("ZetaView.xaml"));
        Assert.Contains("App.xaml", names);
        Assert.Contains("Converters.g.cs", names);
        // The app (found by content, not file name) set the namespace before the views compiled.
        Assert.Contains("x:Class=\"Shop.ZetaView\"", result.Files.First(f => f.Name == "ZetaView.xaml").Content);
        Assert.Contains("services.AddTransient<ViewModels.AlphaViewModel>();", result.Files.First(f => f.Name == "App.xaml.cs").Content);
    }

    [Fact]
    public void CompileAll_reports_errors_from_every_file_and_keeps_going()
    {
        var result = new FlagstaffCompiler().CompileAll(
        [
            new SourceFile("app.flag", "app A { }"),
            new SourceFile("a.flag", "view AView { text \"a\" }"),
            new SourceFile("b.flag", "view BView { stat \"X\" value: vm.Y; }"),
            new SourceFile("c.flag", "view CView { text \"ok\"; }"),
        ]);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "FS1051" && d.Location.File == "a.flag");
        Assert.Contains(result.Diagnostics, d => d.Code == "FS1001" && d.Location.File == "b.flag");
        Assert.Contains(result.Files, f => f.Name == "CView.xaml");
        Assert.DoesNotContain(result.Files, f => f.Name == "BView.xaml");
    }
}
