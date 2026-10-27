namespace Flagstaff.Compiler;

public sealed class Lexer
{
    private static readonly HashSet<string> Keywords =
    [
        "app", "view", "bind", "vm", "theme", "nav", "top", "side",
        "brand", "link", "toolbar", "heading", "spacer", "text", "button",
        "row", "col", "column", "stack", "split", "group",
        "card", "cards", "stat", "summary", "line",
        "table", "alert", "badge", "separator", "image",
        "textbox", "number", "select", "checkbox", "radio",
        "datepicker", "filePicker", "textarea", "toggle",
        "progress", "spinner",
        "xaml", "when", "enabled", "format", "of", "navigate", "onload",
        "icon", "variant", "value", "items", "cols", "options",
        "true", "false", "null", "bold",
        "currency", "date", "percent",
        "backdrop", "mica", "acrylic", "titlebar", "extended", "none",
        // Universal element form: use "<assembly>" as prefix; / on Event -> target / value forms
        "use", "as", "on", "ref", "resource", "load",
    ];

    private readonly string _source;
    private readonly string _file;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public Lexer(string source, string file = "<input>")
    {
        _source = source;
        _file = file;
    }

    /// <summary>Tokenizes the source; throws <see cref="FlagstaffParseException"/> (FS1050) on an unexpected character.</summary>
    public List<Token> Tokenize() => Tokenize(tolerant: false);

    /// <summary>
    /// Tokenizes the source. With <paramref name="tolerant"/>, unexpected characters become
    /// <see cref="TokenKind.Invalid"/> tokens so the parser can report them in context (or skip them
    /// while recovering from an unknown keyword such as <c>treeview { &lt;TreeView /&gt; }</c>).
    /// </summary>
    public List<Token> Tokenize(bool tolerant)
    {
        var tokens = new List<Token>();
        while (_pos < _source.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _source.Length) break;

            if (tokens.Count >= 1
                && tokens[^1].IsKeyword("xaml")
                && _source[_pos] == '{')
            {
                var braceLoc = Here();
                _pos++; _col++;
                var raw = ExtractRawXamlBlock();
                tokens.Add(new Token(TokenKind.RawXaml, raw, braceLoc));
                continue;
            }

            var loc = Here();
            var ch = _source[_pos];

            Token? token = ch switch
            {
                '{' => Advance(new Token(TokenKind.LeftBrace, "{", loc)),
                '}' => Advance(new Token(TokenKind.RightBrace, "}", loc)),
                '(' => Advance(new Token(TokenKind.LeftParen, "(", loc)),
                ')' => Advance(new Token(TokenKind.RightParen, ")", loc)),
                ':' => Advance(new Token(TokenKind.Colon, ":", loc)),
                ';' => Advance(new Token(TokenKind.Semicolon, ";", loc)),
                ',' => Advance(new Token(TokenKind.Comma, ",", loc)),
                '[' => Advance(new Token(TokenKind.LeftBracket, "[", loc)),
                ']' => Advance(new Token(TokenKind.RightBracket, "]", loc)),
                '*' => Advance(new Token(TokenKind.Star, "*", loc)),
                '"' => ReadString(loc),
                '#' => ReadColor(loc),
                _ => null
            };

            if (token is not null)
            {
                tokens.Add(token.Value);
                continue;
            }

            if (ch == '-' && Peek(1) == '>')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.Arrow, "->", loc));
                continue;
            }
            if (ch == '.' && Peek(1) == '.')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.DotDot, "..", loc));
                continue;
            }
            if (ch == '.')
            {
                _pos++; _col++;
                tokens.Add(new Token(TokenKind.Dot, ".", loc));
                continue;
            }
            if (ch == '>' && Peek(1) == '=')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.GreaterEqual, ">=", loc));
                continue;
            }
            if (ch == '<' && Peek(1) == '=')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.LessEqual, "<=", loc));
                continue;
            }
            if (ch == '=' && Peek(1) == '=')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.EqualEqual, "==", loc));
                continue;
            }
            if (ch == '!' && Peek(1) == '=')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.BangEqual, "!=", loc));
                continue;
            }
            if (ch == '!')
            {
                _pos++; _col++;
                tokens.Add(new Token(TokenKind.Bang, "!", loc));
                continue;
            }
            if (ch == '&' && Peek(1) == '&')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.AndAnd, "&&", loc));
                continue;
            }
            if (ch == '|' && Peek(1) == '|')
            {
                _pos += 2; _col += 2;
                tokens.Add(new Token(TokenKind.OrOr, "||", loc));
                continue;
            }
            if (ch == '>')
            {
                _pos++; _col++;
                tokens.Add(new Token(TokenKind.Greater, ">", loc));
                continue;
            }
            if (ch == '<')
            {
                _pos++; _col++;
                tokens.Add(new Token(TokenKind.Less, "<", loc));
                continue;
            }

            if (char.IsDigit(ch) || (ch == '-' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
            {
                tokens.Add(ReadNumber(loc));
                continue;
            }

            if (IsIdentStart(ch))
            {
                tokens.Add(ReadIdentOrKeyword(loc));
                continue;
            }

            if (tolerant)
            {
                _pos++; _col++;
                tokens.Add(new Token(TokenKind.Invalid, ch.ToString(), loc));
                continue;
            }
            throw new FlagstaffParseException($"Unexpected character '{ch}'", loc, "FS1050", HintFor(ch.ToString()));
        }

        tokens.Add(new Token(TokenKind.EndOfFile, "", Here()));
        return tokens;
    }

    public List<Token> TokenizeWithXamlAwareness()
    {
        var tokens = new List<Token>();
        var raw = Tokenize();

        for (int i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            if (t.IsKeyword("xaml") && i + 1 < raw.Count && raw[i + 1].Kind == TokenKind.LeftBrace)
            {
                tokens.Add(t);
                i++; // skip the LeftBrace
                var xamlContent = ExtractRawXamlFromSource(raw[i].Location);
                tokens.Add(new Token(TokenKind.RawXaml, xamlContent, raw[i].Location));
                continue;
            }
            tokens.Add(t);
        }
        return tokens;
    }

    private string ExtractRawXamlBlock()
    {
        int depth = 1;
        int start = _pos;
        while (_pos < _source.Length && depth > 0)
        {
            var c = _source[_pos];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            if (depth > 0)
            {
                if (c == '\n') { _line++; _col = 1; }
                else { _col++; }
                _pos++;
            }
        }
        var content = _source[start.._pos].Trim();
        if (_pos < _source.Length) { _pos++; _col++; } // consume closing }
        return content;
    }

    private string ExtractRawXamlFromSource(SourceLocation braceLocation)
    {
        int searchPos = 0;
        int currentLine = 1;
        int currentCol = 1;
        while (searchPos < _source.Length)
        {
            if (currentLine == braceLocation.Line && currentCol == braceLocation.Column)
                break;
            if (_source[searchPos] == '\n') { currentLine++; currentCol = 1; }
            else { currentCol++; }
            searchPos++;
        }

        if (searchPos < _source.Length && _source[searchPos] == '{')
            searchPos++;

        int depth = 1;
        int start = searchPos;
        while (searchPos < _source.Length && depth > 0)
        {
            if (_source[searchPos] == '{') depth++;
            else if (_source[searchPos] == '}') depth--;
            if (depth > 0) searchPos++;
        }
        return _source[start..searchPos].Trim();
    }

    internal static string? HintFor(string ch) => ch switch
    {
        "&" => "use && for logical and",
        "|" => "use || for logical or",
        "=" => "use == for equality",
        "/" => "raw XAML belongs inside xaml { }",
        _ => null
    };

    private Token Advance(Token token)
    {
        _pos++;
        _col++;
        return token;
    }

    private char Peek(int offset = 0)
    {
        var idx = _pos + offset;
        return idx < _source.Length ? _source[idx] : '\0';
    }

    private SourceLocation Here() => new(_file, _line, _col);

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            if (char.IsWhiteSpace(_source[_pos]))
            {
                if (_source[_pos] == '\n') { _line++; _col = 1; }
                else { _col++; }
                _pos++;
                continue;
            }
            if (_pos + 1 < _source.Length && _source[_pos] == '/' && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n') { _pos++; _col++; }
                continue;
            }
            break;
        }
    }

    private Token ReadString(SourceLocation loc)
    {
        _pos++; _col++; // opening quote
        var start = _pos;
        while (_pos < _source.Length && _source[_pos] != '"')
        {
            if (_source[_pos] == '\\') { _pos++; _col++; }
            if (_source[_pos] == '\n') { _line++; _col = 1; } else { _col++; }
            _pos++;
        }
        var value = _source[start.._pos];
        if (_pos < _source.Length) { _pos++; _col++; } // closing quote
        return new Token(TokenKind.StringLiteral, value, loc);
    }

    private Token ReadColor(SourceLocation loc)
    {
        _pos++; _col++; // #
        var start = _pos;
        while (_pos < _source.Length && IsHexDigit(_source[_pos]))
        {
            _pos++; _col++;
        }
        return new Token(TokenKind.ColorLiteral, "#" + _source[start.._pos], loc);
    }

    private Token ReadNumber(SourceLocation loc)
    {
        var start = _pos;
        if (_source[_pos] == '-') { _pos++; _col++; }
        while (_pos < _source.Length && char.IsDigit(_source[_pos])) { _pos++; _col++; }

        if (_pos < _source.Length && _source[_pos] == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            _pos++; _col++;
            while (_pos < _source.Length && char.IsDigit(_source[_pos])) { _pos++; _col++; }
            return new Token(TokenKind.FloatLiteral, _source[start.._pos], loc);
        }

        // Grid star width: `2*` (the `*` must touch the digits) lexes as one Star token.
        if (_source[start] != '-' && _pos < _source.Length && _source[_pos] == '*')
        {
            _pos++; _col++;
            return new Token(TokenKind.Star, _source[start.._pos], loc);
        }

        return new Token(TokenKind.IntLiteral, _source[start.._pos], loc);
    }

    private Token ReadIdentOrKeyword(SourceLocation loc)
    {
        var start = _pos;
        while (_pos < _source.Length && IsIdentPart(_source[_pos])) { _pos++; _col++; }
        var value = _source[start.._pos];
        var kind = Keywords.Contains(value) ? TokenKind.Keyword : TokenKind.Identifier;
        return new Token(kind, value, loc);
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
    private static bool IsHexDigit(char c) => char.IsAsciiHexDigit(c);
}

/// <summary>
/// Thrown by the lexer and parser on unrecoverable input. The compiler converts it to a
/// <see cref="Diagnostic"/> at its boundary (FS1050 lexer, FS1051 parser by default).
/// </summary>
public class FlagstaffParseException : Exception
{
    public SourceLocation Location { get; }

    /// <summary>Diagnostic code, e.g. FS1050 (unexpected character) or FS1051 (parse error).</summary>
    public string Code { get; }

    /// <summary>The message without the location prefix.</summary>
    public string Reason { get; }

    public string? Hint { get; }

    public FlagstaffParseException(string message, SourceLocation location, string code = "FS1051", string? hint = null)
        : base($"{location}: {message}")
    {
        Location = location;
        Code = code;
        Reason = message;
        Hint = hint;
    }

    public Diagnostic ToDiagnostic() =>
        new(DiagnosticSeverity.Error, Code, Reason, Location, Hint);
}
