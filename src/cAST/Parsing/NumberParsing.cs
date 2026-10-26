using System.Globalization;
using System.Numerics;

namespace cAST.Parsing;

/// <summary>Converts numeric literal source text to values (the slow path; most integers are converted while scanning).</summary>
internal static class NumberParsing
{
    /// <summary>Parses a Number literal (decimal, 0x/0o/0b, legacy octal, with optional separators).</summary>
    internal static bool TryParseNumber(ReadOnlySpan<char> raw, out double value)
    {
        Span<char> buffer = raw.Length <= 128 ? stackalloc char[raw.Length] : new char[raw.Length];
        int n = 0;
        foreach (char c in raw)
            if (c != '_') buffer[n++] = c;
        ReadOnlySpan<char> s = buffer[..n];

        if (s.Length >= 2 && s[0] == '0')
        {
            switch (s[1])
            {
                case 'x': case 'X': return TryParseRadix(s[2..], 16, out value);
                case 'o': case 'O': return TryParseRadix(s[2..], 8, out value);
                case 'b': case 'B': return TryParseRadix(s[2..], 2, out value);
            }

            // Legacy octal: 0 followed only by octal digits.
            bool allDigits = true, allOctal = true;
            foreach (char c in s[1..])
            {
                if (c is < '0' or > '9') { allDigits = false; break; }
                if (c > '7') allOctal = false;
            }
            if (allDigits && allOctal) return TryParseRadix(s[1..], 8, out value);
        }

        return double.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseRadix(ReadOnlySpan<char> digits, int radix, out double value)
    {
        value = 0;
        if (digits.IsEmpty) return false;
        ulong acc = 0;
        int shift = radix == 16 ? 4 : radix == 8 ? 3 : 1;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = CharInfo.DigitValue(digits[i]);
            if (d >= radix) return false;
            if (acc >> (64 - shift) != 0)
            {
                // Overflows 64 bits: fall back to BigInteger, whose double conversion rounds correctly.
                return TryParseBig(digits, radix, out value);
            }
            acc = (acc << shift) | (uint)d;
        }
        value = acc;
        return true;
    }

    private static bool TryParseBig(ReadOnlySpan<char> digits, int radix, out double value)
    {
        value = 0;
        if (!TryParseBigInteger(digits, radix, out var big)) return false;
        value = (double)big;
        return true;
    }

    /// <summary>Parses a BigInt literal including its trailing <c>n</c>.</summary>
    internal static bool TryParseBigInt(ReadOnlySpan<char> raw, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (raw.Length == 0 || raw[^1] != 'n') return false;
        raw = raw[..^1];
        Span<char> buffer = raw.Length <= 128 ? stackalloc char[raw.Length] : new char[raw.Length];
        int n = 0;
        foreach (char c in raw)
            if (c != '_') buffer[n++] = c;
        ReadOnlySpan<char> s = buffer[..n];

        int radix = 10;
        if (s.Length >= 2 && s[0] == '0')
        {
            switch (s[1])
            {
                case 'x': case 'X': radix = 16; s = s[2..]; break;
                case 'o': case 'O': radix = 8; s = s[2..]; break;
                case 'b': case 'B': radix = 2; s = s[2..]; break;
            }
        }
        if (radix == 10)
            return BigInteger.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return TryParseBigInteger(s, radix, out value);
    }

    private static bool TryParseBigInteger(ReadOnlySpan<char> digits, int radix, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (digits.IsEmpty) return false;
        foreach (char c in digits)
        {
            int d = CharInfo.DigitValue(c);
            if (d >= radix) return false;
            value = value * radix + d;
        }
        return true;
    }
}
