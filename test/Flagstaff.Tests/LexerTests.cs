using Flagstaff.Compiler;

namespace Flagstaff.Tests;

public class LexerTests
{
    [Fact]
    public void Tokenizes_simple_app()
    {
        var source = """
            app TestApp {
                theme { accent: #c0392b; }
            }
            """;
        var lexer = new Lexer(source);
        var tokens = lexer.Tokenize();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("app", tokens[0].Value);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("TestApp", tokens[1].Value);
        Assert.Equal(TokenKind.LeftBrace, tokens[2].Kind);
    }

    [Fact]
    public void Tokenizes_binding()
    {
        var source = "vm.Year";
        var tokens = new Lexer(source).Tokenize();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("vm", tokens[0].Value);
        Assert.Equal(TokenKind.Dot, tokens[1].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("Year", tokens[2].Value);
    }

    [Fact]
    public void Tokenizes_range()
    {
        var source = "2019..2026";
        var tokens = new Lexer(source).Tokenize();

        Assert.Equal(TokenKind.IntLiteral, tokens[0].Kind);
        Assert.Equal("2019", tokens[0].Value);
        Assert.Equal(TokenKind.DotDot, tokens[1].Kind);
        Assert.Equal(TokenKind.IntLiteral, tokens[2].Kind);
        Assert.Equal("2026", tokens[2].Value);
    }

    [Fact]
    public void Tokenizes_arrow()
    {
        var source = "-> HomeView";
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal(TokenKind.Arrow, tokens[0].Kind);
    }

    [Fact]
    public void Tokenizes_color()
    {
        var source = "#c0392b";
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal(TokenKind.ColorLiteral, tokens[0].Kind);
        Assert.Equal("#c0392b", tokens[0].Value);
    }

    [Fact]
    public void Skips_line_comments()
    {
        var source = """
            // this is a comment
            app TestApp { }
            """;
        var tokens = new Lexer(source).Tokenize();
        Assert.Equal("app", tokens[0].Value);
    }

    [Fact]
    public void Reports_location_correctly()
    {
        var source = "app\n  TestApp";
        var tokens = new Lexer(source, "test.flag").Tokenize();
        Assert.Equal(1, tokens[0].Location.Line);
        Assert.Equal(2, tokens[1].Location.Line);
        Assert.Equal(3, tokens[1].Location.Column);
    }
}
