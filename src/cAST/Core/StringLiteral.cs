namespace cAST;

public readonly partial struct StringLiteral
{
    /// <summary>Whether this string, as an object key, prints as a bare name when it has no raw text and is an identifier name.</summary>
    public bool IsUnquotedKey
    {
        get => Unquoted;
        set => Unquoted = value;
    }
}
