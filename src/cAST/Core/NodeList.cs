using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace cAST;

/// <summary>Implemented by every node view: a typed (arena, id) reference to a node.</summary>
/// <typeparam name="TSelf">The view type.</typeparam>
public interface INode<TSelf> where TSelf : struct, INode<TSelf>
{
    /// <summary>The arena that owns the node.</summary>
    JsAst Ast { get; }

    /// <summary>The node id; 0 means none.</summary>
    int Id { get; }

    /// <summary>The concrete kind of the node.</summary>
    NodeKind Kind { get; }

    /// <summary>Whether this is an absent node.</summary>
    bool IsNone { get; }

    /// <summary>Creates a view of node <paramref name="id"/> in <paramref name="ast"/>.</summary>
    static abstract TSelf Create(JsAst ast, int id);
}

/// <summary>
/// A live view of a list field of a node (e.g. <see cref="BlockStatement.List"/>). Reads always reflect the
/// current contents; mutations (<see cref="Add"/>, <see cref="Insert"/>, <see cref="RemoveAt"/>, ...) update
/// the owning node in place.
/// </summary>
[DebuggerDisplay("Count = {Count}")]
public readonly struct NodeList<T> : IReadOnlyList<T> where T : struct, INode<T>
{
    internal readonly JsAst _ast;
    internal readonly int _owner;
    internal readonly int _slot;

    internal NodeList(JsAst ast, int owner, int slot)
    {
        _ast = ast;
        _owner = owner;
        _slot = slot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int StartSlot() => ref Unsafe.Add(ref _ast._nodes[_owner].A, _slot);

    /// <summary>The number of elements.</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _owner == 0 ? 0 : Unsafe.Add(ref StartSlot(), 1);
    }

    /// <summary>Whether the list has no elements.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Gets or sets the element at <paramref name="index"/>.</summary>
    public T this[int index]
    {
        get
        {
            ref int start = ref StartSlot();
            if ((uint)index >= (uint)Unsafe.Add(ref start, 1)) ThrowIndex();
            return T.Create(_ast, _ast._lists[start + index]);
        }
        set
        {
            ref int start = ref StartSlot();
            if ((uint)index >= (uint)Unsafe.Add(ref start, 1)) ThrowIndex();
            _ast._lists[start + index] = _ast.Own(value.Ast, value.Id);
        }
    }

    /// <summary>The first element.</summary>
    public T First => this[0];

    /// <summary>The last element.</summary>
    public T Last => this[Count - 1];

    /// <summary>The element ids (valid until the next mutation of any list in the arena).</summary>
    public ReadOnlySpan<int> Ids
    {
        get
        {
            if (_owner == 0) return default;
            ref int start = ref StartSlot();
            return _ast._lists.AsSpan(start, Unsafe.Add(ref start, 1));
        }
    }

    /// <summary>Appends an element.</summary>
    public void Add(T item) => Insert(Count, item);

    /// <summary>Inserts an element at <paramref name="index"/>.</summary>
    public void Insert(int index, T item)
    {
        EnsureOwner();
        int id = _ast.Own(item.Ast, item.Id);
        _ast.ListInsert(_owner, _slot, index, id);
    }

    /// <summary>Removes the element at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        EnsureOwner();
        _ast.ListRemoveAt(_owner, _slot, index);
    }

    /// <summary>Removes all elements.</summary>
    public void Clear()
    {
        if (_owner == 0) return;
        ref int start = ref StartSlot();
        start = 0;
        Unsafe.Add(ref start, 1) = 0;
    }

    /// <summary>Replaces the whole list with <paramref name="items"/>.</summary>
    public void SetAll(ReadOnlySpan<T> items)
    {
        EnsureOwner();
        var range = _ast.AddList(items);
        ref int start = ref StartSlot();
        start = range.Start;
        Unsafe.Add(ref start, 1) = range.Count;
    }

    /// <summary>Returns the index of <paramref name="item"/>, or -1.</summary>
    public int IndexOf(T item)
    {
        if (!ReferenceEquals(item.Ast, _ast) || item.Id == 0) return -1;
        return Ids.IndexOf(item.Id);
    }

    /// <summary>Copies the elements into a new array.</summary>
    public T[] ToArray()
    {
        var ids = Ids;
        var result = new T[ids.Length];
        for (int i = 0; i < ids.Length; i++) result[i] = T.Create(_ast, ids[i]);
        return result;
    }

    private void EnsureOwner()
    {
        if (_owner == 0) throw new InvalidOperationException("The list does not belong to a node.");
    }

    private static void ThrowIndex() => throw new ArgumentOutOfRangeException("index");

    /// <summary>Returns an enumerator over the elements (snapshot of the current range).</summary>
    public Enumerator GetEnumerator() => new(_ast, _owner == 0 ? 0 : StartSlot(), Count);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>A struct enumerator over a <see cref="NodeList{T}"/>.</summary>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly JsAst _ast;
        private readonly int _start;
        private readonly int _count;
        private int _index;

        internal Enumerator(JsAst ast, int start, int count)
        {
            _ast = ast;
            _start = start;
            _count = count;
            _index = -1;
        }

        /// <inheritdoc/>
        public readonly T Current => T.Create(_ast, _ast._lists[_start + _index]);

        readonly object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext() => ++_index < _count;

        /// <inheritdoc/>
        public void Reset() => _index = -1;

        /// <inheritdoc/>
        public readonly void Dispose() { }
    }
}

public sealed partial class JsAst
{
    // Capacity of lists that Insert/Add moved to the end of the pool, by start offset. A moved list reserves room to
    // grow (doubling), so adding to several lists in turn is amortized instead of copying a list on every add. Pool
    // offsets are never reused, so an entry can only describe the list that was moved there.
    private Dictionary<int, int>? _listCapacity;

    internal void ListInsert(int owner, int slot, int index, int id)
    {
        ref int startRef = ref Unsafe.Add(ref _nodes[owner].A, slot);
        int start = startRef, count = Unsafe.Add(ref startRef, 1);
        if ((uint)index > (uint)count) throw new ArgumentOutOfRangeException(nameof(index));

        bool inPlace = false;
        if (count > 0)
        {
            if (start + count == _listCount && start + count < _lists.Length)
            {
                AllocList(1); // the list is the last one in the pool: grow it in place
                inPlace = true;
            }
            else if (_listCapacity is not null && _listCapacity.TryGetValue(start, out int capacity) && count < capacity)
            {
                inPlace = true; // room reserved when the list was moved
            }
        }

        if (inPlace)
        {
            var span = _lists.AsSpan(start, count + 1);
            span[index..count].CopyTo(span[(index + 1)..]);
            span[index] = id;
        }
        else
        {
            int capacity = Math.Max(4, (count + 1) * 2);
            int newStart = AllocList(capacity); // may reallocate _lists; startRef is into _nodes (unaffected)
            var src = _lists.AsSpan(start, count);
            var dst = _lists.AsSpan(newStart, count + 1);
            src[..index].CopyTo(dst);
            dst[index] = id;
            src[index..].CopyTo(dst[(index + 1)..]);
            _listCapacity ??= new Dictionary<int, int>();
            if (count > 0) _listCapacity.Remove(start);
            _listCapacity[newStart] = capacity;
            start = newStart;
        }
        ref NodeRecord r = ref _nodes[owner];
        Unsafe.Add(ref r.A, slot) = start;
        Unsafe.Add(ref r.A, slot + 1) = count + 1;
    }

    internal void ListRemoveAt(int owner, int slot, int index)
    {
        ref int startRef = ref Unsafe.Add(ref _nodes[owner].A, slot);
        int start = startRef, count = Unsafe.Add(ref startRef, 1);
        if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        var span = _lists.AsSpan(start, count);
        span[(index + 1)..].CopyTo(span[index..]);
        Unsafe.Add(ref startRef, 1) = count - 1;
    }
}
