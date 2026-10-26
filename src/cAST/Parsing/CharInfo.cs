using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cAST.Parsing;

/// <summary>Character classification for the scanner.</summary>
internal static class CharInfo
{
    internal const byte IdStart = 1;
    internal const byte IdPart = 2;

    /// <summary>Identifier flags for ASCII characters (index &lt; 128).</summary>
    internal static ReadOnlySpan<byte> Ascii =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0,
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 0, 0, 0, 3,
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 0, 0, 0, 0, 0,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAsciiIdPart(char c) => c < 128 && (Unsafe.Add(ref MemoryMarshal.GetReference(Ascii), c) & IdPart) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsAsciiIdStart(char c) => c < 128 && (Unsafe.Add(ref MemoryMarshal.GetReference(Ascii), c) & IdStart) != 0;

    internal static bool IsIdentifierStart(int cp)
    {
        if (cp < 128) return cp >= 0 && (Ascii[cp] & IdStart) != 0;
        return IsIdStartUnicode(cp);
    }

    internal static bool IsIdentifierPart(int cp)
    {
        if (cp < 128) return cp >= 0 && (Ascii[cp] & IdPart) != 0;
        return IsIdContinueUnicode(cp);
    }

    internal static bool IsIdStartUnicode(int cp)
    {
        switch (CharUnicodeInfo.GetUnicodeCategory(cp))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
                return true;
        }
        // Other_ID_Start
        return cp is 0x1885 or 0x1886 or 0x2118 or 0x212E or 0x309B or 0x309C;
    }

    internal static bool IsIdContinueUnicode(int cp)
    {
        switch (CharUnicodeInfo.GetUnicodeCategory(cp))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.ConnectorPunctuation:
                return true;
        }
        // Other_ID_Start, Other_ID_Continue, ZWNJ, ZWJ
        return cp is 0x1885 or 0x1886 or 0x2118 or 0x212E or 0x309B or 0x309C
            or 0x00B7 or 0x0387 or (>= 0x1369 and <= 0x1371) or 0x19DA or 0x200C or 0x200D;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is an IdentifierName (an identifier start followed by identifier parts,
    /// reserved words included), with supplementary characters given as surrogate pairs.
    /// </summary>
    internal static bool IsIdentifierName(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        bool first = true;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                cp = char.ConvertToUtf32(text[i], text[++i]);
            else if (char.IsSurrogate(text[i]))
                return false;
            if (first ? !IsIdentifierStart(cp) : !IsIdentifierPart(cp)) return false;
            first = false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsLineTerminator(int c) => c is '\n' or '\r' or 0x2028 or 0x2029;

    /// <summary>Non-ASCII JavaScript whitespace: any Zs character or U+FEFF.</summary>
    internal static bool IsUnicodeWhitespace(int cp) =>
        cp == 0xFEFF || (cp >= 0x80 && CharUnicodeInfo.GetUnicodeCategory(cp) == UnicodeCategory.SpaceSeparator);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HexValue(char c)
    {
        uint d = (uint)c - '0';
        if (d <= 9) return (int)d;
        uint l = ((uint)c | 0x20) - 'a';
        return l <= 5 ? (int)l + 10 : -1;
    }

    /// <summary>Digit value for numeric literal scanning (16 for non-digits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int DigitValue(char c)
    {
        int v = HexValue(c);
        return v < 0 ? 16 : v;
    }
}
