using System.Globalization;
using System.Text;

namespace cAST;

/// <summary>
/// Conversions between the values of regular expression and template literals and their source text, used when a
/// value is set so that the text the generator prints always matches it.
/// </summary>
internal static class LiteralText
{
    /// <summary>
    /// Returns <paramref name="pattern"/> as the body of a regular expression literal, escaping what would end or
    /// break the literal (like <c>RegExp.prototype.source</c>): a <c>/</c> outside a character class and line
    /// terminators. An empty pattern becomes <c>(?:)</c>, since <c>//</c> starts a comment.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern ends with an unpaired backslash.</exception>
    internal static string EscapeRegExpPattern(ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty) return "(?:)";
        StringBuilder? sb = null;
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            string? replacement = null;
            if (c == '\\')
            {
                if (i + 1 == pattern.Length)
                    throw new ArgumentException("A regular expression pattern cannot end with an unpaired backslash.", nameof(pattern));
                char next = pattern[i + 1];
                // A backslash before a line terminator matches that character; the escape letter does the same.
                string? escapedTerminator = LineTerminatorEscape(next);
                if (escapedTerminator is not null)
                {
                    sb ??= new StringBuilder(pattern.Length + 8).Append(pattern[..i]);
                    sb.Append(escapedTerminator);
                }
                else
                {
                    sb?.Append(c).Append(next);
                }
                i++;
                continue;
            }
            if (c == '[' && !inClass) inClass = true;
            else if (c == ']' && inClass) inClass = false;
            else if (c == '/' && !inClass) replacement = "\\/";
            else replacement = LineTerminatorEscape(c);

            if (replacement is not null)
            {
                sb ??= new StringBuilder(pattern.Length + 8).Append(pattern[..i]);
                sb.Append(replacement);
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? pattern.ToString();
    }

    private static string? LineTerminatorEscape(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        (char)0x2028 => "\\u2028",
        (char)0x2029 => "\\u2029",
        _ => null,
    };

    /// <summary>
    /// Splits a regular expression literal <c>/body/flags</c> the way the scanner does (the body ends at the
    /// first <c>/</c> outside a character class and escape). Returns false for text that is not one literal.
    /// </summary>
    internal static bool TrySplitRegExpLiteral(ReadOnlySpan<char> literal, out int bodyLength)
    {
        bodyLength = 0;
        if (literal.Length < 3 || literal[0] != '/') return false;
        bool inClass = false, inEscape = false;
        int i = 1;
        for (; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c is '\n' or '\r' or (char)0x2028 or (char)0x2029) return false;
            if (inEscape) inEscape = false;
            else if (c == '\\') inEscape = true;
            else if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass) break;
        }
        if (i >= literal.Length || i == 1) return false; // unterminated, or `//` (a comment)
        bodyLength = i - 1;
        return IsValidRegExpFlags(literal[(i + 1)..]);
    }

    /// <summary>
    /// Whether <paramref name="flags"/> is a valid flag set: letters from <c>dgimsuvy</c>, none repeated, and not
    /// both <c>u</c> and <c>v</c>.
    /// </summary>
    internal static bool IsValidRegExpFlags(ReadOnlySpan<char> flags)
    {
        int seen = 0;
        foreach (char c in flags)
        {
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
            if (bit == 0 || (seen & bit) != 0) return false;
            seen |= bit;
        }
        return (seen & (32 | 64)) != (32 | 64);
    }

    /// <summary>
    /// Returns raw template text whose cooked value is <paramref name="cooked"/>: <c>\</c>, <c>`</c> and the
    /// <c>$</c> of <c>${</c> are escaped, and so are control characters (other than tab), U+2028/U+2029 and lone
    /// surrogates, so the chunk prints on one line and survives any encoding. A tag function sees this text as the
    /// raw value.
    /// </summary>
    internal static string EscapeTemplateRaw(ReadOnlySpan<char> cooked)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < cooked.Length; i++)
        {
            char c = cooked[i];
            string? replacement = c switch
            {
                '\\' => "\\\\",
                '`' => "\\`",
                '$' when i + 1 < cooked.Length && cooked[i + 1] == '{' => "\\$",
                '\n' => "\\n",
                '\r' => "\\r",
                (char)0x2028 => "\\u2028",
                (char)0x2029 => "\\u2029",
                _ when c < 0x20 && c != '\t' || c == 0x7F => "\\x" + ((int)c).ToString("x2", CultureInfo.InvariantCulture),
                _ when char.IsHighSurrogate(c) && i + 1 < cooked.Length && char.IsLowSurrogate(cooked[i + 1]) => null,
                _ when char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(cooked[i - 1]) => null,
                _ when char.IsSurrogate(c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (replacement is not null)
            {
                sb ??= new StringBuilder(cooked.Length + 8).Append(cooked[..i]);
                sb.Append(replacement);
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? cooked.ToString();
    }
}
