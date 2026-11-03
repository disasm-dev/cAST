using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace cAST.Matching;

public static partial class Js
{
    extension<T>(T node) where T : struct, INode<T>
    {
        /// <summary>Every node under this one (not the node itself), in pre-order, nested functions included.</summary>
        public NodeWalk Descendants() => new(node.Ast, node.Id, NodeWalk.AllKinds, enterFunctions: true);

        /// <summary>The nodes of the given kinds under this one, in pre-order, nested functions included.</summary>
        public NodeWalk Descendants(params ReadOnlySpan<NodeKind> kinds) => new(node.Ast, node.Id, NodeWalk.Mask(kinds), enterFunctions: true);

        /// <summary>
        /// Every node under this one that belongs to its own function, in pre-order: a nested <see cref="FunctionLiteral"/>
        /// or <see cref="ArrowFunctionLiteral"/> is yielded but nothing inside it. Class and object methods, getters and
        /// setters have function literal bodies, so theirs are left out too; their keys are not. The node the walk starts
        /// from is always entered.
        /// </summary>
        public NodeWalk OwnDescendants() => new(node.Ast, node.Id, NodeWalk.AllKinds, enterFunctions: false);

        /// <summary>The nodes of the given kinds among those of <c>OwnDescendants()</c>.</summary>
        public NodeWalk OwnDescendants(params ReadOnlySpan<NodeKind> kinds) => new(node.Ast, node.Id, NodeWalk.Mask(kinds), enterFunctions: false);

        /// <summary>Whether a node of <paramref name="kind"/> is under this one; the walk stops at the first.</summary>
        public bool Any(NodeKind kind, bool enterFunctions) => NodeWalk.First(node.Ast, node.Id, NodeWalk.Mask([kind]), enterFunctions) != 0;

        /// <summary>Whether a node of one of <paramref name="kinds"/> is under this one; the walk stops at the first.</summary>
        public bool Any(ReadOnlySpan<NodeKind> kinds, bool enterFunctions) => NodeWalk.First(node.Ast, node.Id, NodeWalk.Mask(kinds), enterFunctions) != 0;

        /// <summary>The first node of <paramref name="kind"/> under this one in pre-order, or a none node.</summary>
        public Node First(NodeKind kind, bool enterFunctions) => new(node.Ast, NodeWalk.First(node.Ast, node.Id, NodeWalk.Mask([kind]), enterFunctions));

        /// <summary>The first node of one of <paramref name="kinds"/> under this one in pre-order, or a none node.</summary>
        public Node First(ReadOnlySpan<NodeKind> kinds, bool enterFunctions) => new(node.Ast, NodeWalk.First(node.Ast, node.Id, NodeWalk.Mask(kinds), enterFunctions));
    }
}

/// <summary>
/// The nodes under a node in pre-order: each node before its children, children in the order <see cref="AstVisitor"/>
/// visits them. The walk reads the arena's node records as it goes, with no visitor calls and no list of results; a
/// node's children are read when the walk moves past it, so a change to them made before then is seen.
/// </summary>
public readonly struct NodeWalk : IEnumerable<Node>
{
    internal static readonly UInt128 AllKinds = UInt128.MaxValue;

    private readonly JsAst? _ast;
    private readonly int _root;
    private readonly UInt128 _kinds;
    private readonly bool _enterFunctions;

    internal NodeWalk(JsAst? ast, int root, UInt128 kinds, bool enterFunctions)
    {
        _ast = ast;
        _root = root;
        _kinds = kinds;
        _enterFunctions = enterFunctions;
    }

    internal static UInt128 Mask(ReadOnlySpan<NodeKind> kinds)
    {
        UInt128 mask = 0;
        foreach (var kind in kinds)
            mask |= UInt128.One << (int)kind;
        return mask;
    }

    internal static int First(JsAst? ast, int root, UInt128 kinds, bool enterFunctions)
    {
        var walk = new Enumerator(ast, root, kinds, enterFunctions);
        try
        {
            return walk.MoveNext() ? walk.Current.Id : 0;
        }
        finally
        {
            walk.Dispose();
        }
    }

    /// <summary>Returns an enumerator over the nodes. Dispose it (<c>foreach</c> does) to give back its stack.</summary>
    public Enumerator GetEnumerator() => new(_ast, _root, _kinds, _enterFunctions);

    IEnumerator<Node> IEnumerable<Node>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>A struct enumerator over a <see cref="NodeWalk"/>, keeping the nodes still to visit on a pooled stack.</summary>
    public struct Enumerator : IEnumerator<Node>
    {
        private readonly JsAst? _ast;
        private readonly int _root;
        private readonly UInt128 _kinds;
        private readonly bool _enterFunctions;
        private int[]? _stack;
        private int _count;
        private int _current;
        private int _expand; // the node whose children go on the stack at the next MoveNext, or 0

        internal Enumerator(JsAst? ast, int root, UInt128 kinds, bool enterFunctions)
        {
            _ast = ast;
            _root = root;
            _kinds = kinds;
            _enterFunctions = enterFunctions;
            _expand = root;
        }

        /// <inheritdoc/>
        public readonly Node Current => new(_ast!, _current);

        readonly object IEnumerator.Current => Current;

        /// <inheritdoc/>
        public bool MoveNext()
        {
            var ast = _ast;
            while (true)
            {
                if (_expand != 0)
                {
                    PushChildren(ast!, _expand);
                    _expand = 0;
                }
                if (_count == 0)
                {
                    _current = 0;
                    return false;
                }
                int id = _stack![--_count];
                NodeKind kind = ast!._nodes[id].Kind;
                if (_enterFunctions || kind is not (NodeKind.FunctionLiteral or NodeKind.ArrowFunctionLiteral))
                    _expand = id;
                if (((_kinds >> (int)kind) & UInt128.One) != UInt128.Zero)
                {
                    _current = id;
                    return true;
                }
            }
        }

        private void PushChildren(JsAst ast, int id)
        {
            byte[] slots = NodeLayout.Slots[(int)ast._nodes[id].Kind];
            for (int i = slots.Length - 1; i >= 0; i--)
            {
                int slot = slots[i] & 15;
                int field = Unsafe.Add(ref ast._nodes[id].A, slot);
                switch (slots[i] >> 4)
                {
                    case 1:
                        if (field != 0) Push(field);
                        break;
                    case 2:
                        for (int k = Unsafe.Add(ref ast._nodes[id].A, slot + 1) - 1; k >= 0; k--)
                        {
                            int element = ast._lists[field + k];
                            if (element != 0) Push(element);
                        }
                        break;
                }
            }
        }

        private void Push(int id)
        {
            if (_stack is null || _count == _stack.Length) Grow();
            _stack[_count++] = id;
        }

        [MemberNotNull(nameof(_stack))]
        private void Grow()
        {
            int[] grown = ArrayPool<int>.Shared.Rent(_stack is null ? 32 : _stack.Length * 2);
            if (_stack is not null)
            {
                _stack.AsSpan(0, _count).CopyTo(grown);
                ArrayPool<int>.Shared.Return(_stack);
            }
            _stack = grown;
        }

        /// <inheritdoc/>
        public void Reset()
        {
            _count = 0;
            _current = 0;
            _expand = _root;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_stack is null) return;
            ArrayPool<int>.Shared.Return(_stack);
            _stack = null;
            _count = 0;
        }
    }
}
