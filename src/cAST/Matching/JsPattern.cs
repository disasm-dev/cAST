using System.Runtime.CompilerServices;
using cAST.Parsing;

namespace cAST.Matching;

/// <summary>
/// A JavaScript template matched against nodes node for node: kinds, operators, flags, names and literal values (not their
/// raw text) must be equal; positions, comments and scope contexts are ignored. Identifiers starting with <c>$</c> are
/// holes: <c>$name</c> matches any expression and binds it, <c>$name$</c> matches an integer literal (as
/// <see cref="Js.IsInt32"/>) and binds its value, and <c>$$</c> matches any expression without binding it. A hole used
/// twice matches only the same thing both times: identifiers of the same name (not binding), or the same integer.
/// </summary>
/// <remarks>
/// Parse a pattern once and keep it (<c>static readonly</c>). Matching only reads it, so one pattern can be used from
/// several threads at once, and allocates nothing.
/// </remarks>
/// <example><c>JsPattern.Parse("$t = $o[$k] ^ $n$;")</c> matches <c>x = table[i] ^ 0x5f;</c>.</example>
public sealed class JsPattern
{
    private const sbyte Wildcard = -1;

    private readonly JsAst _ast;
    private readonly int[] _statements;
    private readonly sbyte[] _holes; // per template node: 0, Wildcard, or 1 + the index of its hole
    private readonly string[] _names;
    private readonly int _integers; // bit i set: hole i is an integer hole

    private JsPattern(JsAst ast, int[] statements, sbyte[] holes, string[] names, int integers)
    {
        _ast = ast;
        _statements = statements;
        _holes = holes;
        _names = names;
        _integers = integers;
    }

    /// <summary>
    /// Parses <paramref name="template"/>: one expression, one statement, or a run of statements. It is parsed as the body
    /// of a loop in a function, so it may <c>return</c>, <c>break</c> and <c>continue</c>.
    /// </summary>
    /// <exception cref="JsSyntaxException">The template is not valid JavaScript.</exception>
    /// <exception cref="ArgumentException">
    /// The template is empty, a hole is misspelled or used as both kinds, or there are more than 16 holes.
    /// </exception>
    public static JsPattern Parse(string template)
    {
        JsAst ast;
        using (var parsed = JsParser.ParseOrThrow("function _(){for(;;){" + template + "\n}}"))
            ast = parsed.Clone(); // right-sized: the parse may have rented a large pooled arena
        var block = ast.Program.Body[0].AsFunctionDeclaration().Function.Body.List[0].AsForStatement().Body.AsBlockStatement();
        if (block.List.IsEmpty) throw new ArgumentException("The template is empty.", nameof(template));
        var holes = new sbyte[ast.NodeCount];
        var names = new List<string>();
        int integers = 0;
        foreach (var node in block.Descendants(NodeKind.Identifier))
        {
            string text = node.AsIdentifier().Name;
            if (text.Length < 2 || text[0] != '$') continue;
            if (text == "$$")
            {
                holes[node.Id] = Wildcard;
                continue;
            }
            bool integer = text.Length > 2 && text[^1] == '$';
            string name = integer ? text[1..^1] : text[1..];
            if (name.Contains('$'))
                throw new ArgumentException($"\"{text}\" is not a hole: write $name, $name$ or $$.", nameof(template));
            int index = names.IndexOf(name);
            if (index < 0)
            {
                if (names.Count == Bindings.Capacity)
                    throw new ArgumentException($"A pattern has at most {Bindings.Capacity} holes.", nameof(template));
                index = names.Count;
                names.Add(name);
                if (integer) integers |= 1 << index;
            }
            else if (IsInteger(integers, index) != integer)
            {
                throw new ArgumentException($"\"{name}\" is used both as ${name} and as ${name}$.", nameof(template));
            }
            holes[node.Id] = (sbyte)(index + 1);
        }
        return new JsPattern(ast, block.List.Ids.ToArray(), holes, [.. names], integers);
    }

    /// <summary>Matches a template of one expression (with or without its semicolon) against <paramref name="node"/>.</summary>
    /// <exception cref="InvalidOperationException">The template is not one expression statement.</exception>
    public bool Match(Expression node, out Bindings bindings)
    {
        if (_statements.Length != 1 || _ast._nodes[_statements[0]].Kind != NodeKind.ExpressionStatement)
            throw new InvalidOperationException("The template is not one expression.");
        bindings = new Bindings(this, node.Ast);
        return Same(_ast._nodes[_statements[0]].A, node.Ast, node.Id, ref bindings);
    }

    /// <summary>Matches a template of one statement against <paramref name="node"/>.</summary>
    /// <exception cref="InvalidOperationException">The template is not one statement.</exception>
    public bool Match(Statement node, out Bindings bindings)
    {
        if (_statements.Length != 1)
            throw new InvalidOperationException("The template is not one statement.");
        bindings = new Bindings(this, node.Ast);
        return Same(_statements[0], node.Ast, node.Id, ref bindings);
    }

    /// <summary>
    /// Matches the template's statements against those of <paramref name="statements"/> from <paramref name="start"/> on,
    /// one for one; the list may go on after them.
    /// </summary>
    public bool Match(NodeList<Statement> statements, int start, out Bindings bindings)
    {
        var ast = statements._ast;
        bindings = new Bindings(this, ast);
        ReadOnlySpan<int> ids = statements.Ids;
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)start, (uint)ids.Length, nameof(start));
        if (ids.Length - start < _statements.Length) return false;
        for (int i = 0; i < _statements.Length; i++)
        {
            if (!Same(_statements[i], ast, ids[start + i], ref bindings)) return false;
        }
        return true;
    }

    internal int HoleIndex(string name, bool integer)
    {
        int index = Array.IndexOf(_names, name);
        if (index < 0) throw new KeyNotFoundException($"The template has no hole \"{name}\".");
        if (IsInteger(_integers, index) != integer)
            throw new InvalidOperationException(integer ? $"${name} is an expression hole: read it with the indexer." : $"${name}$ is an integer hole: read it with Int.");
        return index;
    }

    private static bool IsInteger(int integers, int index) => ((integers >> index) & 1) != 0;

    // Whether target node t of ast matches template node p, binding holes on the way.
    private bool Same(int p, JsAst ast, int t, ref Bindings bindings)
    {
        if (p == 0 || t == 0) return p == t;
        int hole = _holes[p];
        if (hole != 0) return Fill(hole, ast, t, ref bindings);
        ref NodeRecord pattern = ref _ast._nodes[p];
        ref NodeRecord target = ref ast._nodes[t];
        if (pattern.Kind != target.Kind || pattern.Op != target.Op) return false;
        switch (pattern.Kind)
        {
            case NodeKind.Identifier:
            case NodeKind.StringLiteral:
            case NodeKind.RegExpLiteral:
            case NodeKind.TemplateElement:
                return _ast.GetLazySpan(p, 0).SequenceEqual(ast.GetLazySpan(t, 0));
            case NodeKind.NumberLiteral:
                return new NumberLiteral(_ast, p).Value == new NumberLiteral(ast, t).Value;
            case NodeKind.BigIntLiteral:
                return _ast.GetBigInt(pattern.A) == ast.GetBigInt(target.A);
        }
        if (pattern.Flags != target.Flags) return false;
        foreach (byte slot in NodeLayout.Slots[(int)pattern.Kind])
        {
            int i = slot & 15;
            int from = Unsafe.Add(ref pattern.A, i), to = Unsafe.Add(ref target.A, i);
            switch (slot >> 4)
            {
                case 1:
                    if (!Same(from, ast, to, ref bindings)) return false;
                    break;
                case 2:
                    int count = Unsafe.Add(ref pattern.A, i + 1);
                    if (count != Unsafe.Add(ref target.A, i + 1)) return false;
                    for (int k = 0; k < count; k++)
                    {
                        if (!Same(_ast._lists[from + k], ast, ast._lists[to + k], ref bindings)) return false;
                    }
                    break;
            }
        }
        return true;
    }

    private bool Fill(int hole, JsAst ast, int t, ref Bindings bindings)
    {
        var node = new Expression(ast, t);
        if (!NodeKinds.IsExpression(node.Kind)) return false;
        if (hole == Wildcard) return true;
        int index = hole - 1;
        if (IsInteger(_integers, index))
        {
            if (!node.IsInt32(out int value)) return false;
            if (bindings.TryGet(index, out int bound)) return bound == value;
            bindings.Set(index, value);
            return true;
        }
        if (bindings.TryGet(index, out int first))
        {
            return node.Kind == NodeKind.Identifier && ast._nodes[first].Kind == NodeKind.Identifier
                && ast.GetLazySpan(first, 0).SequenceEqual(ast.GetLazySpan(t, 0));
        }
        bindings.Set(index, t);
        return true;
    }
}

/// <summary>What the holes of a <see cref="JsPattern"/> matched, by hole name without its <c>$</c> signs.</summary>
public struct Bindings
{
    internal const int Capacity = 16;

    private readonly JsPattern? _pattern;
    private readonly JsAst? _ast;
    private Values _values;
    private int _bound;

    [InlineArray(Capacity)]
    private struct Values
    {
        private int _element0;
    }

    internal Bindings(JsPattern pattern, JsAst? ast)
    {
        _pattern = pattern;
        _ast = ast;
    }

    /// <summary>The expression the hole <c>$name</c> matched.</summary>
    /// <exception cref="KeyNotFoundException">The template has no hole <paramref name="name"/>.</exception>
    /// <exception cref="InvalidOperationException">The hole is an integer hole, or the match failed before it.</exception>
    public readonly Expression this[string name] => new(_ast!, Get(name, integer: false));

    /// <summary>The value the integer hole <c>$name$</c> matched.</summary>
    /// <exception cref="KeyNotFoundException">The template has no hole <paramref name="name"/>.</exception>
    /// <exception cref="InvalidOperationException">The hole is an expression hole, or the match failed before it.</exception>
    public readonly int Int(string name) => Get(name, integer: true);

    private readonly int Get(string name, bool integer)
    {
        if (_pattern is null) throw new InvalidOperationException("These bindings come from no match.");
        int index = _pattern.HoleIndex(name, integer);
        if (!TryGet(index, out int value)) throw new InvalidOperationException($"The hole \"{name}\" is not bound: the match failed before it.");
        return value;
    }

    internal readonly bool TryGet(int index, out int value)
    {
        value = _values[index];
        return ((_bound >> index) & 1) != 0;
    }

    internal void Set(int index, int value)
    {
        _values[index] = value;
        _bound |= 1 << index;
    }
}
