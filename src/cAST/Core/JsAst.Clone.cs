using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace cAST;

public sealed partial class JsAst
{
    /// <summary>
    /// Deep-copies <paramref name="node"/> (and its subtree) within this arena. Scope contexts are copied too, so a copy
    /// of code that declares variables shares their contexts with the original until the next
    /// <see cref="Resolution.JsResolver.Resolve(JsAst)"/>.
    /// </summary>
    public T Clone<T>(T node) where T : struct, INode<T>
    {
        if (node.IsNone) return node;
        int id = Own(node.Ast, node.Id);
        InitCloneStack();
        return T.Create(this, CopyTree(this, id));
    }

    /// <summary>
    /// Deep-copies <paramref name="node"/> from another arena into this one. Scope contexts are not copied (they are
    /// numbered per arena): the copy is unresolved until this arena is resolved again.
    /// </summary>
    public T Import<T>(T node) where T : struct, INode<T>
    {
        if (node.IsNone) return default;
        if (ReferenceEquals(node.Ast, this)) return Clone(node);
        InitCloneStack();
        return T.Create(this, CopyTree(node.Ast, node.Id));
    }

    /// <summary>Deep-copies <paramref name="node"/> from another arena into this one.</summary>
    public Node Import(Node node) => Import<Node>(node);

    /// <summary>Copies the whole arena (all nodes, lists and strings) into a new, independent arena.</summary>
    public JsAst Clone()
    {
        var copy = new JsAst(this);
        return copy;
    }

    private JsAst(JsAst other)
    {
        _source = other._source;
        _nodes = GC.AllocateUninitializedArray<NodeRecord>(Math.Max(other._nodeCount, 16));
        Array.Copy(other._nodes, _nodes, other._nodeCount);
        _nodeCount = other._nodeCount;
        _lists = GC.AllocateUninitializedArray<int>(Math.Max(other._listCount, 16));
        Array.Copy(other._lists, _lists, other._listCount);
        _listCount = other._listCount;
        _strings = new StringEntry[Math.Max(other._stringCount, 16)];
        Array.Copy(other._strings, _strings, other._stringCount);
        _stringCount = other._stringCount;
        _buckets = (Bucket[])other._buckets.Clone();
        _bucketMask = other._bucketMask;
        _internedCount = other._internedCount;
        if (other._bigInts is not null)
        {
            _bigInts = (BigInteger[])other._bigInts.Clone();
            _bigIntCount = other._bigIntCount;
        }
        _root = other._root;
        _errors = other._errors is null ? null : new List<ParseError>(other._errors);
        _nextScopeContext = other._nextScopeContext;
        _scopeContextsSet = other._scopeContextsSet;
        _listCapacity = other._listCapacity is null ? null : new Dictionary<int, int>(other._listCapacity);
    }

    /// <summary>Sets every scope context in the arena, attached or not, to <see cref="ScopeContext.Unresolved"/>.</summary>
    internal void ClearScopeContexts()
    {
        // Branch-free: node kinds come in no predictable order, so a branch per record would mostly mispredict.
        // A kind without a scope slot ANDs its slot A with -1 (unchanged); one with a slot ANDs that slot with 0.
        ReadOnlySpan<sbyte> scopeSlot = NodeLayout.ScopeSlot;
        ref NodeRecord first = ref MemoryMarshal.GetArrayDataReference(_nodes);
        for (int id = 1; id < _nodeCount; id++)
        {
            ref NodeRecord r = ref Unsafe.Add(ref first, id);
            int slot = scopeSlot[(int)r.Kind];
            int keep = slot >> 31;
            Unsafe.Add(ref r.A, slot & ~keep) &= keep;
        }
    }

    /// <summary>Sets every scope context in the subtree of node <paramref name="root"/> to <see cref="ScopeContext.Unresolved"/>.</summary>
    internal void ClearScopeContexts(int root)
    {
        var scopeSlot = NodeLayout.ScopeSlot;
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            ref NodeRecord r = ref _nodes[id];
            int slot = scopeSlot[(int)r.Kind];
            if (slot >= 0) Unsafe.Add(ref r.A, slot) = 0;
            byte[] slots = NodeLayout.Slots[(int)r.Kind];
            for (int i = 0; i < slots.Length; i++)
            {
                int value = Unsafe.Add(ref r.A, slots[i] & 15);
                switch (slots[i] >> 4)
                {
                    case 1:
                        if (value != 0) stack.Push(value);
                        break;
                    case 2:
                        {
                            int count = Unsafe.Add(ref r.A, (slots[i] & 15) + 1);
                            for (int j = 0; j < count; j++)
                                if (_lists[value + j] != 0) stack.Push(_lists[value + j]);
                            break;
                        }
                }
            }
        }
    }

    private nint _cloneStackLimit;

    private unsafe void InitCloneStack()
    {
        byte probe;
        _cloneStackLimit = (nint)(&probe) - 64 * 1024;
    }

    private unsafe bool CloneStackIsLow()
    {
        byte probe;
        if ((nint)(&probe) >= _cloneStackLimit) return false;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
        _cloneStackLimit = (nint)(&probe) - 64 * 1024;
        return false;
    }

    /// <summary>Copies the subtree rooted at <paramref name="id"/> of <paramref name="src"/> into this arena.</summary>
    private int CopyTree(JsAst src, int id)
    {
        if (id == 0) return 0;
        if (CloneStackIsLow()) return CopyTreeOnNewStack(src, id);
        return CopyTreeCore(src, id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CopyTreeOnNewStack(JsAst src, int id)
    {
        int result = 0;
        Exception? error = null;
        nint savedLimit = _cloneStackLimit;
        var thread = new Thread(() =>
        {
            InitCloneStack();
            try { result = CopyTreeCore(src, id); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        _cloneStackLimit = savedLimit;
        if (error is not null) throw new InvalidOperationException("Cloning failed on the overflow stack.", error);
        return result;
    }

    private int CopyTreeCore(JsAst src, int id)
    {
        NodeRecord copy = src._nodes[id];
        bool foreign = !ReferenceEquals(src, this);
        byte[] slots = NodeLayout.Slots[(int)copy.Kind];
        for (int s = 0; s < slots.Length; s++)
        {
            int kind = slots[s] >> 4, slot = slots[s] & 15;
            ref int value = ref Unsafe.Add(ref copy.A, slot);
            switch (kind)
            {
                case 1: // child
                    value = CopyTree(src, value);
                    break;
                case 2: // list
                    {
                        int start = value, count = Unsafe.Add(ref copy.A, slot + 1);
                        if (count == 0) break;
                        int newStart = AllocList(count);
                        for (int i = 0; i < count; i++)
                        {
                            int child = CopyTree(src, src._lists[start + i]);
                            _lists[newStart + i] = child;
                        }
                        value = newStart;
                        break;
                    }
                case 3: // string id
                    if (foreign && value != 0) value = Intern(src.GetStringSpan(value));
                    break;
                case 4: // BigInt id
                    if (foreign && value != 0) value = AddBigInt(src.GetBigInt(value));
                    break;
                case 5: // lazy string: source slices do not survive the move to another source
                    if (foreign && (value != 0 || src.HasLazyString(id, slot)))
                    {
                        var text = src.GetLazySpan(id, slot);
                        value = text.IsEmpty ? KnownStrings.Empty : Intern(text);
                    }
                    break;
                case 6: // scope context: numbers from another arena mean nothing here (resolve this arena again)
                    if (foreign) value = 0;
                    break;
            }
        }
        ref NodeRecord r = ref AllocRecord(copy.Kind, out int newId);
        r = copy;
        return newId;
    }

    /// <summary>Interns <paramref name="value"/>, allocating a string only if it is new.</summary>
    public int Intern(ReadOnlySpan<char> value)
    {
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
                id = AddEntry(value.ToString(), 0, value.Length);
                b.Hash = hash;
                b.Id = id;
                if (++_internedCount * 2 > buckets.Length) GrowBuckets();
                return id;
            }
            if (b.Hash == hash && EntryEquals(id, value)) return id;
            i = (i + 1) & mask;
        }
    }
}
