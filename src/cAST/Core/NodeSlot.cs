using System.Runtime.CompilerServices;

namespace cAST;

/// <summary>
/// A handle to the place a node occupies in its parent: a field, or an element of a list. Obtained from
/// <see cref="AstTransformer.SlotOf{T}(ref T)"/> during a walk and usable after it, for edits decided later (for example
/// after an analysis pass). The handle stays bound to the node it was taken for: if a list element moved, it is found
/// again; once the node is no longer in that slot (replaced or removed), the handle is spent and editing through it
/// throws.
/// </summary>
public readonly struct NodeSlot : IEquatable<NodeSlot>
{
    private readonly JsAst? _ast;
    private readonly int _owner;
    private readonly int _code;     // field: slot index; list element: (index << 4) | 8 | listSlot
    private readonly int _expected; // the node the handle was taken for

    private NodeSlot(JsAst ast, int owner, int code, int expected)
    {
        _ast = ast;
        _owner = owner;
        _code = code;
        _expected = expected;
    }

    internal static NodeSlot At(JsAst ast, int owner, int code, int expected) => new(ast, owner, code, expected);

    /// <summary>The arena of the slot.</summary>
    public JsAst Ast => _ast ?? throw new InvalidOperationException("This is a default NodeSlot.");

    /// <summary>The node whose field or list holds the slot.</summary>
    public Node Owner => new(Ast, _owner);

    /// <summary>Whether the slot is an element of a list (else a field).</summary>
    public bool IsListElement => (_code & 8) != 0;

    /// <summary>Whether the slot still holds the node the handle was taken for.</summary>
    public bool IsValid => _ast is not null && TryFind(out _);

    /// <summary>The node the handle was taken for.</summary>
    public Node Node => new(Ast, _expected);

    /// <summary>For a list element: its current index (the element is found again if the list changed).</summary>
    /// <exception cref="InvalidOperationException">The slot is a field, or the node is no longer in the list.</exception>
    public int Index
    {
        get
        {
            if (!IsListElement) throw new InvalidOperationException("The slot is a field, not a list element.");
            return Find();
        }
    }

    /// <summary>Puts <paramref name="replacement"/> where the node is, checking that the slot's declared type accepts it.</summary>
    /// <exception cref="InvalidOperationException">The node is no longer in the slot, or none would go into a required slot.</exception>
    /// <exception cref="ArgumentException">The slot's type does not accept the replacement's kind.</exception>
    public void Replace(Node replacement)
    {
        var ast = Ast;
        int id = ast.Own(replacement.Ast, replacement.Id);
        int type = SlotType(ast);
        if (id == 0)
        {
            if ((type & NodeLayout.NoneAllowed) == 0)
                throw new InvalidOperationException($"This slot requires a {NodeLayout.TypeName(type)}; use Remove() for a list element.");
        }
        else if (!NodeLayout.Accepts(type, ast._nodes[id].Kind))
        {
            throw new ArgumentException($"A {ast._nodes[id].Kind} cannot go where a {NodeLayout.TypeName(type)} is expected.", nameof(replacement));
        }
        int index = Find();
        ref int field = ref Unsafe.Add(ref ast._nodes[_owner].A, _code & 7);
        if (IsListElement) ast._lists[field + index] = id;
        else field = id;
    }

    /// <summary>Removes the node: a list element is removed from its list, an optional field is set to none.</summary>
    /// <exception cref="InvalidOperationException">The node is no longer in the slot, or the field is required.</exception>
    public void Remove()
    {
        var ast = Ast;
        int index = Find();
        if (IsListElement)
        {
            ast.ListRemoveAt(_owner, _code & 7, index);
            return;
        }
        if ((SlotType(ast) & NodeLayout.NoneAllowed) == 0)
            throw new InvalidOperationException($"This field requires a {NodeLayout.TypeName(SlotType(ast))}: replace the node instead.");
        Unsafe.Add(ref ast._nodes[_owner].A, _code & 7) = 0;
    }

    private int SlotType(JsAst ast)
    {
        int kind = (int)ast._nodes[_owner].Kind;
        byte[] slots = NodeLayout.Slots[kind];
        for (int i = 0; i < slots.Length; i++)
            if ((slots[i] & 15) == (_code & 7) && (slots[i] >> 4) is 1 or 2) return NodeLayout.Types[kind][i];
        throw new InvalidOperationException("The owner no longer has this slot.");
    }

    /// <summary>The current index of the node (list element) or 0 (field); throws if the node is not there any more.</summary>
    private int Find()
    {
        if (TryFind(out int index)) return index;
        throw new InvalidOperationException("The slot no longer holds the node the handle was taken for.");
    }

    private bool TryFind(out int index)
    {
        var ast = _ast!;
        index = 0;
        if ((uint)_owner >= (uint)ast._nodeCount) return false;
        ref int field = ref Unsafe.Add(ref ast._nodes[_owner].A, _code & 7);
        if (!IsListElement) return field == _expected;
        int start = field, count = Unsafe.Add(ref field, 1);
        index = _code >> 4;
        if (index < count && ast._lists[start + index] == _expected) return true;
        if (_expected == 0) return false; // a hole cannot be found again
        int found = ast._lists.AsSpan(start, count).IndexOf(_expected);
        index = found;
        return found >= 0;
    }

    /// <inheritdoc/>
    public bool Equals(NodeSlot other) => ReferenceEquals(_ast, other._ast) && _owner == other._owner && _code == other._code && _expected == other._expected;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is NodeSlot other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_owner, _code, _expected);

    /// <summary>Whether two handles refer to the same node in the same slot.</summary>
    public static bool operator ==(NodeSlot left, NodeSlot right) => left.Equals(right);

    /// <summary>Whether two handles differ.</summary>
    public static bool operator !=(NodeSlot left, NodeSlot right) => !left.Equals(right);
}
