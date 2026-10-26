namespace cAST.Parsing;

/// <summary>
/// The lexical tokens of JavaScript. The order matters: everything from <see cref="Identifier"/> on is an
/// IdentifierName, and everything after <see cref="EscapedReservedWord"/> is a contextual (unreserved) word.
/// </summary>
public enum TokenKind : byte
{
    Undetermined,
    Illegal,
    Eof,
    Comment,

    String,
    Number,

    Plus,
    Minus,
    Multiply,
    Exponent,
    Slash,
    Remainder,

    And,
    Or,
    ExclusiveOr,
    ShiftLeft,
    ShiftRight,
    UnsignedShiftRight,

    AddAssign,
    SubtractAssign,
    MultiplyAssign,
    ExponentAssign,
    QuotientAssign,
    RemainderAssign,

    AndAssign,
    OrAssign,
    ExclusiveOrAssign,
    ShiftLeftAssign,
    ShiftRightAssign,
    UnsignedShiftRightAssign,

    LogicalAnd,
    LogicalOr,
    Coalesce,
    LogicalAndAssign,
    LogicalOrAssign,
    CoalesceAssign,
    Increment,
    Decrement,

    Equal,
    StrictEqual,
    Less,
    Greater,
    Assign,
    Not,

    BitwiseNot,

    NotEqual,
    StrictNotEqual,
    LessOrEqual,
    GreaterOrEqual,

    LeftParenthesis,
    LeftBracket,
    LeftBrace,
    Comma,
    Period,

    RightParenthesis,
    RightBracket,
    RightBrace,
    Semicolon,
    Colon,
    QuestionMark,
    QuestionDot,
    Arrow,
    Ellipsis,
    Backtick,

    PrivateIdentifier,

    TemplateHead,
    TemplateMiddle,
    TemplateTail,
    NoSubstitutionTemplate,

    Identifier,
    Keyword,
    Boolean,
    Null,

    If,
    In,
    Do,

    Var,
    For,
    New,
    Try,

    This,
    Else,
    Case,
    Void,
    With,

    Const,
    While,
    Break,
    Catch,
    Throw,
    Class,
    Super,

    Return,
    Typeof,
    Delete,
    Switch,

    Default,
    Finally,
    Extends,

    Function,
    Continue,
    Debugger,

    InstanceOf,

    EscapedReservedWord,

    Of,
    Let,
    Static,
    Async,
    Await,
    Yield,
}

/// <summary>Helpers for <see cref="TokenKind"/>.</summary>
public static class TokenKinds
{
    private static readonly string[] Names = BuildNames();

    /// <summary>Whether the token is an IdentifierName (identifier or any keyword).</summary>
    public static bool IsIdentifierName(TokenKind kind) => kind >= TokenKind.Identifier;

    /// <summary>Whether the token is a contextual (unreserved) keyword.</summary>
    public static bool IsUnreservedWord(TokenKind kind) => kind > TokenKind.EscapedReservedWord;

    /// <summary>The token as it appears in source, or its name.</summary>
    public static string ToText(TokenKind kind) => (uint)kind < (uint)Names.Length && Names[(int)kind] is { } s ? s : kind.ToString();

    private static string[] BuildNames()
    {
        var n = new string[(int)TokenKind.Yield + 1];
        n[(int)TokenKind.Undetermined] = "UNKNOWN";
        n[(int)TokenKind.Illegal] = "Illegal";
        n[(int)TokenKind.Eof] = "Eof";
        n[(int)TokenKind.Comment] = "Comment";
        n[(int)TokenKind.Keyword] = "Keyword";
        n[(int)TokenKind.String] = "String";
        n[(int)TokenKind.Boolean] = "Boolean";
        n[(int)TokenKind.Null] = "Null";
        n[(int)TokenKind.Number] = "Number";
        n[(int)TokenKind.Identifier] = "Identifier";
        n[(int)TokenKind.PrivateIdentifier] = "PrivateIdentifier";
        n[(int)TokenKind.Plus] = "+";
        n[(int)TokenKind.Minus] = "-";
        n[(int)TokenKind.Exponent] = "**";
        n[(int)TokenKind.Multiply] = "*";
        n[(int)TokenKind.Slash] = "/";
        n[(int)TokenKind.Remainder] = "%";
        n[(int)TokenKind.And] = "&";
        n[(int)TokenKind.Or] = "|";
        n[(int)TokenKind.ExclusiveOr] = "^";
        n[(int)TokenKind.ShiftLeft] = "<<";
        n[(int)TokenKind.ShiftRight] = ">>";
        n[(int)TokenKind.UnsignedShiftRight] = ">>>";
        n[(int)TokenKind.AddAssign] = "+=";
        n[(int)TokenKind.SubtractAssign] = "-=";
        n[(int)TokenKind.MultiplyAssign] = "*=";
        n[(int)TokenKind.ExponentAssign] = "**=";
        n[(int)TokenKind.QuotientAssign] = "/=";
        n[(int)TokenKind.RemainderAssign] = "%=";
        n[(int)TokenKind.AndAssign] = "&=";
        n[(int)TokenKind.OrAssign] = "|=";
        n[(int)TokenKind.ExclusiveOrAssign] = "^=";
        n[(int)TokenKind.ShiftLeftAssign] = "<<=";
        n[(int)TokenKind.ShiftRightAssign] = ">>=";
        n[(int)TokenKind.UnsignedShiftRightAssign] = ">>>=";
        n[(int)TokenKind.LogicalAnd] = "&&";
        n[(int)TokenKind.LogicalOr] = "||";
        n[(int)TokenKind.Coalesce] = "??";
        n[(int)TokenKind.LogicalAndAssign] = "&&=";
        n[(int)TokenKind.LogicalOrAssign] = "||=";
        n[(int)TokenKind.CoalesceAssign] = "??=";
        n[(int)TokenKind.Increment] = "++";
        n[(int)TokenKind.Decrement] = "--";
        n[(int)TokenKind.Equal] = "==";
        n[(int)TokenKind.StrictEqual] = "===";
        n[(int)TokenKind.Less] = "<";
        n[(int)TokenKind.Greater] = ">";
        n[(int)TokenKind.Assign] = "=";
        n[(int)TokenKind.Not] = "!";
        n[(int)TokenKind.BitwiseNot] = "~";
        n[(int)TokenKind.NotEqual] = "!=";
        n[(int)TokenKind.StrictNotEqual] = "!==";
        n[(int)TokenKind.LessOrEqual] = "<=";
        n[(int)TokenKind.GreaterOrEqual] = ">=";
        n[(int)TokenKind.LeftParenthesis] = "(";
        n[(int)TokenKind.LeftBracket] = "[";
        n[(int)TokenKind.LeftBrace] = "{";
        n[(int)TokenKind.Comma] = ",";
        n[(int)TokenKind.Period] = ".";
        n[(int)TokenKind.RightParenthesis] = ")";
        n[(int)TokenKind.RightBracket] = "]";
        n[(int)TokenKind.RightBrace] = "}";
        n[(int)TokenKind.Semicolon] = ";";
        n[(int)TokenKind.Colon] = ":";
        n[(int)TokenKind.QuestionMark] = "?";
        n[(int)TokenKind.QuestionDot] = "?.";
        n[(int)TokenKind.Arrow] = "=>";
        n[(int)TokenKind.Ellipsis] = "...";
        n[(int)TokenKind.Backtick] = "`";
        n[(int)TokenKind.If] = "if";
        n[(int)TokenKind.In] = "in";
        n[(int)TokenKind.Of] = "of";
        n[(int)TokenKind.Do] = "do";
        n[(int)TokenKind.Var] = "var";
        n[(int)TokenKind.Let] = "let";
        n[(int)TokenKind.For] = "for";
        n[(int)TokenKind.New] = "new";
        n[(int)TokenKind.Try] = "try";
        n[(int)TokenKind.This] = "this";
        n[(int)TokenKind.Else] = "else";
        n[(int)TokenKind.Case] = "case";
        n[(int)TokenKind.Void] = "void";
        n[(int)TokenKind.With] = "with";
        n[(int)TokenKind.Async] = "async";
        n[(int)TokenKind.Await] = "await";
        n[(int)TokenKind.Yield] = "yield";
        n[(int)TokenKind.Const] = "const";
        n[(int)TokenKind.While] = "while";
        n[(int)TokenKind.Break] = "break";
        n[(int)TokenKind.Catch] = "catch";
        n[(int)TokenKind.Throw] = "throw";
        n[(int)TokenKind.Class] = "class";
        n[(int)TokenKind.Super] = "super";
        n[(int)TokenKind.Return] = "return";
        n[(int)TokenKind.Typeof] = "typeof";
        n[(int)TokenKind.Delete] = "delete";
        n[(int)TokenKind.Switch] = "switch";
        n[(int)TokenKind.Static] = "static";
        n[(int)TokenKind.Default] = "default";
        n[(int)TokenKind.Finally] = "finally";
        n[(int)TokenKind.Extends] = "extends";
        n[(int)TokenKind.Function] = "function";
        n[(int)TokenKind.Continue] = "continue";
        n[(int)TokenKind.Debugger] = "debugger";
        n[(int)TokenKind.InstanceOf] = "instanceof";
        return n;
    }
}
