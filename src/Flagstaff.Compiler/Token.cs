namespace Flagstaff.Compiler;

public enum TokenKind
{
    // Literals
    StringLiteral,
    IntLiteral,
    FloatLiteral,
    ColorLiteral,

    // Identifiers & keywords
    Identifier,
    Keyword,

    // Punctuation
    LeftBrace,
    RightBrace,
    LeftParen,
    RightParen,
    Colon,
    Semicolon,
    Dot,
    DotDot,       // ..
    Arrow,        // ->
    Comma,
    LeftBracket,  // [
    RightBracket, // ]
    Star,         // * or N* (grid star width; Value is "*" / "2*")

    // Comparison operators
    Greater,
    Less,
    GreaterEqual,
    LessEqual,
    EqualEqual,
    BangEqual,

    // Logical operators
    Bang,         // !
    AndAnd,       // &&
    OrOr,         // ||

    // Special
    RawXaml,      // verbatim content inside xaml { ... }
    Invalid,      // unexpected character (tolerant lexing only; the parser reports FS1050)

    // End
    EndOfFile,
}

public readonly record struct Token(
    TokenKind Kind,
    string Value,
    SourceLocation Location)
{
    public bool IsKeyword(string kw) => Kind == TokenKind.Keyword && Value == kw;
    public bool IsIdentifier => Kind == TokenKind.Identifier;
}
