using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cAST;

public sealed partial class JsAst
{
    internal struct Bucket
    {
        public int Hash;
        public int Id;
    }

    internal StringEntry[] _strings = null!;
    internal int _stringCount;
    private Bucket[] _buckets = null!;
    private int _bucketMask;
    private int _internedCount;

    private void InitStrings(int capacity)
    {
        _strings = ArenaPool.RentStrings(capacity);
        _strings[0] = default;
        _strings[1] = new StringEntry { Value = string.Empty };

        int buckets = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(256, capacity));
        _buckets = ArenaPool.RentBuckets(buckets);
        _bucketMask = buckets - 1;
        _internedCount = 0;

        InsertBucket(NameHash.Compute(ReadOnlySpan<char>.Empty), KnownStrings.Empty);
        var entries = KnownStrings.Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            string text = entries[i].Text;
            int id = i + 2;
            _strings[id] = new StringEntry { Value = text, Length = text.Length };
            InsertBucket(NameHash.Compute(text), id);
        }
        _stringCount = KnownStrings.Count;
    }

    private void InsertBucket(int hash, int id)
    {
        var buckets = _buckets;
        int mask = _bucketMask;
        int i = hash & mask;
        while (buckets[i].Id != 0) i = (i + 1) & mask;
        buckets[i].Hash = hash;
        buckets[i].Id = id;
        if (++_internedCount * 2 > buckets.Length) GrowBuckets();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowBuckets()
    {
        var old = _buckets;
        var grown = new Bucket[old.Length * 2];
        int mask = grown.Length - 1;
        foreach (var b in old)
        {
            if (b.Id == 0) continue;
            int i = b.Hash & mask;
            while (grown[i].Id != 0) i = (i + 1) & mask;
            grown[i] = b;
        }
        _buckets = grown;
        _bucketMask = mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AddEntry(string? value, int start, int length)
    {
        int id = _stringCount;
        if (id == _strings.Length) GrowStrings();
        ref StringEntry e = ref _strings[id];
        e.Value = value;
        e.Start = start;
        e.Length = length;
        _stringCount = id + 1;
        return id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowStrings()
    {
        var grown = new StringEntry[_strings.Length * 2];
        Array.Copy(_strings, grown, _stringCount);
        _strings = grown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EntryEquals(int id, ReadOnlySpan<char> text)
    {
        ref StringEntry e = ref _strings[id];
        if (e.Value is { } value) return text.SequenceEqual(value);
        return e.Length == text.Length && text.SequenceEqual(_source.AsSpan(e.Start, e.Length));
    }

    /// <summary>Interns a slice of the source text; the string is materialized lazily.</summary>
    internal int InternSource(int start, int length, int hash)
    {
        ReadOnlySpan<char> text = _source.AsSpan(start, length);
        var buckets = _buckets;
        int mask = _bucketMask;
        int i = hash & mask;
        while (true)
        {
            ref Bucket b = ref buckets[i];
            int id = b.Id;
            if (id == 0)
            {
                id = AddEntry(null, start, length);
                b.Hash = hash;
                b.Id = id;
                if (++_internedCount * 2 > buckets.Length) GrowBuckets();
                return id;
            }
            if (b.Hash == hash && EntryEquals(id, text)) return id;
            i = (i + 1) & mask;
        }
    }

    internal int InternSource(int start, int length) => InternSource(start, length, NameHash.Compute(_source.AsSpan(start, length)));

    /// <summary>Interns <paramref name="value"/>, returning its canonical string id.</summary>
    public int Intern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int hash = NameHash.Compute(value);
        var buckets = _buckets;
        int mask = _bucketMask;
        int i = hash & mask;
        while (true)
        {
            ref Bucket b = ref buckets[i];
            int id = b.Id;
            if (id == 0)
            {
                id = AddEntry(value, 0, value.Length);
                b.Hash = hash;
                b.Id = id;
                if (++_internedCount * 2 > buckets.Length) GrowBuckets();
                return id;
            }
            if (b.Hash == hash && EntryEquals(id, value)) return id;
            i = (i + 1) & mask;
        }
    }

    /// <summary>Adds a (non-interned) lazily materialized slice of the source.</summary>
    internal int AddSourceSlice(int start, int length) => AddEntry(null, start, length);

    /// <summary>Adds a (non-interned) string.</summary>
    internal int AddString(string value) => AddEntry(value, 0, value.Length);

    /// <summary>Returns the string with the given id (materializing source slices on first use).</summary>
    public string GetString(int id)
    {
        if (id == 0) return string.Empty;
        ref StringEntry e = ref _strings[id];
        return e.Value ??= _source.Substring(e.Start, e.Length);
    }

    /// <summary>Returns the string with the given id, or <c>null</c> for id 0.</summary>
    public string? GetStringOrNull(int id) => id == 0 ? null : GetString(id);

    /// <summary>Returns the text of the string with the given id without materializing it.</summary>
    public ReadOnlySpan<char> GetStringSpan(int id)
    {
        if (id == 0) return default;
        ref StringEntry e = ref _strings[id];
        return e.Value is { } value ? value.AsSpan() : _source.AsSpan(e.Start, e.Length);
    }

    /// <summary>The length of the string with the given id.</summary>
    public int GetStringLength(int id)
    {
        if (id == 0) return 0;
        ref StringEntry e = ref _strings[id];
        return e.Value?.Length ?? e.Length;
    }

    /// <summary>The number of entries in the string table.</summary>
    public int StringCount => _stringCount;
}

/// <summary>A fast, length-independent hash for identifier-like strings (first, middle and last 4 chars + length).</summary>
internal static class NameHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compute(ReadOnlySpan<char> text) => Compute(ref MemoryMarshal.GetReference(text), text.Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compute(ref char p, int len)
    {
        ulong h;
        if (len >= 4)
        {
            ulong a = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref p));
            ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref p, len - 4)));
            h = (a * 0x9E3779B97F4A7C15UL) ^ (b * 0xC2B2AE3D27D4EB4FUL);
            if (len > 8)
            {
                ulong m = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref p, (len >> 1) - 2)));
                h ^= m * 0x165667B19E3779F9UL;
            }
        }
        else if (len >= 2)
        {
            uint a = Unsafe.ReadUnaligned<uint>(ref Unsafe.As<char, byte>(ref p));
            uint b = Unsafe.ReadUnaligned<uint>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref p, len - 2)));
            h = (((ulong)a << 32) | b) * 0x9E3779B97F4A7C15UL;
        }
        else
        {
            h = len == 1 ? p * 0x9E3779B97F4A7C15UL : 0;
        }
        h += (ulong)len * 0x27D4EB2F165667C5UL;
        h ^= h >> 31;
        h *= 0x94D049BB133111EBUL;
        return (int)(h >> 32);
    }
}
