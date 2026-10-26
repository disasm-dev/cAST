using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace cAST;

/// <summary>
/// Base class for AST transforms, shaped like go-fAST's visitors: override the hooks for the node kinds you care
/// about, and call the base implementation to transform the children. Every hook receives its node by
/// <see langword="ref"/>: assigning <c>node = replacement</c> puts the replacement in the node's slot in its parent
/// (go-fAST's <c>*n = replacement</c>) when the hook returns. Replace a node where its type allows the replacement: an
/// identifier can become a call in <see cref="VisitExpression"/>, but <see cref="VisitIdentifier"/> can only put
/// another identifier in its place.
/// </summary>
/// <remarks>
/// <para>
/// Node views are values, so the walker keeps no pointer into the arena while a hook runs: allocating nodes in a hook
/// is safe, and the replacement is written when the hook returns (code in the same hook that reads the parent still
/// sees the old child until then). A replaced node stays intact; it is simply no longer in the tree.
/// </para>
/// <para>
/// A list element can be removed, and nodes inserted before or after it, with <see cref="Remove{T}"/>,
/// <see cref="InsertBefore{TNode, T}"/> and <see cref="InsertAfter{TNode, T}"/>. These edits are applied, in one pass,
/// when the walk of that list finishes; inserted nodes are not visited by the current walk. Changing a list with
/// <see cref="NodeList{T}"/> methods while the transformer walks that same list and has edits pending for it is an
/// error. <see cref="SlotOf{T}"/> returns a handle to a node's slot that stays usable after the walk. These methods take
/// the hook's <c>ref node</c>: a node the walk did not pass to the running hook (one walked with <see cref="Visit{T}"/>,
/// or by calling a hook yourself) has no slot, and they throw for it.
/// </para>
/// <para>
/// Optional chains are stored as an <see cref="OptionalChain"/> above an <see cref="Optional"/> that marks where the
/// <c>?.</c> is: replacing the Optional, or a node between it and its OptionalChain, removes the <c>?.</c>.
/// </para>
/// <para>A transformer instance walks one tree at a time and is not thread-safe.</para>
/// </remarks>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
public abstract partial class AstTransformer
{
    private static readonly ConcurrentDictionary<Type, bool[]> s_overrides = new();

    // Indexed by hook id (see Hooks): whether this transformer's type overrides that Visit* method.
    private readonly HookFlags _ov;

    // Bit k set: kind k has no children and its Visit* method is not overridden, so reaching it does nothing.
    private readonly KindMask _skip;

    [InlineArray(2)]
    private struct KindMask
    {
        private ulong _element0;
    }

    /// <summary>Initializes the transformer (the set of overridden methods is computed once per type).</summary>
    protected AstTransformer()
    {
        Type type = GetType();
        if (!s_overrides.TryGetValue(type, out var ov))
        {
            ov = ComputeOverrides(type);
            s_overrides.TryAdd(type, ov);
        }
        ov.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<HookFlags, bool>(ref _ov), HookCount));
        for (int kind = 1; kind < NodeKinds.Count; kind++)
        {
            if (!ov[kind] && IsLeafKind(kind))
                _skip[kind >> 6] |= 1UL << (kind & 63);
        }
    }

    private static bool IsLeafKind(int kind)
    {
        foreach (byte slot in NodeLayout.Slots[kind])
            if ((slot >> 4) is 1 or 2) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Ov(int hook) => Unsafe.Add(ref Unsafe.As<HookFlags, bool>(ref Unsafe.AsRef(in _ov)), hook);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Skip(NodeKind kind)
    {
        uint k = (uint)kind;
        return ((Unsafe.Add(ref Unsafe.As<KindMask, ulong>(ref Unsafe.AsRef(in _skip)), (nint)(k >> 6)) >> (int)(k & 63)) & 1) != 0;
    }

    private static bool[] ComputeOverrides([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
    {
        var ov = new bool[HookCount];
        foreach (var (hook, method, parameter) in Hooks)
            ov[hook] = IsOverridden(type, method, parameter);
        return ov;
    }

    private static bool IsOverridden([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, string name, Type parameter)
    {
        try
        {
            // Anything other than AstTransformer's own declaration counts as overridden (a method hidden with `new`
            // included): a false positive only costs a virtual call that lands in the default implementation.
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [parameter]);
            return method is null || method.DeclaringType != typeof(AstTransformer);
        }
        catch (AmbiguousMatchException)
        {
            return true;
        }
    }

    // ---- entry point ---------------------------------------------------------------------------------

    // The arena of the walk started by Transform (hooks called directly by user code run without one).
    private JsAst? _ast;

    /// <summary>Transforms the whole program of <paramref name="ast"/>.</summary>
    public void Transform(JsAst ast)
    {
        ArgumentNullException.ThrowIfNull(ast);
        if (_ast is not null) throw new InvalidOperationException("This transformer is already transforming a tree.");
        _ast = ast;
        try
        {
            var program = ast.Program;
            VisitProgram(ref program);
            if (program._id != ast._root)
            {
                if (program._id == 0) throw new InvalidOperationException("The program cannot be replaced by none.");
                ast._root = ast.Own(program._ast, program._id);
            }
        }
        finally
        {
            _ast = null;
            _slot = default;
            _frameCount = 0;
            _listDepth = 0;
            _edits.Clear();
        }
    }

    /// <summary>
    /// Transforms the subtree <paramref name="node"/> on its own, whether it is part of the program or a detached copy
    /// (<see cref="JsAst.Clone{T}(T)"/>), and returns what the hooks left in its place. The node itself is walked without
    /// a slot, as with <see cref="Visit{T}"/>.
    /// </summary>
    public T Transform<T>(T node) where T : struct, INode<T>
    {
        if (node.IsNone) return node;
        if (_ast is not null) throw new InvalidOperationException("This transformer is already transforming a tree.");
        var ast = node.Ast;
        _ast = ast;
        try
        {
            return T.Create(ast, WalkAs<T>(ast, node.Id));
        }
        finally
        {
            _ast = null;
            _slot = default;
            _frameCount = 0;
            _listDepth = 0;
            _edits.Clear();
        }
    }

    /// <summary>The arena being transformed, for creating replacement nodes (<c>Ast.NewIdentifier("x")</c>).</summary>
    /// <exception cref="InvalidOperationException">No <see cref="Transform"/> is in progress.</exception>
    protected JsAst Ast => _ast ?? throw new InvalidOperationException("No tree is being transformed (hooks called directly have no Ast).");

    /// <summary>
    /// Transforms <paramref name="node"/> and returns what the hooks left in its place, for walking a child yourself
    /// (go-fAST's <c>n.Right.VisitWith(v)</c>): <c>node.Right = Visit(node.Right);</c>. The node is walked without a slot,
    /// so <see cref="Remove{T}"/>, <see cref="InsertBefore{TNode, T}"/>, <see cref="InsertAfter{TNode, T}"/> and
    /// <see cref="SlotOf{T}"/> throw for it; its descendants keep theirs.
    /// </summary>
    /// <exception cref="InvalidOperationException">No <see cref="Transform"/> is in progress, or the node belongs to another arena.</exception>
    protected T Visit<T>(T node) where T : struct, INode<T>
    {
        var ast = Ast;
        if (node.IsNone) return node;
        CheckArena(node.Ast);
        return T.Create(ast, WalkAs<T>(ast, node.Id));
    }

    /// <summary>Checks that a node taken from a hook belongs to the tree being transformed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JsAst CheckArena(JsAst ast)
    {
        if (_ast is not null && !ReferenceEquals(ast, _ast)) ThrowForeign();
        return ast;
    }

    [DoesNotReturn]
    private static void ThrowForeign() =>
        throw new InvalidOperationException("The node belongs to a different JsAst than the one being transformed. Use JsAst.Import to copy it.");

    // ---- the current slot ----------------------------------------------------------------------------

    /// <summary>
    /// Where the node of the running hook sits, in the tree being transformed: owner id and slot code (0 owner = no
    /// slot), and the address of the local the walk passed to the hook by ref, which identifies that node's hook. No
    /// object references, so saving and restoring it around every hook costs no GC write barrier.
    /// </summary>
    private struct SlotState
    {
        public int Owner;
        public int Code; // field: slot index; list element: (index << 4) | 8 | listSlot
        public nint Node;
        public bool Remove;
    }

    private SlotState _slot;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe SlotState Enter<T>(JsAst ast, int owner, int code, ref T node) where T : struct
    {
        var saved = _slot;
        // A slot only means something in the tree Transform is walking (hooks called directly have none).
        _slot.Owner = ReferenceEquals(ast, _ast) ? owner : 0;
        _slot.Code = code;
        _slot.Node = (nint)Unsafe.AsPointer(ref node);
        _slot.Remove = false;
        return saved;
    }

    /// <summary>The slot of the running hook's node, when <paramref name="node"/> is the ref the walk passed to that hook.</summary>
    private unsafe SlotState SlotFor<T>(ref T node) where T : struct
    {
        var slot = _slot;
        if (slot.Owner == 0 || (nint)Unsafe.AsPointer(ref node) != slot.Node) ThrowNoSlot();
        return slot;
    }

    /// <summary>The slot owner a default union hook walks <paramref name="node"/> with: the hook's own, or none when called by hand.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe int OwnerFor<T>(ref T node) where T : struct => (nint)Unsafe.AsPointer(ref node) == _slot.Node ? _slot.Owner : 0;

    /// <summary>The id to leave in the slot after a hook: 0 if the node was removed, else the (checked) replacement.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Result(JsAst ast, int id, JsAst? newAst, int newId)
    {
        if (_slot.Remove) return 0;
        if (newId == id && (newId == 0 || ReferenceEquals(newAst, ast))) return id;
        return ast.Own(newAst, newId);
    }

    /// <summary>Stores <paramref name="value"/> (the result of visiting child <paramref name="old"/>) into a field.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StoreField(JsAst ast, int owner, int slot, int old, int value, bool noneAllowed)
    {
        ref int field = ref Unsafe.Add(ref ast._nodes[owner].A, slot);
        if (field != old)
            throw new InvalidOperationException(
                $"The {ast._nodes[owner].Kind} field in slot {"ABCDEFG"[slot]} was changed while its old node was being transformed, and the hook also replaced that node.");
        if (value == 0 && !noneAllowed) ThrowRequired(ast, owner, slot);
        field = value;
    }

    /// <summary>After an element's hook: checks the list still holds it at <paramref name="index"/> and stores a replacement.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreElement(JsAst ast, int owner, int slot, int index, int old, int value)
    {
        ref int range = ref Unsafe.Add(ref ast._nodes[owner].A, slot);
        if ((uint)index >= (uint)Unsafe.Add(ref range, 1)) ThrowElementGone(ast, owner, index);
        ref int element = ref ast._lists[range + index];
        if (element != old)
        {
            // Replaced directly (list[i] = x) by the hook: keep that, unless the hook also assigned its ref.
            if (value != old) ThrowElementGone(ast, owner, index);
            return;
        }
        if (value == old) return;
        if (value == 0 && (ListType(ast, owner, slot) & NodeLayout.NoneAllowed) == 0) ThrowRequired(ast, owner, slot);
        element = value;
    }

    [DoesNotReturn]
    private static void ThrowElementGone(JsAst ast, int owner, int index) =>
        throw new InvalidOperationException(
            $"Element {index} of a {ast._nodes[owner].Kind} list changed while it was being transformed. Use Remove, InsertBefore and InsertAfter to change the list being walked.");

    [DoesNotReturn]
    private static void ThrowRequired(JsAst ast, int owner, int slot) =>
        throw new InvalidOperationException(
            $"A {ast._nodes[owner].Kind} cannot hold none in slot {"ABCDEFG"[slot]} ({NodeLayout.TypeName(SlotType(ast, owner, slot))} is required). Replace the node, or call Remove(ref node) on a list element.");

    /// <summary>The declared type code of payload slot <paramref name="slot"/> of node <paramref name="owner"/>.</summary>
    private static int SlotType(JsAst ast, int owner, int slot)
    {
        int kind = (int)ast._nodes[owner].Kind;
        byte[] slots = NodeLayout.Slots[kind];
        for (int i = 0; i < slots.Length; i++)
            if ((slots[i] & 15) == slot && (slots[i] >> 4) is 1 or 2) return NodeLayout.Types[kind][i];
        throw new InvalidOperationException($"{(NodeKind)kind} has no child or list in slot {slot}.");
    }

    private static int ListType(JsAst ast, int owner, int slot) => SlotType(ast, owner, slot);

    /// <summary>
    /// A handle to the slot of <paramref name="node"/> (a field of its parent or an element of a list), bound to the node
    /// <paramref name="node"/> holds now. It stays usable after the walk: see <see cref="NodeSlot"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="node"/> is not the ref the walk passed to the running hook.</exception>
    protected NodeSlot SlotOf<T>(ref T node) where T : struct, INode<T>
    {
        var slot = SlotFor(ref node);
        return NodeSlot.At(_ast!, slot.Owner, slot.Code, _ast!.Own(node.Ast, node.Id));
    }

    [DoesNotReturn]
    private static void ThrowNoSlot() =>
        throw new InvalidOperationException(
            "The node has no slot here: Remove, InsertBefore, InsertAfter and SlotOf need the ref the walk of Transform passed to the running hook (a node walked with Visit, or by calling a hook yourself, has none).");

    /// <summary>
    /// Removes <paramref name="node"/>: a list element is removed from its list when the walk of the list finishes; an
    /// optional field is set to none when the hook returns.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The node is in a required field (replace it instead), or <paramref name="node"/> is not the ref the walk passed to the running hook.
    /// </exception>
    protected void Remove<T>(ref T node) where T : struct, INode<T>
    {
        var slot = SlotFor(ref node);
        var ast = _ast!;
        if ((slot.Code & 8) != 0)
        {
            QueueEdit(ast, slot, EditKind.Remove, 0);
            return;
        }
        if ((SlotType(ast, slot.Owner, slot.Code) & NodeLayout.NoneAllowed) == 0)
            throw new InvalidOperationException(
                $"The {NodeLayout.TypeName(SlotType(ast, slot.Owner, slot.Code))} of a {ast._nodes[slot.Owner].Kind} is required: replace it instead of removing it.");
        _slot.Remove = true;
    }

    /// <summary>Inserts <paramref name="inserted"/> before the list element <paramref name="node"/> (applied when the list's walk finishes; not visited).</summary>
    /// <exception cref="InvalidOperationException"><paramref name="node"/> is not a list element, or not the ref the walk passed to the running hook.</exception>
    protected void InsertBefore<TNode, T>(ref TNode node, T inserted) where TNode : struct, INode<TNode> where T : struct, INode<T>
        => Insert(SlotFor(ref node), EditKind.Before, inserted.Ast, inserted.Id);

    /// <summary>Inserts <paramref name="inserted"/> after the list element <paramref name="node"/> (applied when the list's walk finishes; not visited).</summary>
    /// <exception cref="InvalidOperationException"><paramref name="node"/> is not a list element, or not the ref the walk passed to the running hook.</exception>
    protected void InsertAfter<TNode, T>(ref TNode node, T inserted) where TNode : struct, INode<TNode> where T : struct, INode<T>
        => Insert(SlotFor(ref node), EditKind.After, inserted.Ast, inserted.Id);

    private void Insert(SlotState slot, EditKind kind, JsAst? nodeAst, int id)
    {
        if ((slot.Code & 8) == 0) throw new InvalidOperationException("Only a list element can have nodes inserted before or after it.");
        var ast = _ast!;
        ast.Own(nodeAst, id);
        int type = ListType(ast, slot.Owner, slot.Code & 7);
        if (id == 0 ? (type & NodeLayout.NoneAllowed) == 0 : !NodeLayout.Accepts(type, ast._nodes[id].Kind))
            throw new ArgumentException($"{(id == 0 ? "None" : "A " + ast._nodes[id].Kind)} cannot be an element of a {NodeLayout.TypeName(type)} list.", nameof(id));
        QueueEdit(ast, slot, kind, id);
    }

    // ---- list walks and their pending edits ----------------------------------------------------------

    private enum EditKind : byte { Remove, Before, After }

    private readonly record struct Edit(int Index, EditKind Kind, int Node);

    // A frame holds the pending edits of one list walk. It is created by the first edit (most list walks have none),
    // with the list's range at that moment: a NodeList change to the list after that invalidates the queued indexes.
    private struct Frame
    {
        public int Depth, Owner, Slot, Start, Count, EditBase;
    }

    private Frame[] _frames = new Frame[8];
    private int _frameCount;
    private int _listDepth; // list walks in progress; the innermost is the list of any element whose hook runs
    private readonly List<Edit> _edits = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnterList() => _listDepth++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LeaveList(JsAst ast)
    {
        if (_frameCount != 0 && _frames[_frameCount - 1].Depth == _listDepth) ApplyEdits(ast, _frames[--_frameCount]);
        _listDepth--;
    }

    private void QueueEdit(JsAst ast, SlotState slot, EditKind kind, int node)
    {
        int listSlot = slot.Code & 7, index = slot.Code >> 4;
        if (_frameCount == 0 || _frames[_frameCount - 1].Depth != _listDepth)
        {
            if (_frameCount == _frames.Length) Array.Resize(ref _frames, _frames.Length * 2);
            ref Frame frame = ref _frames[_frameCount++];
            ref int range = ref Unsafe.Add(ref ast._nodes[slot.Owner].A, listSlot);
            frame.Depth = _listDepth;
            frame.Owner = slot.Owner;
            frame.Slot = listSlot;
            frame.Start = range;
            frame.Count = Unsafe.Add(ref range, 1);
            frame.EditBase = _edits.Count;
        }
        ref Frame top = ref _frames[_frameCount - 1];
        if (top.Owner != slot.Owner || top.Slot != listSlot)
            throw new InvalidOperationException("The current node's list is not the list being walked.");
        if (kind == EditKind.Remove && _edits.Count > top.EditBase && _edits[^1] is { Kind: EditKind.Remove } last && last.Index == index)
            return; // removed already
        _edits.Add(new Edit(index, kind, node));
    }

    /// <summary>Rebuilds a list with its pending removals and insertions, in one pass.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ApplyEdits(JsAst ast, Frame frame)
    {
        int first = frame.EditBase, end = _edits.Count;
        try
        {
            ref int range = ref Unsafe.Add(ref ast._nodes[frame.Owner].A, frame.Slot);
            int start = range, count = Unsafe.Add(ref range, 1);
            if (start != frame.Start || count != frame.Count)
                throw new InvalidOperationException(
                    $"A {ast._nodes[frame.Owner].Kind} list was changed through NodeList while Remove/InsertBefore/InsertAfter edits to it were pending.");

            // Edits are queued in walk order, so their indexes rise; keep them in call order within an index.
            SortByIndex(first, end);
            int inserted = 0;
            for (int k = first; k < end; k++)
                if (_edits[k].Kind != EditKind.Remove) inserted++;

            int[] buffer = ArrayPool<int>.Shared.Rent(count + inserted);
            try
            {
                int w = 0, k = first;
                for (int i = 0; i < count; i++)
                {
                    int from = k;
                    bool removed = false;
                    while (k < end && _edits[k].Index == i)
                    {
                        var edit = _edits[k++];
                        if (edit.Kind == EditKind.Before) buffer[w++] = edit.Node;
                        else if (edit.Kind == EditKind.Remove) removed = true;
                    }
                    if (!removed) buffer[w++] = ast._lists[start + i];
                    for (int j = from; j < k; j++)
                        if (_edits[j].Kind == EditKind.After) buffer[w++] = _edits[j].Node;
                }

                if (w > count)
                {
                    start = ast.AllocList(w); // may move _lists; range points into _nodes
                    range = ref Unsafe.Add(ref ast._nodes[frame.Owner].A, frame.Slot);
                    range = start;
                }
                buffer.AsSpan(0, w).CopyTo(ast._lists.AsSpan(start, w));
                Unsafe.Add(ref range, 1) = w;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buffer);
            }
        }
        finally
        {
            _edits.RemoveRange(first, end - first);
        }
    }

    private void SortByIndex(int first, int end)
    {
        // Insertion sort, stable: the slice is almost always sorted already.
        for (int i = first + 1; i < end; i++)
        {
            var item = _edits[i];
            int j = i - 1;
            while (j >= first && _edits[j].Index > item.Index)
            {
                _edits[j + 1] = _edits[j];
                j--;
            }
            _edits[j + 1] = item;
        }
    }

    // ---- deep trees ----------------------------------------------------------------------------------

    // As in AstVisitor: when the stack runs low, the rest of the subtree is transformed on a fresh thread.
    private const nuint StackWindow = 256 * 1024;
    private const int StackSlack = 64 * 1024;
    private nint _stackLimit;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe bool StackIsLow()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
        byte probe;
        _stackLimit = (nint)(&probe) - StackSlack;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WalkOnNewStack(JsAst ast, int id, int owner, int code)
    {
        int result = 0;
        Exception? error = null;
        nint savedLimit = _stackLimit;
        var thread = new Thread(() =>
        {
            try { result = Walk(this, ast, id, owner, code); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        _stackLimit = savedLimit;
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
