using cAST.Parsing;

namespace cAST;

/// <summary>
/// Strings with fixed ids in every <see cref="JsAst"/> string table: the empty string, all keywords and a
/// handful of names the parser and resolver compare against. Fixed ids make these comparisons integer compares.
/// </summary>
public static class KnownStrings
{
    /// <summary>The id of the empty string.</summary>
    public const int Empty = 1;

    // Reserved words (token kind != Identifier). Order must match Entries below.
    internal const int If = 2, In = 3, Do = 4, Var = 5, For = 6, New = 7, Try = 8, This = 9, Else = 10, Case = 11,
        Void = 12, With = 13, Enum = 14, Const = 15, While = 16, Break = 17, Catch = 18, Throw = 19, Class = 20,
        Super = 21, Return = 22, Typeof = 23, Delete = 24, Switch = 25, Export = 26, Import = 27, Default = 28,
        Finally = 29, Extends = 30, Function = 31, Continue = 32, Debugger = 33, InstanceOf = 34, True = 35,
        False = 36, Null = 37;

    // Contextual keywords (token kind > EscapedReservedWord).
    internal const int Let = 38, Static = 39, Async = 40, Await = 41, Yield = 42;

    /// <summary>The last id whose token kind is not <see cref="TokenKind.Identifier"/>.</summary>
    internal const int LastKeyword = 42;

    // Plain identifiers the parser/resolver look for.
    internal const int Of = 43, Get = 44, Set = 45, Target = 46, Meta = 47, Constructor = 48, Prototype = 49,
        Arguments = 50, Eval = 51, Undefined = 52, NaN = 53, Infinity = 54;

    /// <summary>The number of fixed ids, including the none id 0.</summary>
    internal const int Count = 55;

    internal static readonly (string Text, TokenKind Kind)[] Entries =
    [
        ("if", TokenKind.If), ("in", TokenKind.In), ("do", TokenKind.Do), ("var", TokenKind.Var), ("for", TokenKind.For),
        ("new", TokenKind.New), ("try", TokenKind.Try), ("this", TokenKind.This), ("else", TokenKind.Else),
        ("case", TokenKind.Case), ("void", TokenKind.Void), ("with", TokenKind.With), ("enum", TokenKind.Keyword),
        ("const", TokenKind.Const), ("while", TokenKind.While), ("break", TokenKind.Break), ("catch", TokenKind.Catch),
        ("throw", TokenKind.Throw), ("class", TokenKind.Class), ("super", TokenKind.Super), ("return", TokenKind.Return),
        ("typeof", TokenKind.Typeof), ("delete", TokenKind.Delete), ("switch", TokenKind.Switch),
        ("export", TokenKind.Keyword), ("import", TokenKind.Keyword), ("default", TokenKind.Default),
        ("finally", TokenKind.Finally), ("extends", TokenKind.Extends), ("function", TokenKind.Function),
        ("continue", TokenKind.Continue), ("debugger", TokenKind.Debugger), ("instanceof", TokenKind.InstanceOf),
        ("true", TokenKind.Boolean), ("false", TokenKind.Boolean), ("null", TokenKind.Null),
        ("let", TokenKind.Let), ("static", TokenKind.Static), ("async", TokenKind.Async), ("await", TokenKind.Await),
        ("yield", TokenKind.Yield),
        ("of", TokenKind.Identifier), ("get", TokenKind.Identifier), ("set", TokenKind.Identifier),
        ("target", TokenKind.Identifier), ("meta", TokenKind.Identifier), ("constructor", TokenKind.Identifier),
        ("prototype", TokenKind.Identifier), ("arguments", TokenKind.Identifier), ("eval", TokenKind.Identifier),
        ("undefined", TokenKind.Identifier), ("NaN", TokenKind.Identifier), ("Infinity", TokenKind.Identifier),
    ];

    /// <summary>Token kind of each fixed id (index = id).</summary>
    internal static readonly TokenKind[] KindById = BuildKinds();

    private static TokenKind[] BuildKinds()
    {
        var kinds = new TokenKind[Count];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = TokenKind.Identifier;
        for (int i = 0; i < Entries.Length; i++) kinds[i + 2] = Entries[i].Kind;
        return kinds;
    }
}
