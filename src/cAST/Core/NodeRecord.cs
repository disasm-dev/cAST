using System.Runtime.InteropServices;

namespace cAST;

/// <summary>
/// The fixed 32-byte storage record of every AST node. The meaning of the payload slots
/// (<see cref="A"/>..<see cref="G"/>) depends on <see cref="Kind"/> and is defined by the schema in tools/AstGen.
/// Records contain no object references, so the GC never needs to scan the node array.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal struct NodeRecord
{
    public NodeKind Kind;
    public byte Op;
    public byte Flags;
    public byte Extra;
    public int A;
    public int B;
    public int C;
    public int D;
    public int E;
    public int F;
    public int G;
}

/// <summary>A (start, count) range inside <see cref="JsAst"/>'s list pool.</summary>
internal readonly record struct ListRange(int Start, int Count)
{
    public static ListRange Empty => default;
}

/// <summary>
/// A string-table entry. Strings taken from the source are stored lazily as a slice and only
/// materialized when asked for, so parsing allocates no strings for names and literals.
/// </summary>
internal struct StringEntry
{
    public string? Value;
    public int Start;
    public int Length;
}
