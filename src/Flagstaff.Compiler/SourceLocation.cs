namespace Flagstaff.Compiler;

public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}({Line},{Column})";

    /// <summary>The file name without its directory (both <c>/</c> and <c>\</c> separate directories).</summary>
    public string FileName => (File ?? "")[((File ?? "").LastIndexOfAny(Separators) + 1)..];

    private static readonly char[] Separators = ['/', (char)92];

    /// <summary>
    /// <c>name.flag(line,col)</c>: the file name only, for text embedded in generated code and diagnostic
    /// messages, so the output does not depend on the working directory of the compiler invocation.
    /// </summary>
    public string ToShortString() => $"{FileName}({Line},{Column})";
}

public readonly record struct Span(SourceLocation Start, SourceLocation End);
