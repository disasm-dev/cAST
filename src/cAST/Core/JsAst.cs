using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cAST;

/// <summary>
/// An arena holding a whole JavaScript AST. Nodes are 32-byte records in one array and refer to each
/// other by integer id; node views such as <see cref="BinaryExpression"/> or <see cref="Expression"/> are
/// lightweight (arena, id) structs. Strings live in an interned string table; strings sliced from the source
/// are materialized lazily.
/// </summary>
/// <remarks>
/// <para>
/// Threading: use an arena from one thread at a time, reads included (the first read of a name or literal text interns
/// it into the arena). It may move between threads. Separate arenas can be parsed, walked, transformed, resolved and
/// generated on separate threads at the same time: the library shares no mutable state between them, and it never
/// runs work in parallel itself (a very deep tree continues on a fresh thread while the calling thread waits).
/// </para>
/// <para>
/// Nodes from different arenas cannot be mixed; use <see cref="Import(Node)"/> to deep-copy a node into another arena.
/// </para>
/// </remarks>
public sealed partial class JsAst : IDisposable
{
    internal NodeRecord[] _nodes;
    internal int _nodeCount;

    internal int[] _lists;
    internal int _listCount;

    private BigInteger[]? _bigInts;
    private int _bigIntCount;

    internal int _root;
    internal List<ParseError>? _errors;

    // The next scope context the resolver may give a scope (TopLevel is always the program's). Numbers are never
    // reused within an arena, so a context left over from an earlier resolution cannot match a current one.
    internal int _nextScopeContext = (int)ScopeContext.TopLevel + 1;

    // The binding numbers the resolver has given (Identifier.BindingNumber): each below this, never given twice to two
    // bindings; a global's is its name's (by string id, plus one, 0 for none) whichever run gives it.
    internal int _bindingNumbers;
    internal int[] _globalBindingNumbers = [];

    // The resolver of this arena, kept with its tables for the next resolution.
    internal Resolution.Resolver? _resolver;

    // Whether any scope context in the arena may be non-zero (set by the resolver, the ScopeContext setters and
    // factories given a context). While false, resolution can skip clearing old contexts: a fresh parse has none.
    internal bool _scopeContextsSet;

    private readonly string _source;

    /// <summary>Creates an empty arena (for building ASTs by hand).</summary>
    public JsAst() : this(string.Empty, 64, 64, 64)
    {
        _root = CreateProgram(ListRange.Empty);
    }

    internal JsAst(string source, int nodeCapacity, int listCapacity, int stringCapacity)
    {
        _source = source;
        _nodes = ArenaPool.RentNodes(Math.Max(nodeCapacity, 16));
        _nodes[0] = default; // id 0 is the none node
        _nodeCount = 1;
        _lists = ArenaPool.RentLists(Math.Max(listCapacity, 16));
        _listCount = 0;
        InitStrings(Math.Max(stringCapacity, KnownStrings.Count * 2));
    }

    /// <summary>The source text the AST was parsed from (empty for hand-built ASTs).</summary>
    public string Source => _source;

    /// <summary>The root <see cref="cAST.Program"/> node.</summary>
    public Program Program => new(this, _root);

    /// <summary>The syntax errors reported while parsing (empty if the parse succeeded).</summary>
    public IReadOnlyList<ParseError> Errors => (IReadOnlyList<ParseError>?)_errors ?? [];

    /// <summary>Whether any syntax error was reported while parsing.</summary>
    public bool HasErrors => _errors is { Count: > 0 };

    /// <summary>The number of node records allocated in this arena (including detached/garbage nodes).</summary>
    public int NodeCount => _nodeCount;

    /// <summary>Throws a <see cref="JsSyntaxException"/> if the parse reported errors.</summary>
    public JsAst ThrowIfErrors()
    {
        if (HasErrors) throw new JsSyntaxException(_errors!);
        return this;
    }

    /// <summary>Returns a view of the node with the given id.</summary>
    public Node GetNode(int id)
    {
        if ((uint)id >= (uint)_nodeCount) throw new ArgumentOutOfRangeException(nameof(id));
        return new Node(this, id);
    }

    // ---- node storage ------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref NodeRecord AllocRecord(NodeKind kind, out int id)
    {
        int n = _nodeCount;
        NodeRecord[] nodes = _nodes;
        if ((uint)n >= (uint)nodes.Length)
            nodes = GrowNodes();
        _nodeCount = n + 1;
        id = n;
        ref NodeRecord r = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(nodes), (nint)(uint)n);
        r = default;
        r.Kind = kind;
        return ref r;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NodeRecord[] GrowNodes()
    {
        var old = _nodes;
        var grown = ArenaPool.RentNodes(checked(Math.Max(old.Length * 2, 1024)));
        Array.Copy(old, grown, _nodeCount);
        ArenaPool.ReturnNodes(old);
        _nodes = grown;
        return grown;
    }

    /// <summary>Validates that <paramref name="id"/> (from <paramref name="ast"/>) belongs to this arena.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Own(JsAst? ast, int id)
    {
        if (id != 0 && !ReferenceEquals(ast, this))
            ThrowForeignNode();
        return id;
    }

    [DoesNotReturn]
    private static void ThrowForeignNode() =>
        throw new InvalidOperationException("The node belongs to a different JsAst. Use JsAst.Import to copy it into this arena.");

    /// <summary>Allocates a scope context never used before in this arena.</summary>
    internal ScopeContext NextScopeContext()
    {
        int context = _nextScopeContext;
        _nextScopeContext = checked(context + 1);
        return (ScopeContext)context;
    }

    /// <summary>
    /// How many binding numbers the resolver has given in this arena: every <see cref="Identifier.BindingNumber"/> is below
    /// this, so a table indexed by binding number needs this many entries.
    /// </summary>
    public int BindingNumbers => _bindingNumbers;

    /// <summary>Allocates a binding number never used before in this arena.</summary>
    internal int NextBindingNumber() => _bindingNumbers++;

    /// <summary>The binding number of the global named by string <paramref name="name"/>, the same in every run.</summary>
    internal int GlobalBindingNumber(int name)
    {
        if (name >= _globalBindingNumbers.Length)
            Array.Resize(ref _globalBindingNumbers, Math.Max(name + 1, _globalBindingNumbers.Length * 2));
        ref int number = ref _globalBindingNumbers[name];
        if (number == 0)
            number = NextBindingNumber() + 1;
        return number - 1;
    }

    internal static InvalidCastException BadCast(NodeKind actual, string target) =>
        new($"Cannot view a {actual} node as {target}.");

    internal static string DebugString(JsAst? ast, int id)
    {
        if (ast is null || id == 0) return "<none>";
        try
        {
            var text = Generation.JsGenerator.GenerateMinified(new Node(ast, id));
            return text.Length > 300 ? string.Concat(text.AsSpan(0, 300), "…") : text;
        }
        catch (Exception e)
        {
            return $"<{ast._nodes[id].Kind}: {e.GetType().Name}>";
        }
    }

    // ---- lists -------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int AllocList(int count)
    {
        int start = _listCount;
        int end = start + count;
        if ((uint)end > (uint)_lists.Length)
            GrowLists(end);
        _listCount = end;
        return start;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowLists(int required)
    {
        var old = _lists;
        var grown = ArenaPool.RentLists(Math.Max(required, Math.Max(old.Length * 2, 1024)));
        Array.Copy(old, grown, _listCount);
        ArenaPool.ReturnLists(old);
        _lists = grown;
    }

    internal ListRange AddList(ReadOnlySpan<int> ids)
    {
        if (ids.IsEmpty) return ListRange.Empty;
        int start = AllocList(ids.Length);
        ids.CopyTo(_lists.AsSpan(start, ids.Length));
        return new ListRange(start, ids.Length);
    }

    internal ListRange AddList<T>(ReadOnlySpan<T> items) where T : struct, INode<T>
    {
        if (items.IsEmpty) return ListRange.Empty;
        int start = AllocList(items.Length);
        var dst = _lists.AsSpan(start, items.Length);
        for (int i = 0; i < items.Length; i++)
            dst[i] = Own(items[i].Ast, items[i].Id);
        return new ListRange(start, items.Length);
    }

    internal ReadOnlySpan<int> ListSpan(int start, int count) => _lists.AsSpan(start, count);

    // ---- BigInts -----------------------------------------------------------------------------------

    internal int AddBigInt(BigInteger value)
    {
        _bigInts ??= new BigInteger[8];
        if (_bigIntCount == _bigInts.Length) Array.Resize(ref _bigInts, _bigInts.Length * 2);
        _bigInts[_bigIntCount++] = value;
        return _bigIntCount; // ids are 1-based; 0 means none
    }

    internal BigInteger GetBigInt(int id) => id == 0 ? BigInteger.Zero : _bigInts![id - 1];

    // ---- errors ------------------------------------------------------------------------------------

    internal void SetErrors(List<ParseError>? errors) => _errors = errors is { Count: > 0 } ? errors : null;

    /// <summary>
    /// Returns the 1-based line and column (in UTF-16 code units) of <paramref name="offset"/> in <see cref="Source"/>.
    /// LF, CRLF, CR, LS and PS are line breaks. Runs in O(offset).
    /// </summary>
    public (int Line, int Column) GetLineColumn(int offset) => LineColumn(_source, offset);

    /// <summary>
    /// Returns the 1-based line and column (in UTF-16 code units) of <paramref name="offset"/> in <paramref name="source"/>.
    /// </summary>
    public static (int Line, int Column) LineColumn(string source, int offset)
    {
        int end = Math.Clamp(offset, 0, source.Length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < end; i++)
        {
            switch (source[i])
            {
                case '\n':
                case (char)0x2028:
                case (char)0x2029:
                    line++;
                    lineStart = i + 1;
                    break;
                case '\r':
                    line++;
                    if (i + 1 < end && source[i + 1] == '\n') i++;
                    lineStart = i + 1;
                    break;
            }
        }
        return (line, end - lineStart + 1);
    }

    // ---- pooling -----------------------------------------------------------------------------------

    /// <summary>
    /// Returns the arena's large arrays to a pool so the next parse can reuse them without allocating.
    /// The AST (and every view into it) must not be used afterwards.
    /// </summary>
    public void Dispose()
    {
        var nodes = _nodes;
        var lists = _lists;
        var strings = _strings;
        var buckets = _buckets;
        int stringCount = _stringCount;
        if (nodes.Length == 0) return;
        _nodes = [];
        _lists = [];
        _strings = [];
        _buckets = [];
        _nodeCount = _listCount = _stringCount = 0;
        ArenaPool.ReturnNodes(nodes);
        ArenaPool.ReturnLists(lists);
        ArenaPool.ReturnStrings(strings, stringCount);
        ArenaPool.ReturnBuckets(buckets);
    }
}

/// <summary>A tiny per-thread pool for the arena's big arrays (they are usually on the large object heap).</summary>
internal static class ArenaPool
{
    [ThreadStatic] private static NodeRecord[]? t_nodes;
    [ThreadStatic] private static int[]? t_lists;
    [ThreadStatic] private static StringEntry[]? t_strings;
    [ThreadStatic] private static JsAst.Bucket[]? t_buckets;

    internal static NodeRecord[] RentNodes(int minLength)
    {
        var cached = t_nodes;
        if (cached is not null && cached.Length >= minLength)
        {
            t_nodes = null;
            return cached;
        }
        // NodeRecord contains no references, so the array can skip zeroing: every record is
        // fully written by AllocRecord before it is read.
        return GC.AllocateUninitializedArray<NodeRecord>(minLength);
    }

    internal static void ReturnNodes(NodeRecord[] array)
    {
        if (array.Length == 0) return;
        if (t_nodes is null || t_nodes.Length < array.Length) t_nodes = array;
    }

    internal static int[] RentLists(int minLength)
    {
        var cached = t_lists;
        if (cached is not null && cached.Length >= minLength)
        {
            t_lists = null;
            return cached;
        }
        return GC.AllocateUninitializedArray<int>(minLength);
    }

    internal static void ReturnLists(int[] array)
    {
        if (array.Length == 0) return;
        if (t_lists is null || t_lists.Length < array.Length) t_lists = array;
    }

    internal static StringEntry[] RentStrings(int minLength)
    {
        var cached = t_strings;
        if (cached is not null && cached.Length >= minLength)
        {
            t_strings = null;
            return cached;
        }
        return new StringEntry[minLength];
    }

    internal static void ReturnStrings(StringEntry[] array, int used)
    {
        if (array.Length == 0) return;
        // Drop string references so the pooled array does not keep them alive (entries past `used` are clear:
        // they were cleared when the array was last returned, or never written).
        Array.Clear(array, 0, Math.Min(used, array.Length));
        if (t_strings is null || t_strings.Length < array.Length) t_strings = array;
    }

    internal static JsAst.Bucket[] RentBuckets(int length)
    {
        var cached = t_buckets;
        if (cached is not null && cached.Length == length)
        {
            t_buckets = null;
            Array.Clear(cached);
            return cached;
        }
        return new JsAst.Bucket[length];
    }

    internal static void ReturnBuckets(JsAst.Bucket[] array)
    {
        if (array.Length == 0) return;
        if (t_buckets is null || t_buckets.Length < array.Length) t_buckets = array;
    }
}
