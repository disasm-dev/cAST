using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cAST.Parsing;

// The lexer. Ported from go-fAST's scanner package, restructured for .NET:
//  * the source is read through a pinned char* and the string's NUL terminator is used as a sentinel,
//    so the hot loops need no end-of-input checks;
//  * identifiers are never hashed or copied: keywords are recognized with a length/char switch and names
//    stay as source slices (see JsAst.Lazy.cs);
//  * strings, comments and templates use vectorized SearchValues scans once they get long;
//  * integer literal values are accumulated while scanning, so most numbers need no separate parse.
public sealed unsafe partial class JsParser
{
    private static readonly SearchValues<char> s_doubleQuoteStops = SearchValues.Create("\"\\\r\n");
    private static readonly SearchValues<char> s_singleQuoteStops = SearchValues.Create("'\\\r\n");
    private static readonly SearchValues<char> s_templateStops = SearchValues.Create("`$\\\r");
    private static readonly SearchValues<char> s_lineTerminators = SearchValues.Create(['\n', '\r', (char)0x2028, (char)0x2029]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char c) => (uint)(c - '0') <= 9;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsIdPart(char c) =>
        c < 128 && (Unsafe.Add(ref MemoryMarshal.GetReference(CharInfo.Ascii), c) & CharInfo.IdPart) != 0;

    /// <summary>Scans the next token into the current-token fields.</summary>
    private void Scan()
    {
        _hasEscape = false;
        _newLine = false;
        _tokKnown = 0;
        _numExact = false;
        char* src = _src;
        int pos = _pos;

        while (true)
        {
            char c = src[pos];

            // ---- whitespace (handled before the dispatch so _tokStart is written once per token) ----
            if (c <= ' ')
            {
                if (c == ' ' || c == '\t')
                {
                    c = src[++pos];
                    if (c == ' ' || c == '\t') pos = SkipSpaces(pos + 1);
                    continue;
                }
                if (c == '\n' || c == '\r')
                {
                    _newLine = true;
                    pos = SkipWhitespaceRun(pos + 1);
                    continue;
                }
                if (c == '\v' || c == '\f')
                {
                    pos++;
                    continue;
                }
            }

            _tokStart = pos;
            switch (c)
            {
                // ---- single-character punctuators ----
                case '(': _kind = TokenKind.LeftParenthesis; pos++; break;
                case ')': _kind = TokenKind.RightParenthesis; pos++; break;
                case ',': _kind = TokenKind.Comma; pos++; break;
                case ':': _kind = TokenKind.Colon; pos++; break;
                case ';': _kind = TokenKind.Semicolon; pos++; break;
                case '[': _kind = TokenKind.LeftBracket; pos++; break;
                case ']': _kind = TokenKind.RightBracket; pos++; break;
                case '{': _kind = TokenKind.LeftBrace; pos++; break;
                case '}': _kind = TokenKind.RightBrace; pos++; break;
                case '~': _kind = TokenKind.BitwiseNot; pos++; break;

                // ---- operators ----
                // Reading src[pos + k] is safe whenever src[pos + k - 1] was a real (non-NUL) character,
                // because the string's terminator bounds every such read.
                case '!':
                    if (src[pos + 1] == '=')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.StrictNotEqual; pos += 3; }
                        else { _kind = TokenKind.NotEqual; pos += 2; }
                    }
                    else { _kind = TokenKind.Not; pos++; }
                    break;

                case '%':
                    if (src[pos + 1] == '=') { _kind = TokenKind.RemainderAssign; pos += 2; }
                    else { _kind = TokenKind.Remainder; pos++; }
                    break;

                case '&':
                    c = src[pos + 1];
                    if (c == '&')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.LogicalAndAssign; pos += 3; }
                        else { _kind = TokenKind.LogicalAnd; pos += 2; }
                    }
                    else if (c == '=') { _kind = TokenKind.AndAssign; pos += 2; }
                    else { _kind = TokenKind.And; pos++; }
                    break;

                case '*':
                    c = src[pos + 1];
                    if (c == '*')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.ExponentAssign; pos += 3; }
                        else { _kind = TokenKind.Exponent; pos += 2; }
                    }
                    else if (c == '=') { _kind = TokenKind.MultiplyAssign; pos += 2; }
                    else { _kind = TokenKind.Multiply; pos++; }
                    break;

                case '+':
                    c = src[pos + 1];
                    if (c == '+') { _kind = TokenKind.Increment; pos += 2; }
                    else if (c == '=') { _kind = TokenKind.AddAssign; pos += 2; }
                    else { _kind = TokenKind.Plus; pos++; }
                    break;

                case '-':
                    c = src[pos + 1];
                    if (c == '-') { _kind = TokenKind.Decrement; pos += 2; }
                    else if (c == '=') { _kind = TokenKind.SubtractAssign; pos += 2; }
                    else { _kind = TokenKind.Minus; pos++; }
                    break;

                case '.':
                    c = src[pos + 1];
                    if (c == '.')
                    {
                        if (src[pos + 2] == '.') { _kind = TokenKind.Ellipsis; pos += 3; }
                        else { _kind = TokenKind.Illegal; pos += 2; }
                    }
                    else if (IsDigit(c)) pos = ScanAfterLeadingDot(pos + 1);
                    else { _kind = TokenKind.Period; pos++; }
                    break;

                case '/':
                    c = src[pos + 1];
                    if (c == '/') { pos = SkipLineComment(pos + 2); continue; }
                    if (c == '*') { pos = SkipBlockComment(pos + 2); continue; }
                    if (c == '=') { _kind = TokenKind.QuotientAssign; pos += 2; }
                    else { _kind = TokenKind.Slash; pos++; }
                    break;

                case '<':
                    c = src[pos + 1];
                    if (c == '<')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.ShiftLeftAssign; pos += 3; }
                        else { _kind = TokenKind.ShiftLeft; pos += 2; }
                    }
                    else if (c == '=') { _kind = TokenKind.LessOrEqual; pos += 2; }
                    else { _kind = TokenKind.Less; pos++; }
                    break;

                case '=':
                    c = src[pos + 1];
                    if (c == '=')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.StrictEqual; pos += 3; }
                        else { _kind = TokenKind.Equal; pos += 2; }
                    }
                    else if (c == '>') { _kind = TokenKind.Arrow; pos += 2; }
                    else { _kind = TokenKind.Assign; pos++; }
                    break;

                case '>':
                    c = src[pos + 1];
                    if (c == '=') { _kind = TokenKind.GreaterOrEqual; pos += 2; }
                    else if (c == '>')
                    {
                        c = src[pos + 2];
                        if (c == '=') { _kind = TokenKind.ShiftRightAssign; pos += 3; }
                        else if (c == '>')
                        {
                            if (src[pos + 3] == '=') { _kind = TokenKind.UnsignedShiftRightAssign; pos += 4; }
                            else { _kind = TokenKind.UnsignedShiftRight; pos += 3; }
                        }
                        else { _kind = TokenKind.ShiftRight; pos += 2; }
                    }
                    else { _kind = TokenKind.Greater; pos++; }
                    break;

                case '?':
                    c = src[pos + 1];
                    if (c == '?')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.CoalesceAssign; pos += 3; }
                        else { _kind = TokenKind.Coalesce; pos += 2; }
                    }
                    else if (c == '.' && !IsDigit(src[pos + 2])) { _kind = TokenKind.QuestionDot; pos += 2; }
                    else { _kind = TokenKind.QuestionMark; pos++; }
                    break;

                case '^':
                    if (src[pos + 1] == '=') { _kind = TokenKind.ExclusiveOrAssign; pos += 2; }
                    else { _kind = TokenKind.ExclusiveOr; pos++; }
                    break;

                case '|':
                    c = src[pos + 1];
                    if (c == '|')
                    {
                        if (src[pos + 2] == '=') { _kind = TokenKind.LogicalOrAssign; pos += 3; }
                        else { _kind = TokenKind.LogicalOr; pos += 2; }
                    }
                    else if (c == '=') { _kind = TokenKind.OrAssign; pos += 2; }
                    else { _kind = TokenKind.Or; pos++; }
                    break;

                // ---- strings, templates ----
                case '"':
                case '\'':
                    pos = ScanString(pos, c);
                    break;

                case '`':
                    pos = ScanTemplate(pos + 1, TokenKind.TemplateHead, TokenKind.NoSubstitutionTemplate);
                    break;

                // ---- special ----
                case '#':
                    if (pos == 0 && src[1] == '!') { pos = SkipLineComment(2); continue; }
                    pos = ScanPrivateName(pos);
                    break;

                case '\\':
                    pos = ScanIdentifierSlow(pos, pos);
                    break;

                // ---- numbers ----
                case '0':
                    pos = ScanZero(pos);
                    break;

                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    pos = ScanDecimal(pos);
                    break;

                // ---- identifiers that may be keywords ----
                case 'a':
                case 'b':
                case 'c':
                case 'd':
                case 'e':
                case 'f':
                case 'g':
                case 'h':
                case 'i':
                case 'j':
                case 'k':
                case 'l':
                case 'm':
                case 'n':
                case 'o':
                case 'p':
                case 'q':
                case 'r':
                case 's':
                case 't':
                case 'u':
                case 'v':
                case 'w':
                case 'x':
                case 'y':
                case 'z':
                    pos = ScanIdentifierOrKeyword(pos);
                    break;

                // ---- identifiers that cannot be keywords ----
                case 'A':
                case 'B':
                case 'C':
                case 'D':
                case 'E':
                case 'F':
                case 'G':
                case 'H':
                case 'I':
                case 'J':
                case 'K':
                case 'L':
                case 'M':
                case 'N':
                case 'O':
                case 'P':
                case 'Q':
                case 'R':
                case 'S':
                case 'T':
                case 'U':
                case 'V':
                case 'W':
                case 'X':
                case 'Y':
                case 'Z':
                case '$':
                case '_':
                    pos = ScanPlainIdentifier(pos);
                    break;

                case '\0':
                    if (pos >= _len)
                    {
                        _kind = TokenKind.Eof;
                        break;
                    }
                    goto default;

                default:
                    if (c >= 0x80)
                    {
                        int r = ScanNonAscii(pos, out bool skip);
                        pos = r;
                        if (skip) continue;
                        break;
                    }
                    // Remaining ASCII control characters, '@' and DEL.
                    pos = InvalidAsciiCharacter(pos);
                    break;
            }
            break;
        }

        _pos = pos;
        _tokEnd = pos;
    }

    /// <summary>Reports an ASCII character that cannot start a token and makes it an Undetermined token.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int InvalidAsciiCharacter(int pos)
    {
        ErrorAt(pos, pos + 1, $"Invalid character `{_src[pos]}`");
        _kind = TokenKind.Undetermined;
        return pos + 1;
    }

    /// <summary>Handles a token starting with a non-ASCII character.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ScanNonAscii(int pos, out bool skip)
    {
        char* src = _src;
        char c = src[pos];
        int cp = c, width = 1;
        if (char.IsHighSurrogate(c) && char.IsLowSurrogate(src[pos + 1]))
        {
            cp = char.ConvertToUtf32(c, src[pos + 1]);
            width = 2;
        }

        skip = false;
        if (CharInfo.IsIdStartUnicode(cp))
            return ScanIdentifierSlow(pos, pos);

        if (c is (char)0x2028 or (char)0x2029)
        {
            _newLine = true;
            skip = true;
            return pos + 1;
        }

        if (CharInfo.IsUnicodeWhitespace(cp))
        {
            skip = true;
            return pos + width;
        }

        ErrorAt(pos, pos + width, $"Invalid character `{new string(src, pos, width)}`");
        _kind = TokenKind.Undetermined;
        return pos + width;
    }

    // ---- comments ----------------------------------------------------------------------------------

    /// <summary>Skips a single-line comment body (after <c>//</c>); does not consume the line terminator.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int SkipLineComment(int p)
    {
        int i = new ReadOnlySpan<char>(_src + p, _len - p).IndexOfAny(s_lineTerminators);
        return i < 0 ? _len : p + i;
    }

    /// <summary>Skips a multi-line comment body (after <c>/*</c>), setting the newline flag if it spans lines.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int SkipBlockComment(int p)
    {
        var rest = new ReadOnlySpan<char>(_src + p, _len - p);
        int i = rest.IndexOf("*/");
        if (i < 0)
        {
            if (!_newLine && rest.ContainsAny(s_lineTerminators)) _newLine = true;
            ErrorAt(_tokStart, _len, "Unterminated multiline comment");
            return _len;
        }
        if (!_newLine && rest[..i].ContainsAny(s_lineTerminators)) _newLine = true;
        return p + i + 2;
    }

    // ---- identifiers and keywords ------------------------------------------------------------------

    // The identifier scanners and SkipWhitespaceRun stay out of line: inlined into Scan, their vector
    // constants occupy callee-saved XMM registers and their span temporaries need zeroing, which made Scan's
    // prologue save seven XMM registers and clear 48 bytes of stack for every token.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ScanIdentifierOrKeyword(int start)
    {
        char* src = _src;
        int p = SkipIdentifierChars(start + 1);
        char c = src[p];
        if (c >= 0x80 || c == '\\') return ScanIdentifierSlow(start, p);

        _kind = TokenKind.Identifier;
        int len = p - start;
        if ((uint)(len - 2) <= 9) MatchKeyword(src + start, len);
        return p;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ScanPlainIdentifier(int start)
    {
        char* src = _src;
        int p = SkipIdentifierChars(start + 1);
        char c = src[p];
        if (c >= 0x80 || c == '\\') return ScanIdentifierSlow(start, p);
        _kind = TokenKind.Identifier;
        return p;
    }

    /// <summary>
    /// Returns the offset of the first non-ASCII-identifier character at or after <paramref name="p"/>.
    /// One 16-wide AVX2 (or 8-wide) compare settles a typical identifier in a single predictable step instead
    /// of a per-character loop whose exit branch mispredicts on every identifier. The common case is kept free
    /// of loops so the JIT does not hoist the vector constants into (callee-saved) registers; names longer
    /// than one vector continue in <see cref="SkipLongIdentifierChars"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SkipIdentifierChars(int p)
    {
        char* src = _src;
        if (Vector256.IsHardwareAccelerated)
        {
            if (p + Vector256<ushort>.Count <= _len)
            {
                uint stop = IdentifierStops(Vector256.Load((ushort*)(src + p)));
                return stop != 0 ? p + BitOperations.TrailingZeroCount(stop) : SkipLongIdentifierChars(p + Vector256<ushort>.Count);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            if (p + Vector128<ushort>.Count <= _len)
            {
                uint stop = IdentifierStops(Vector128.Load((ushort*)(src + p)));
                return stop != 0 ? p + BitOperations.TrailingZeroCount(stop) : SkipLongIdentifierChars(p + Vector128<ushort>.Count);
            }
        }
        while (IsIdPart(src[p])) p++;
        return p;
    }

    /// <summary>Continues <see cref="SkipIdentifierChars"/> past its first vector (names of 17+ chars).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int SkipLongIdentifierChars(int p)
    {
        char* src = _src;
        if (Vector256.IsHardwareAccelerated)
        {
            while (p + Vector256<ushort>.Count <= _len)
            {
                uint stop = IdentifierStops(Vector256.Load((ushort*)(src + p)));
                if (stop != 0) return p + BitOperations.TrailingZeroCount(stop);
                p += Vector256<ushort>.Count;
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            while (p + Vector128<ushort>.Count <= _len)
            {
                uint stop = IdentifierStops(Vector128.Load((ushort*)(src + p)));
                if (stop != 0) return p + BitOperations.TrailingZeroCount(stop);
                p += Vector128<ushort>.Count;
            }
        }
        while (IsIdPart(src[p])) p++;
        return p;
    }

    /// <summary>Bit i set: char i of <paramref name="v"/> is not an ASCII identifier char (A-Z a-z 0-9 _ $).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint IdentifierStops(Vector256<ushort> v)
    {
        var ok = Vector256.LessThan((v | Vector256.Create((ushort)0x20)) - Vector256.Create((ushort)'a'), Vector256.Create((ushort)26))
            | Vector256.LessThan(v - Vector256.Create((ushort)'0'), Vector256.Create((ushort)10))
            | Vector256.Equals(v, Vector256.Create((ushort)'_'))
            | Vector256.Equals(v, Vector256.Create((ushort)'$'));
        return ~ok.ExtractMostSignificantBits() & 0xFFFF;
    }

    /// <inheritdoc cref="IdentifierStops(Vector256{ushort})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint IdentifierStops(Vector128<ushort> v)
    {
        var ok = Vector128.LessThan((v | Vector128.Create((ushort)0x20)) - Vector128.Create((ushort)'a'), Vector128.Create((ushort)26))
            | Vector128.LessThan(v - Vector128.Create((ushort)'0'), Vector128.Create((ushort)10))
            | Vector128.Equals(v, Vector128.Create((ushort)'_'))
            | Vector128.Equals(v, Vector128.Create((ushort)'$'));
        return ~ok.ExtractMostSignificantBits() & 0xFF;
    }

    /// <summary>Skips spaces and tabs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SkipSpaces(int p)
    {
        char* src = _src;
        char c;
        while ((c = src[p]) == ' ' || c == '\t') p++;
        return p;
    }

    /// <summary>Skips a run of spaces, tabs, CR and LF (line breaks plus indentation), 16 chars at a time.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int SkipWhitespaceRun(int p)
    {
        char* src = _src;
        if (Vector256.IsHardwareAccelerated)
        {
            while (p + Vector256<ushort>.Count <= _len)
            {
                var v = Vector256.Load((ushort*)(src + p));
                var ws = Vector256.Equals(v, Vector256.Create((ushort)' '))
                    | Vector256.Equals(v, Vector256.Create((ushort)'\n'))
                    | Vector256.Equals(v, Vector256.Create((ushort)'\t'))
                    | Vector256.Equals(v, Vector256.Create((ushort)'\r'));
                uint stop = ~ws.ExtractMostSignificantBits() & 0xFFFF;
                if (stop != 0) return p + BitOperations.TrailingZeroCount(stop);
                p += Vector256<ushort>.Count;
            }
        }
        char c;
        while ((c = src[p]) == ' ' || c == '\t' || c == '\n' || c == '\r') p++;
        return p;
    }

    /// <summary>Sets <see cref="_kind"/> and <see cref="_tokKnown"/> if <paramref name="s"/> is a keyword or known name.</summary>
    private void MatchKeyword(ReadOnlySpan<char> s)
    {
        fixed (char* p = s) MatchKeyword(p, s.Length);
    }

    /// <summary>
    /// Sets <see cref="_kind"/> and <see cref="_tokKnown"/> if the <paramref name="length"/> chars at
    /// <paramref name="p"/> are a keyword or known name. Takes a pointer rather than a span so callers need no
    /// span temporary (which the JIT would zero-initialize in their prologue).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MatchKeyword(char* p, int length)
    {
        var s = new ReadOnlySpan<char>(p, length);
        // Roslyn compiles this to a switch on length, then on a distinguishing character, then one compare.
        switch (s)
        {
            case "if": _kind = TokenKind.If; _tokKnown = KnownStrings.If; return;
            case "in": _kind = TokenKind.In; _tokKnown = KnownStrings.In; return;
            case "do": _kind = TokenKind.Do; _tokKnown = KnownStrings.Do; return;
            case "of": _tokKnown = KnownStrings.Of; return;
            case "var": _kind = TokenKind.Var; _tokKnown = KnownStrings.Var; return;
            case "for": _kind = TokenKind.For; _tokKnown = KnownStrings.For; return;
            case "new": _kind = TokenKind.New; _tokKnown = KnownStrings.New; return;
            case "try": _kind = TokenKind.Try; _tokKnown = KnownStrings.Try; return;
            case "let": _kind = TokenKind.Let; _tokKnown = KnownStrings.Let; return;
            case "get": _tokKnown = KnownStrings.Get; return;
            case "set": _tokKnown = KnownStrings.Set; return;
            case "this": _kind = TokenKind.This; _tokKnown = KnownStrings.This; return;
            case "else": _kind = TokenKind.Else; _tokKnown = KnownStrings.Else; return;
            case "case": _kind = TokenKind.Case; _tokKnown = KnownStrings.Case; return;
            case "void": _kind = TokenKind.Void; _tokKnown = KnownStrings.Void; return;
            case "with": _kind = TokenKind.With; _tokKnown = KnownStrings.With; return;
            case "enum": _kind = TokenKind.Keyword; _tokKnown = KnownStrings.Enum; return;
            case "true": _kind = TokenKind.Boolean; _tokKnown = KnownStrings.True; return;
            case "null": _kind = TokenKind.Null; _tokKnown = KnownStrings.Null; return;
            case "eval": _tokKnown = KnownStrings.Eval; return;
            case "meta": _tokKnown = KnownStrings.Meta; return;
            case "const": _kind = TokenKind.Const; _tokKnown = KnownStrings.Const; return;
            case "while": _kind = TokenKind.While; _tokKnown = KnownStrings.While; return;
            case "break": _kind = TokenKind.Break; _tokKnown = KnownStrings.Break; return;
            case "catch": _kind = TokenKind.Catch; _tokKnown = KnownStrings.Catch; return;
            case "throw": _kind = TokenKind.Throw; _tokKnown = KnownStrings.Throw; return;
            case "class": _kind = TokenKind.Class; _tokKnown = KnownStrings.Class; return;
            case "super": _kind = TokenKind.Super; _tokKnown = KnownStrings.Super; return;
            case "false": _kind = TokenKind.Boolean; _tokKnown = KnownStrings.False; return;
            case "async": _kind = TokenKind.Async; _tokKnown = KnownStrings.Async; return;
            case "await": _kind = TokenKind.Await; _tokKnown = KnownStrings.Await; return;
            case "yield": _kind = TokenKind.Yield; _tokKnown = KnownStrings.Yield; return;
            case "return": _kind = TokenKind.Return; _tokKnown = KnownStrings.Return; return;
            case "typeof": _kind = TokenKind.Typeof; _tokKnown = KnownStrings.Typeof; return;
            case "delete": _kind = TokenKind.Delete; _tokKnown = KnownStrings.Delete; return;
            case "switch": _kind = TokenKind.Switch; _tokKnown = KnownStrings.Switch; return;
            case "export": _kind = TokenKind.Keyword; _tokKnown = KnownStrings.Export; return;
            case "import": _kind = TokenKind.Keyword; _tokKnown = KnownStrings.Import; return;
            case "static": _kind = TokenKind.Static; _tokKnown = KnownStrings.Static; return;
            case "target": _tokKnown = KnownStrings.Target; return;
            case "default": _kind = TokenKind.Default; _tokKnown = KnownStrings.Default; return;
            case "finally": _kind = TokenKind.Finally; _tokKnown = KnownStrings.Finally; return;
            case "extends": _kind = TokenKind.Extends; _tokKnown = KnownStrings.Extends; return;
            case "function": _kind = TokenKind.Function; _tokKnown = KnownStrings.Function; return;
            case "continue": _kind = TokenKind.Continue; _tokKnown = KnownStrings.Continue; return;
            case "debugger": _kind = TokenKind.Debugger; _tokKnown = KnownStrings.Debugger; return;
            case "prototype": _tokKnown = KnownStrings.Prototype; return;
            case "arguments": _tokKnown = KnownStrings.Arguments; return;
            case "undefined": _tokKnown = KnownStrings.Undefined; return;
            case "instanceof": _kind = TokenKind.InstanceOf; _tokKnown = KnownStrings.InstanceOf; return;
            case "constructor": _tokKnown = KnownStrings.Constructor; return;
        }
    }

    /// <summary>
    /// General identifier scanner for names containing non-ASCII characters or <c>\u</c> escapes.
    /// Characters in [start, p) are already known to be plain ASCII identifier characters.
    /// </summary>
    private int ScanIdentifierSlow(int start, int p)
    {
        char* src = _src;
        bool escaped = false;
        bool nonAscii = false;
        _kind = TokenKind.Identifier;

        while (true)
        {
            char c = src[p];
            bool first = p == start;
            if (c < 0x80)
            {
                if (c == '\\')
                {
                    if (!escaped)
                    {
                        escaped = true;
                        _bufLen = 0;
                        BufAppend(new ReadOnlySpan<char>(src + start, p - start));
                    }
                    int escStart = p;
                    p++; // backslash
                    int cp = -1;
                    if (src[p] == 'u')
                    {
                        p++;
                        cp = ReadUnicodeEscape(ref p, forIdentifier: true);
                    }
                    if (cp < 0 || cp == '\\' || !(first ? CharInfo.IsIdentifierStart(cp) : CharInfo.IsIdentifierPart(cp)))
                    {
                        ErrorAt(escStart, p, "Invalid Unicode escape sequence");
                        if (cp < 0) break;
                        continue;
                    }
                    BufAppendCodePoint(cp);
                    continue;
                }
                if (!IsIdPart(c) || (first && IsDigit(c))) break;
                if (escaped) BufAppend(c);
                p++;
                continue;
            }

            int w = 1;
            int point = c;
            if (char.IsHighSurrogate(c) && char.IsLowSurrogate(src[p + 1]))
            {
                point = char.ConvertToUtf32(c, src[p + 1]);
                w = 2;
            }
            if (!(first ? CharInfo.IsIdStartUnicode(point) : CharInfo.IsIdContinueUnicode(point))) break;
            if (escaped) BufAppend(new ReadOnlySpan<char>(src + p, w));
            nonAscii = true;
            p += w;
        }

        if (escaped)
        {
            string name = BufToString();
            _escaped = name;
            _hasEscape = true;
            if (name.Length is >= 2 and <= 11)
            {
                MatchKeyword(name);
                // Escaped reserved words cannot act as keywords; escaped contextual keywords are identifiers.
                if (_kind != TokenKind.Identifier)
                    _kind = _kind > TokenKind.EscapedReservedWord ? TokenKind.Identifier : TokenKind.EscapedReservedWord;
            }
        }
        else if (!nonAscii && (uint)(p - start - 2) <= 9 && (uint)(src[start] - 'a') <= 25)
        {
            // Plain ASCII name that only came here because a non-identifier, non-ASCII character follows it.
            MatchKeyword(src + start, p - start);
        }
        return p;
    }

    /// <summary>Scans <c>#name</c>. The name (without <c>#</c>) is interned into <see cref="_tokKnown"/>.</summary>
    private int ScanPrivateName(int start)
    {
        char* src = _src;
        int p = start + 1;
        char c;
        while (IsIdPart(c = src[p])) p++;
        if (c >= 0x80 || c == '\\')
        {
            // Re-scan the name part with the general scanner.
            int nameStart = start + 1;
            p = ScanIdentifierSlow(nameStart, p);
            _tokKnown = _hasEscape ? _ast.Intern(_escaped!) : _ast.InternSource(nameStart, p - nameStart);
            _hasEscape = false;
            _escaped = null;
        }
        else
        {
            _tokKnown = _ast.InternSource(start + 1, p - start - 1);
        }
        _kind = TokenKind.PrivateIdentifier;
        return p;
    }

    // ---- escape buffer -----------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BufAppend(char c)
    {
        if (_bufLen == _buf.Length) GrowEscapeBuffer(_bufLen + 1);
        _buf[_bufLen++] = c;
    }

    private void BufAppend(ReadOnlySpan<char> s)
    {
        if (_bufLen + s.Length > _buf.Length) GrowEscapeBuffer(_bufLen + s.Length);
        s.CopyTo(_buf.AsSpan(_bufLen));
        _bufLen += s.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowEscapeBuffer(int capacity)
    {
        var grown = ArrayPool<char>.Shared.Rent(Math.Max(_buf.Length * 2, capacity));
        _buf.AsSpan(0, _bufLen).CopyTo(grown);
        ArrayPool<char>.Shared.Return(_buf);
        _buf = grown;
    }

    private void BufAppendCodePoint(int cp)
    {
        if (cp < 0x10000)
        {
            BufAppend((char)cp);
        }
        else
        {
            cp -= 0x10000;
            BufAppend((char)(0xD800 + (cp >> 10)));
            BufAppend((char)(0xDC00 + (cp & 0x3FF)));
        }
    }

    private string BufToString() => new(_buf, 0, _bufLen);

    // ---- escapes -----------------------------------------------------------------------------------

    /// <summary>
    /// Reads the part of a unicode escape after <c>\u</c>: <c>XXXX</c> or <c>{X...}</c>. Returns the code
    /// point, or -1 if malformed. For identifiers, a surrogate pair written as two escapes is combined.
    /// </summary>
    private int ReadUnicodeEscape(ref int p, bool forIdentifier)
    {
        char* src = _src;
        if (src[p] == '{')
        {
            p++;
            int value = 0, digits = 0;
            int d;
            while ((d = CharInfo.HexValue(src[p])) >= 0)
            {
                value = (value << 4) | d;
                p++;
                digits++;
                if (value > 0x10FFFF) return -1;
            }
            if (digits == 0 || src[p] != '}') return -1;
            p++;
            return value;
        }

        int v = Hex4(p);
        if (v < 0) return -1;
        p += 4;
        if (forIdentifier && char.IsHighSurrogate((char)v) && src[p] == '\\' && src[p + 1] == 'u')
        {
            int q = p + 2;
            int low = Hex4(q);
            if (low >= 0 && char.IsLowSurrogate((char)low))
            {
                p = q + 4;
                return char.ConvertToUtf32((char)v, (char)low);
            }
        }
        return v;
    }

    private int Hex4(int p)
    {
        char* src = _src;
        int a = CharInfo.HexValue(src[p]);
        if (a < 0) return -1;
        int b = CharInfo.HexValue(src[p + 1]);
        if (b < 0) return -1;
        int c = CharInfo.HexValue(src[p + 2]);
        if (c < 0) return -1;
        int d = CharInfo.HexValue(src[p + 3]);
        if (d < 0) return -1;
        return (a << 12) | (b << 8) | (c << 4) | d;
    }

    /// <summary>
    /// Processes one escape sequence (the backslash is already consumed) into the escape buffer.
    /// In templates, legacy octal escapes and <c>\8</c>/<c>\9</c> are invalid.
    /// </summary>
    private int ReadEscape(int p, bool inTemplate, ref bool valid)
    {
        char* src = _src;
        char c = src[p];
        if (c == '\0' && p >= _len)
        {
            valid = false;
            return p;
        }
        p++;
        switch (c)
        {
            // Line continuations produce nothing.
            case '\n':
            case (char)0x2028:
            case (char)0x2029:
                break;
            case '\r':
                if (src[p] == '\n') p++;
                break;

            case 'b': BufAppend('\b'); break;
            case 'f': BufAppend('\f'); break;
            case 'n': BufAppend('\n'); break;
            case 'r': BufAppend('\r'); break;
            case 't': BufAppend('\t'); break;
            case 'v': BufAppend('\v'); break;

            case 'x':
                {
                    int h1 = CharInfo.HexValue(src[p]);
                    int h2 = h1 < 0 ? -1 : CharInfo.HexValue(src[p + 1]);
                    if (h2 < 0) { valid = false; break; }
                    p += 2;
                    BufAppend((char)((h1 << 4) | h2));
                    break;
                }

            case 'u':
                {
                    int cp = ReadUnicodeEscape(ref p, forIdentifier: false);
                    if (cp < 0) { valid = false; break; }
                    BufAppendCodePoint(cp);
                    break;
                }

            case '0':
                if (IsDigit(src[p]))
                {
                    if (inTemplate) { p++; valid = false; break; }
                    goto case '1';
                }
                BufAppend('\0');
                break;

            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
                {
                    if (inTemplate) { valid = false; break; }
                    // LegacyOctalEscapeSequence: ZeroToThree OctalDigit OctalDigit | OctalDigit OctalDigit | OctalDigit.
                    int value = c - '0';
                    if (c == '0' && !IsOctal(src[p]))
                    {
                        BufAppend('\0'); // \08, \09
                        break;
                    }
                    if (IsOctal(src[p]))
                    {
                        value = value * 8 + (src[p] - '0');
                        p++;
                        if (c <= '3' && IsOctal(src[p]))
                        {
                            value = value * 8 + (src[p] - '0');
                            p++;
                        }
                    }
                    BufAppend((char)value);
                    break;
                }

            case '8':
            case '9':
                if (inTemplate) { valid = false; break; }
                BufAppend(c);
                break;

            default:
                // Identity escape (including ' " \).
                BufAppend(c);
                break;
        }
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOctal(char c) => (uint)(c - '0') <= 7;

    // ---- strings -----------------------------------------------------------------------------------

    private int ScanString(int start, char quote)
    {
        char* src = _src;
        int p = start + 1;

        // Scalar fast path: most string literals are short.
        int limit = Math.Min(p + 24, _len);
        while (p < limit)
        {
            char c = src[p];
            if (c == quote) { _kind = TokenKind.String; return p + 1; }
            if (c == '\\' || c == '\n' || c == '\r') return ScanStringStop(start, p, quote);
            p++;
        }
        return ScanLongString(start, p, quote);
    }

    /// <summary>Continues a string longer than the scalar fast path with a vectorized search for its end.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ScanLongString(int start, int p, char quote)
    {
        int i = new ReadOnlySpan<char>(_src + p, _len - p).IndexOfAny(quote == '"' ? s_doubleQuoteStops : s_singleQuoteStops);
        if (i < 0)
        {
            ErrorAt(_tokStart, _len, "Unterminated string");
            _kind = TokenKind.Undetermined;
            return _len;
        }
        return ScanStringStop(start, p + i, quote);
    }

    /// <summary>Handles the character at <paramref name="p"/> that stopped a string scan: quote, backslash or line break.</summary>
    private int ScanStringStop(int start, int p, char quote)
    {
        char c = _src[p];
        if (c == quote) { _kind = TokenKind.String; return p + 1; }
        if (c == '\\') return ScanStringEscaped(start, p, quote);
        ErrorAt(_tokStart, p, "Unterminated string");
        _kind = TokenKind.Undetermined;
        return p;
    }

    private int ScanStringEscaped(int start, int p, char quote)
    {
        char* src = _src;
        var stops = quote == '"' ? s_doubleQuoteStops : s_singleQuoteStops;
        _bufLen = 0;
        BufAppend(new ReadOnlySpan<char>(src + start + 1, p - start - 1));

        while (true)
        {
            // At a backslash.
            int escStart = p;
            bool valid = true;
            p = ReadEscape(p + 1, inTemplate: false, ref valid);
            if (!valid) ErrorAt(escStart, p, "Invalid escape sequence");

            int i = new ReadOnlySpan<char>(src + p, _len - p).IndexOfAny(stops);
            if (i < 0)
            {
                ErrorAt(_tokStart, _len, "Unterminated string");
                _kind = TokenKind.Undetermined;
                return _len;
            }
            BufAppend(new ReadOnlySpan<char>(src + p, i));
            p += i;
            char c = src[p];
            if (c == quote)
            {
                p++;
                break;
            }
            if (c == '\\') continue;
            ErrorAt(_tokStart, p, "Unterminated string");
            _kind = TokenKind.Undetermined;
            return p;
        }

        _escaped = BufToString();
        _hasEscape = true;
        _kind = TokenKind.String;
        return p;
    }

    // ---- templates ---------------------------------------------------------------------------------

    /// <summary>
    /// Scans a template chunk starting just after its opening delimiter (<c>`</c> or <c>}</c>).
    /// Returns <paramref name="sub"/> on <c>${</c> and <paramref name="tail"/> on the closing backtick.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ScanTemplate(int p, TokenKind sub, TokenKind tail)
    {
        char* src = _src;
        int contentStart = p;
        while (true)
        {
            int i = new ReadOnlySpan<char>(src + p, _len - p).IndexOfAny(s_templateStops);
            if (i < 0)
            {
                ErrorAt(_tokStart, _len, "Unterminated template literal");
                _kind = TokenKind.Undetermined;
                return _len;
            }
            p += i;
            switch (src[p])
            {
                case '`':
                    _kind = tail;
                    return p + 1;
                case '$':
                    if (src[p + 1] == '{')
                    {
                        _kind = sub;
                        return p + 2;
                    }
                    p++;
                    continue;
                default: // '\\' or '\r'
                    return ScanTemplateCooked(contentStart, p, sub, tail);
            }
        }
    }

    /// <summary>Continues a template chunk that needs a cooked value (escapes or CR normalization).</summary>
    private int ScanTemplateCooked(int contentStart, int p, TokenKind sub, TokenKind tail)
    {
        char* src = _src;
        _bufLen = 0;
        BufAppend(new ReadOnlySpan<char>(src + contentStart, p - contentStart));
        bool valid = true;

        while (true)
        {
            char c = src[p];
            switch (c)
            {
                case '\\':
                    p = ReadEscape(p + 1, inTemplate: true, ref valid);
                    break;
                case '\r':
                    p++;
                    if (src[p] == '\n') p++;
                    BufAppend('\n');
                    break;
                case '`':
                    _kind = tail;
                    _escaped = BufToString();
                    _hasEscape = true;
                    _templateEscapesValid = valid;
                    return p + 1;
                case '$' when src[p + 1] == '{':
                    _kind = sub;
                    _escaped = BufToString();
                    _hasEscape = true;
                    _templateEscapesValid = valid;
                    return p + 2;
                case '$':
                    BufAppend('$');
                    p++;
                    break;
            }

            int i = new ReadOnlySpan<char>(src + p, _len - p).IndexOfAny(s_templateStops);
            if (i < 0)
            {
                BufAppend(new ReadOnlySpan<char>(src + p, _len - p));
                ErrorAt(_tokStart, _len, "Unterminated template literal");
                _escaped = BufToString();
                _hasEscape = true;
                _kind = TokenKind.Undetermined;
                return _len;
            }
            BufAppend(new ReadOnlySpan<char>(src + p, i));
            p += i;
        }
    }

    /// <summary>
    /// Decodes the raw text of one template chunk (the text between <c>`</c> or <c>}</c> and <c>`</c> or
    /// <c>${</c>) into its cooked value with the scanner's own escape rules. Returns false if the text is not one
    /// complete chunk (it contains an unescaped <c>`</c> or <c>${</c>, or ends inside an escape) or contains an
    /// escape that is invalid in templates.
    /// </summary>
    internal static bool TryCookTemplate(string raw, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? cooked)
    {
        string text = "`" + raw + "`";
        var parser = Rent();
        try
        {
            parser._source = text;
            parser._len = text.Length;
            parser._errors = new List<ParseError>();
            parser._tokStart = 0;
            parser._hasEscape = false;
            parser._escaped = null;
            parser._templateEscapesValid = true;
            int end;
            fixed (char* src = text)
            {
                parser._src = src;
                end = parser.ScanTemplate(1, TokenKind.TemplateHead, TokenKind.NoSubstitutionTemplate);
                parser._src = null;
            }
            if (parser._kind != TokenKind.NoSubstitutionTemplate || end != text.Length || parser._errors.Count > 0 || !parser._templateEscapesValid)
            {
                cooked = null;
                return false;
            }
            cooked = parser._hasEscape ? parser._escaped! : raw;
            return true;
        }
        finally
        {
            Return(parser);
        }
    }

    /// <summary>Re-scans the current <c>}</c> token as the continuation of a template literal.</summary>
    private void NextTemplatePart()
    {
        _peekValid = false;
        _hasEscape = false;
        _escaped = null;
        _newLine = false;
        // _tokStart is the '}' position; scanning resumes right after it.
        _pos = ScanTemplate(_tokStart + 1, TokenKind.TemplateMiddle, TokenKind.TemplateTail);
        _tokEnd = _pos;
    }

    // ---- regular expressions -----------------------------------------------------------------------

    /// <summary>
    /// Re-scans the current <c>/</c> or <c>/=</c> token as a regular expression literal.
    /// Returns the literal length and the pattern length; the literal starts at the current token start.
    /// </summary>
    private (int LiteralLength, int PatternLength) ScanRegExp()
    {
        _peekValid = false;
        char* src = _src;
        int start = _tokStart;
        int p = start + 1;
        bool inClass = false, inEscape = false;
        int patternEnd;
        while (true)
        {
            char c = src[p];
            if (c == '\0' && p >= _len)
            {
                ErrorAt(start, p, "Unterminated regular expression");
                patternEnd = p;
                break;
            }
            p++;
            if (CharInfo.IsLineTerminator(c))
            {
                ErrorAt(start, p, "Unterminated regular expression");
                patternEnd = p - 1;
                break;
            }
            if (inEscape) inEscape = false;
            else if (c == '/' && !inClass) { patternEnd = p - 1; break; }
            else if (c == '[') inClass = true;
            else if (c == '\\') inEscape = true;
            else if (c == ']') inClass = false;
        }

        // Flags, with invalid/duplicate detection.
        int seen = 0;
        while (true)
        {
            char c = src[p];
            if (!IsIdPart(c)) break;
            p++;
            int bit = c switch
            {
                'd' => 1,
                'g' => 2,
                'i' => 4,
                'm' => 8,
                's' => 16,
                'u' => 32,
                'v' => 64,
                'y' => 128,
                _ => 0,
            };
            if (bit == 0) ErrorAt(p - 1, p, $"Invalid regular expression flag `{c}`");
            else if ((seen & bit) != 0) ErrorAt(p - 1, p, $"Duplicate regular expression flag `{c}`");
            seen |= bit;
        }

        _pos = p;
        _tokEnd = p;
        return (p - start, patternEnd - start - 1);
    }

    // ---- numbers -----------------------------------------------------------------------------------

    /// <summary>A number starting with <c>0</c>.</summary>
    private int ScanZero(int start)
    {
        char* src = _src;
        _kind = TokenKind.Number;
        int p = start + 1;
        switch (src[p])
        {
            case 'b': case 'B': return ScanNonDecimal(p + 1, 2);
            case 'o': case 'O': return ScanNonDecimal(p + 1, 8);
            case 'x': case 'X': return ScanNonDecimal(p + 1, 16);
            case 'e': case 'E': return CheckAfterNumber(ScanExponent(p + 1));
            case '.': return ScanFraction(p + 1);
            case 'n': return CheckAfterNumber(p + 1);
            case '_':
                ErrorAt(p, p + 1, "Invalid character `_`");
                _kind = TokenKind.Undetermined;
                return p + 1;
        }

        if (!IsDigit(src[p]))
        {
            _numExact = true;
            _numValue = 0;
            return CheckAfterNumber(p);
        }

        // Legacy octal (0777) or decimal with a leading zero (089).
        while (IsDigit(src[p])) p++;
        char c = src[p];
        if (c == '.') return ScanFraction(p + 1);
        if (c == 'e' || c == 'E') return CheckAfterNumber(ScanExponent(p + 1));
        return CheckAfterNumber(p);
    }

    /// <summary>A decimal number starting with 1-9.</summary>
    private int ScanDecimal(int start)
    {
        char* src = _src;
        _kind = TokenKind.Number;
        int p = start;
        ulong value = 0;
        char c;
        while (IsDigit(c = src[p]))
        {
            value = value * 10 + (uint)(c - '0');
            p++;
        }

        switch (c)
        {
            case '.':
                return ScanFraction(p + 1);
            case 'e':
            case 'E':
                return CheckAfterNumber(ScanExponent(p + 1));
            case 'n':
                return CheckAfterNumber(p + 1);
            case '_':
                p = ScanDigitsAfterSeparator(p);
                if (_kind == TokenKind.Undetermined) return p;
                c = src[p];
                if (c == '.') return ScanFraction(p + 1);
                if (c == 'e' || c == 'E') return CheckAfterNumber(ScanExponent(p + 1));
                if (c == 'n') return CheckAfterNumber(p + 1);
                return CheckAfterNumber(p);
        }

        if (p - start <= 15)
        {
            _numExact = true;
            _numValue = value;
        }
        return CheckAfterNumber(p);
    }

    /// <summary>Continues a digit run at a <c>_</c> separator; each separator must be followed by a digit.</summary>
    private int ScanDigitsAfterSeparator(int p)
    {
        char* src = _src;
        while (true)
        {
            char c = src[p];
            if (IsDigit(c)) { p++; continue; }
            if (c != '_') return p;
            p++;
            if (!IsDigit(src[p]))
            {
                UnexpectedCharacter(ref p);
                return p;
            }
        }
    }

    /// <summary>Optional digits after a decimal point, then an optional exponent.</summary>
    private int ScanFraction(int p)
    {
        char* src = _src;
        _kind = TokenKind.Number;
        if (IsDigit(src[p]))
        {
            p = ScanDigitsAfterSeparator(p + 1);
            if (_kind == TokenKind.Undetermined) return p;
        }
        char c = src[p];
        if (c == 'e' || c == 'E') p = ScanExponent(p + 1);
        if (_kind == TokenKind.Undetermined) return p;
        return CheckAfterNumber(p);
    }

    /// <summary>A number starting with <c>.digit</c> (p is at the first digit).</summary>
    private int ScanAfterLeadingDot(int p)
    {
        _kind = TokenKind.Number;
        p = ScanDigitsAfterSeparator(p);
        if (_kind == TokenKind.Undetermined) return p;
        char c = _src[p];
        if (c == 'e' || c == 'E') p = ScanExponent(p + 1);
        if (_kind == TokenKind.Undetermined) return p;
        return CheckAfterNumber(p);
    }

    /// <summary>The exponent part after <c>e</c>/<c>E</c>: optional sign and at least one digit.</summary>
    private int ScanExponent(int p)
    {
        char* src = _src;
        _kind = TokenKind.Number;
        char c = src[p];
        if (c == '+' || c == '-') p++;
        if (!IsDigit(src[p]))
        {
            UnexpectedCharacter(ref p);
            return p;
        }
        return ScanDigitsAfterSeparator(p + 1);
    }

    /// <summary>Binary, octal or hex digits after the prefix, with separators and an optional <c>n</c>.</summary>
    private int ScanNonDecimal(int p, int radix)
    {
        char* src = _src;
        _kind = TokenKind.Number;
        if (CharInfo.DigitValue(src[p]) >= radix)
        {
            UnexpectedCharacter(ref p);
            return p;
        }

        ulong value = 0;
        bool overflow = false;
        int shift = radix == 16 ? 4 : radix == 8 ? 3 : 1;
        while (true)
        {
            char c = src[p];
            int d = CharInfo.DigitValue(c);
            if (d < radix)
            {
                if (value >> (64 - shift) != 0) overflow = true;
                value = (value << shift) | (uint)d;
                p++;
                continue;
            }
            if (c == '_')
            {
                p++;
                if (CharInfo.DigitValue(src[p]) >= radix)
                {
                    UnexpectedCharacter(ref p);
                    return p;
                }
                continue;
            }
            break;
        }

        if (src[p] == 'n') return CheckAfterNumber(p + 1);
        if (!overflow && value <= (1UL << 53))
        {
            _numExact = true;
            _numValue = value;
        }
        return CheckAfterNumber(p);
    }

    /// <summary>A numeric literal must not be immediately followed by an identifier start or digit.</summary>
    private int CheckAfterNumber(int p)
    {
        char* src = _src;
        _kind = TokenKind.Number;
        char c = src[p];
        if (c < 0x80)
        {
            if (!IsIdPart(c)) return p;
        }
        else if (!CharInfo.IsIdentifierStart(char.IsHighSurrogate(c) && char.IsLowSurrogate(src[p + 1]) ? char.ConvertToUtf32(c, src[p + 1]) : c))
        {
            return p;
        }

        int start = p;
        p += char.IsHighSurrogate(c) && char.IsLowSurrogate(src[p + 1]) ? 2 : 1;
        while (true)
        {
            char n = src[p];
            if (n < 0x80)
            {
                if (!CharInfo.IsAsciiIdStart(n)) break;
                p++;
                continue;
            }
            int w = char.IsHighSurrogate(n) && char.IsLowSurrogate(src[p + 1]) ? 2 : 1;
            int cp = w == 2 ? char.ConvertToUtf32(n, src[p + 1]) : n;
            if (!CharInfo.IsIdStartUnicode(cp)) break;
            p += w;
        }
        ErrorAt(start, p, "Invalid characters after number");
        _kind = TokenKind.Undetermined;
        _numExact = false;
        return p;
    }

    /// <summary>Reports an unexpected character (or end of input) at <paramref name="p"/> and consumes it.</summary>
    private void UnexpectedCharacter(ref int p)
    {
        _kind = TokenKind.Undetermined;
        _numExact = false;
        char c = _src[p];
        if (c == '\0' && p >= _len)
        {
            ErrorAt(p, p, "Unexpected end of file");
            return;
        }
        ErrorAt(p, p + 1, $"Invalid character `{c}`");
        p++;
    }
}
