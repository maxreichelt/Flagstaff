using System.Text.RegularExpressions;
using Flagstaff.Compiler;

namespace Flagstaff.Tests;

/// <summary>Layout diagnostics (FS1010–FS1013), width tokens and universal-prop context checks.</summary>
public class LayoutTests
{
    private static CompilationResult Compile(string source) =>
        new FlagstaffCompiler { AppNamespace = "TestApp" }.Compile(source, "test.flag");

    private static string Xaml(CompilationResult result) =>
        result.Files.Single(f => f.Name == "V.xaml").Content;

    private static void NoErrors(CompilationResult result) =>
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));

    // ── Lexer / parser ─────────────────────────────────────────────

    [Theory]
    [InlineData("*")]
    [InlineData("2*")]
    [InlineData("12*")]
    public void Star_widths_lex_as_one_token(string text)
    {
        var tokens = new Lexer($"width: {text};").Tokenize();
        Assert.Equal(TokenKind.Star, tokens[2].Kind);
        Assert.Equal(text, tokens[2].Value);
        Assert.Equal(TokenKind.Semicolon, tokens[3].Kind);
    }

    [Fact]
    public void Star_separated_from_digits_is_two_tokens()
    {
        var tokens = new Lexer("2 *").Tokenize();
        Assert.Equal([TokenKind.IntLiteral, TokenKind.Star, TokenKind.EndOfFile], tokens.Select(t => t.Kind));
        Assert.Equal("*", tokens[1].Value);
    }

    [Fact]
    public void Width_values_reach_column_definitions()
    {
        var result = Compile("""
            view V {
                row { text "a" width: 2*; text "b" width: auto; text "c" width: 100; text "d" width: *; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        var xaml = Xaml(result);
        Assert.Contains("<ColumnDefinition Width=\"2*\" />", xaml);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", xaml);
        Assert.Contains("<ColumnDefinition Width=\"100\" />", xaml);
        Assert.Contains("<TextBlock Text=\"c\" />", xaml); // the width went to the column, not the element
    }

    // ── FS1010 ─────────────────────────────────────────────────────

    [Fact]
    public void Two_fill_children_are_fs1010_at_the_second()
    {
        var result = Compile("""
            view V {
                text "a" fill;
                text "b" fill;
            }
            """);

        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1010");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(new SourceLocation("test.flag", 3, 14), d.Location);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Fill_in_different_containers_is_fine()
    {
        var result = Compile("""
            view V {
                stack fill { text "a" fill; }
                text "b";
            }
            """);

        Assert.Empty(result.Diagnostics);
        var xaml = Xaml(result);
        Assert.DoesNotContain("ScrollViewer", xaml);
        Assert.Contains("<RowDefinition Height=\"*\" />", xaml);
    }

    // ── FS1011 ─────────────────────────────────────────────────────

    [Fact]
    public void Row_overflow_is_fs1011()
    {
        var result = Compile("""
            view V {
                row cols: 2 { text "a"; text "b"; text "c"; }
            }
            """);

        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1011");
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(new SourceLocation("test.flag", 2, 39), d.Location);
        Assert.Contains("wrap", d.Hint);
    }

    [Fact]
    public void Row_span_overflow_is_fs1011()
    {
        var result = Compile("""
            view V {
                row cols: 2 { text "a"; text "b" span: 2; }
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "FS1011");
    }

    [Fact]
    public void Wrapping_row_does_not_overflow()
    {
        var result = Compile("""
            view V {
                row cols: 2 wrap { text "a"; text "b"; text "c"; }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("<Border Grid.Column=\"0\" Grid.Row=\"1\">", Xaml(result));
    }

    [Fact]
    public void Span_wider_than_cards_is_fs1011()
    {
        var result = Compile("""
            view V {
                cards cols: 2 { text "a" span: 3; }
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "FS1011");
    }

    // ── FS1012 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("text \"a\" width: auto;")]
    [InlineData("text \"a\" width: 2*;")]
    [InlineData("text \"a\" span: 2;")]
    [InlineData("toolbar { text \"a\" fill; }")]
    [InlineData("text \"a\" enabled(vm.On);")]
    public void Layout_prop_without_effect_is_fs1012_warning(string element)
    {
        var result = Compile($$"""
            view V {
                bind vm: VM;
                {{element}}
            }
            """);

        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1012");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Pixel_width_outside_a_grid_sets_width()
    {
        var result = Compile("""
            view V {
                text "a" width: 240;
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("<TextBlock Text=\"a\" Width=\"240\" />", Xaml(result));
    }

    // ── FS1013 ─────────────────────────────────────────────────────

    [Fact]
    public void Fill_inside_scrolling_body_is_fs1013_warning()
    {
        var result = Compile("""
            view V {
                stack { text "a" fill; }
            }
            """);

        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1013");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("<ScrollViewer Grid.Row=\"1\">", Xaml(result));
    }

    // ── Schema / FS1002 / FS1005 ───────────────────────────────────

    [Fact]
    public void Keyword_props_on_the_wrong_keyword_are_fs1002()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                checkbox "x" vm.X placeholder: "p";
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "FS1002");
    }

    [Fact]
    public void Universal_prop_on_emitter_without_support_is_fs1002()
    {
        var result = Compile("""
            view V {
                bind vm: VM;
                table items: vm.Items of Item tooltip: "t" { col "A" bind: item.A; }
            }
            """);

        var d = Assert.Single(result.Diagnostics, d => d.Code == "FS1002");
        Assert.Contains("tooltip", d.Message);
    }

    [Theory]
    [InlineData("width: wide")]
    [InlineData("width: \"x*\"")]
    [InlineData("span: 0")]
    public void Malformed_layout_values_are_fs1005(string prop)
    {
        var result = Compile($$"""
            view V {
                row { text "a" {{prop}}; }
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "FS1005");
    }

    [Fact]
    public void Invalid_align_is_fs1003()
    {
        var result = Compile("""
            view V {
                text "a" align: middle;
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "FS1003");
    }

    // ── Star widths on col, row and split children ─────────────────

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
}
