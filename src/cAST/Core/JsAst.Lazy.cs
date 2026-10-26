using System.Runtime.CompilerServices;

namespace cAST;

// Lazy source-slice strings.
//
// The parser never hashes or copies identifier names and literal text: a "lazy" string slot holds 0 and the
// text is described by the node's position and length slots. The first time a view asks for the string (or
// its id), the slice is interned and the id is cached in the slot. This keeps parsing free of string work,
// like go-fAST's zero-copy substrings, while still giving canonical integer ids on demand.
public sealed partial class JsAst
{
    /// <summary>Describes the source slice backing lazy string slot <paramref name="slot"/> of <paramref name="r"/>.</summary>
    private static bool TryGetLazySlice(ref NodeRecord r, int slot, out int start, out int length)
    {
        switch (r.Kind)
        {
            case NodeKind.Identifier when slot == 0:
                start = r.C;
                length = r.D;
                return length > 0;
            case NodeKind.StringLiteral:
                if (r.D == 0) break;
                if (slot == 0)
                {
                    int q = (r.Flags & 1) != 0 ? 0 : 1; // Unquoted: identifier-named property keys
                    start = r.C + q;
                    length = r.D - 2 * q;
                    return true;
                }
                start = r.C;
                length = r.D;
                return true;
            case NodeKind.NumberLiteral when slot == 2:
                start = r.D;
                length = r.E;
                return length > 0;
            case NodeKind.BigIntLiteral when slot == 1:
                start = r.C;
                length = r.D;
                return length > 0;
            case NodeKind.TemplateElement:
                // Parsed (slot 1) falls back to the raw literal when there were no escapes.
                start = r.C + 1;
                length = r.D;
                return true;
            case NodeKind.RegExpLiteral:
                if (r.E == 0) break;
                switch (slot)
                {
                    case 0: start = r.D; length = r.E; return true;
                    case 1: start = r.D + 1; length = r.F; return true;
                    default: start = r.D + 2 + r.F; length = r.E - r.F - 2; return true;
                }
        }
        start = length = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref int SlotRef(ref NodeRecord r, int slot) => ref Unsafe.Add(ref r.A, slot);

    /// <summary>The (canonical) string id of lazy slot <paramref name="slot"/>, interning the source slice on first use.</summary>
    internal int GetLazyStringId(int id, int slot)
    {
        ref NodeRecord r = ref _nodes[id];
        ref int s = ref SlotRef(ref r, slot);
        if (s != 0) return s;
        if (!TryGetLazySlice(ref r, slot, out int start, out int length)) return 0;
        int sid = length == 0 ? KnownStrings.Empty : InternSource(start, length);
        s = sid; // InternSource only touches the string table, so the ref into _nodes is still valid
        return sid;
    }

    /// <summary>
    /// The interned name id of Identifier node <paramref name="id"/> (0 for an empty name), interning its source
    /// slice on first use: <see cref="GetLazyStringId"/> specialized for the resolver's hottest call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int IdentifierNameId(int id)
    {
        int name = _nodes[id].A;
        return name != 0 ? name : InternIdentifierName(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int InternIdentifierName(int id)
    {
        ref NodeRecord r = ref _nodes[id];
        if (r.Kind != NodeKind.Identifier) return GetLazyStringId(id, 0);
        if (r.D <= 0) return 0;
        int sid = InternSource(r.C, r.D);
        _nodes[id].A = sid; // InternSource only touches the string table, but re-index anyway: cheap and obviously safe
        return sid;
    }

    internal string GetLazyString(int id, int slot)
    {
        int sid = GetLazyStringId(id, slot);
        return sid == 0 ? string.Empty : GetString(sid);
    }

    internal string? GetLazyStringOrNull(int id, int slot)
    {
        int sid = GetLazyStringId(id, slot);
        return sid == 0 ? null : GetString(sid);
    }

    /// <summary>The text of lazy slot <paramref name="slot"/> without materializing or interning it.</summary>
    internal ReadOnlySpan<char> GetLazySpan(int id, int slot)
    {
        ref NodeRecord r = ref _nodes[id];
        int s = SlotRef(ref r, slot);
        if (s != 0) return GetStringSpan(s);
        return TryGetLazySlice(ref r, slot, out int start, out int length) ? _source.AsSpan(start, length) : default;
    }

    /// <summary>Whether lazy slot <paramref name="slot"/> has a value (explicit or from the source).</summary>
    internal bool HasLazyString(int id, int slot)
    {
        ref NodeRecord r = ref _nodes[id];
        return SlotRef(ref r, slot) != 0 || TryGetLazySlice(ref r, slot, out _, out _);
    }

    /// <summary>Sets lazy string slot <paramref name="slot"/> of node <paramref name="id"/>; null clears an optional slot.</summary>
    internal void SetLazyString(int id, int slot, string? value) => SetLazyStringId(id, slot, value is null ? 0 : Intern(value));

    /// <summary>
    /// Sets lazy string slot <paramref name="slot"/> of node <paramref name="id"/> to string id <paramref name="sid"/>
    /// (0 clears an optional slot) and keeps the node's other text in step, so the generator prints what was set:
    /// a string literal's new value drops the raw source text (the value is quoted when printed); an identifier's
    /// name must be an IdentifierName; a regular expression's literal, pattern and flags, and a template element's
    /// raw and cooked text, are derived from one another.
    /// </summary>
    internal void SetLazyStringId(int id, int slot, int sid)
    {
        CheckStringId(sid);
        switch (_nodes[id].Kind)
        {
            case NodeKind.Identifier:
                {
                    if (!Parsing.CharInfo.IsIdentifierName(GetStringSpan(sid)))
                        throw new ArgumentException($"\"{GetString(sid)}\" is not a valid identifier name.", nameof(sid));
                    ref NodeRecord r = ref _nodes[id];
                    r.A = sid;
                    r.D = 0; // the source slice (and its length) no longer describe the name
                    return;
                }
            case NodeKind.StringLiteral when slot == 0:
                {
                    RequireText(sid);
                    ref NodeRecord r = ref _nodes[id];
                    r.A = sid;
                    r.B = 0;
                    r.D = 0;
                    return;
                }
            case NodeKind.StringLiteral:
                {
                    if (sid != 0)
                    {
                        _nodes[id].B = sid;
                        return;
                    }
                    // Clearing Raw drops the source slice, which the value may still be reading: materialize it first.
                    if (_nodes[id].A == 0) _nodes[id].A = GetLazyStringId(id, 0);
                    ref NodeRecord r = ref _nodes[id];
                    r.B = 0;
                    r.D = 0;
                    return;
                }
            case NodeKind.NumberLiteral:
                {
                    ref NodeRecord r = ref _nodes[id];
                    r.C = sid;
                    if (sid == 0) r.E = 0;
                    return;
                }
            case NodeKind.BigIntLiteral:
                {
                    ref NodeRecord r = ref _nodes[id];
                    r.B = sid;
                    if (sid == 0) r.D = 0;
                    return;
                }
            case NodeKind.RegExpLiteral:
                SetRegExpText(id, slot, sid);
                return;
            case NodeKind.TemplateElement:
                SetTemplateText(id, slot, sid);
                return;
            default:
                SlotRef(ref _nodes[id], slot) = sid;
                return;
        }
    }

    /// <summary>Sets one of a regular expression's literal (0), pattern (1) or flags (2) and derives the others.</summary>
    private void SetRegExpText(int id, int slot, int sid)
    {
        RequireText(sid);
        // The source slice is about to be dropped: materialize all three parts first.
        int literal = GetLazyStringId(id, 0), pattern = GetLazyStringId(id, 1), flags = GetLazyStringId(id, 2);
        switch (slot)
        {
            case 0:
                {
                    string text = GetString(sid);
                    if (!LiteralText.TrySplitRegExpLiteral(text, out int bodyLength))
                        throw new ArgumentException($"\"{text}\" is not a regular expression literal (/pattern/flags).", nameof(sid));
                    literal = sid;
                    pattern = Intern(text.AsSpan(1, bodyLength));
                    flags = Intern(text.AsSpan(bodyLength + 2));
                    break;
                }
            case 1:
                pattern = Intern(LiteralText.EscapeRegExpPattern(GetStringSpan(sid)));
                literal = Intern(string.Concat("/", PatternText(pattern), "/", GetStringSpan(flags)));
                break;
            default:
                if (!LiteralText.IsValidRegExpFlags(GetStringSpan(sid)))
                    throw new ArgumentException($"\"{GetString(sid)}\" is not a valid set of regular expression flags.", nameof(sid));
                flags = sid;
                literal = Intern(string.Concat("/", PatternText(pattern), "/", GetStringSpan(flags)));
                break;
        }
        ref NodeRecord r = ref _nodes[id];
        r.A = literal;
        r.B = pattern;
        r.C = flags;
        r.E = 0;
        r.F = 0;
    }

    /// <summary>The pattern text of a regular expression (a node built from scratch has none until it is set).</summary>
    private ReadOnlySpan<char> PatternText(int pattern) => pattern == 0 ? "(?:)" : GetStringSpan(pattern);

    /// <summary>Sets a template element's raw (0) or cooked (1) text and derives the other.</summary>
    private void SetTemplateText(int id, int slot, int sid)
    {
        RequireText(sid);
        int raw, cooked;
        if (slot == 1)
        {
            cooked = sid;
            raw = Intern(LiteralText.EscapeTemplateRaw(GetStringSpan(sid)));
        }
        else
        {
            string text = GetString(sid);
            if (!Parsing.JsParser.TryCookTemplate(text, out string? value))
                throw new ArgumentException($"\"{text}\" is not the raw text of a template chunk (unescaped ` or ${{, or an invalid escape).", nameof(sid));
            raw = sid;
            cooked = Intern(value);
        }
        ref NodeRecord r = ref _nodes[id];
        r.A = raw;
        r.B = cooked;
        r.D = 0;
    }

    private static void RequireText(int sid)
    {
        if (sid == 0) throw new ArgumentException("This text is required (use the empty string for empty text).", nameof(sid));
    }

    /// <summary>Validates a string id from the public API (0 = none).</summary>
    internal int CheckStringId(int sid)
    {
        if ((uint)sid >= (uint)_stringCount)
            throw new ArgumentOutOfRangeException(nameof(sid), sid, "Not a string id of this JsAst.");
        return sid;
    }

    /// <summary>
    /// A number or BigInt literal's value was set: drops its raw source text, so the generator prints the value
    /// instead of the original digits.
    /// </summary>
    internal void OnValueSet(int id)
    {
        ref NodeRecord r = ref _nodes[id];
        switch (r.Kind)
        {
            case NodeKind.NumberLiteral:
                r.C = 0;
                r.E = 0;
                break;
            case NodeKind.BigIntLiteral:
                r.B = 0;
                r.D = 0;
                break;
        }
    }

    /// <summary>
    /// Makes node <paramref name="id"/>'s text independent of its source position, before that position changes:
    /// every lazy string still read from the source is materialized and the slice lengths are cleared. (A lazy
    /// string is described by the node's position and a length, so moving the position would change the text.)
    /// </summary>
    internal void DetachSource(int id)
    {
        switch (_nodes[id].Kind)
        {
            case NodeKind.Identifier:
                if (_nodes[id].A == 0) _nodes[id].A = GetLazyStringId(id, 0);
                _nodes[id].D = 0;
                break;
            case NodeKind.StringLiteral:
                if (_nodes[id].D == 0) break;
                if (_nodes[id].A == 0) _nodes[id].A = GetLazyStringId(id, 0);
                if (_nodes[id].B == 0) _nodes[id].B = GetLazyStringId(id, 1);
                _nodes[id].D = 0;
                break;
            case NodeKind.NumberLiteral:
                if (_nodes[id].E == 0) break;
                if (_nodes[id].C == 0) _nodes[id].C = GetLazyStringId(id, 2);
                _nodes[id].E = 0;
                break;
            case NodeKind.BigIntLiteral:
                if (_nodes[id].D == 0) break;
                if (_nodes[id].B == 0) _nodes[id].B = GetLazyStringId(id, 1);
                _nodes[id].D = 0;
                break;
            case NodeKind.RegExpLiteral:
                {
                    if (_nodes[id].E == 0) break;
                    int literal = GetLazyStringId(id, 0), pattern = GetLazyStringId(id, 1), flags = GetLazyStringId(id, 2);
                    ref NodeRecord r = ref _nodes[id];
                    r.A = literal;
                    r.B = pattern;
                    r.C = flags;
                    r.E = 0;
                    r.F = 0;
                    break;
                }
            case NodeKind.TemplateElement:
                {
                    int raw = GetLazyStringId(id, 0), cooked = GetLazyStringId(id, 1);
                    ref NodeRecord r = ref _nodes[id];
                    r.A = raw;
                    r.B = cooked;
                    r.D = 0;
                    break;
                }
        }
    }
}
