namespace cAST;

/// <summary>A syntax error with the source range (UTF-16 offsets) it applies to.</summary>
/// <param name="Message">The error message.</param>
/// <param name="Start">Offset of the first character of the offending range.</param>
/// <param name="End">Offset just after the offending range.</param>
public readonly record struct ParseError(string Message, int Start, int End)
{
    /// <inheritdoc/>
    public override string ToString() => Message;

    /// <summary>Formats the error with a 1-based line:column prefix computed from <paramref name="source"/>.</summary>
    public string ToString(string source)
    {
        var (line, column) = JsAst.LineColumn(source, Start);
        return $"({line}:{column}) {Message}";
    }
}

/// <summary>Thrown by <see cref="JsAst.ThrowIfErrors"/> and <see cref="Parsing.JsParser.ParseOrThrow(string)"/>.</summary>
public sealed class JsSyntaxException : Exception
{
    /// <summary>Creates the exception from the accumulated errors.</summary>
    public JsSyntaxException(IReadOnlyList<ParseError> errors)
        : base(string.Join('\n', errors.Select(e => e.Message)))
    {
        Errors = errors;
    }

    /// <summary>Every error reported by the parser.</summary>
    public IReadOnlyList<ParseError> Errors { get; }
}
