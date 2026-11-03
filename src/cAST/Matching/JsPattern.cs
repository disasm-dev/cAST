using System.Runtime.CompilerServices;
using cAST.Parsing;

namespace cAST.Matching;

/// <summary>
/// A JavaScript template matched against nodes node for node: kinds, operators, flags, names and literal values (not their
/// raw text) must be equal; positions, comments and scope contexts are ignored. Identifiers starting with <c>$</c> are
/// holes: <c>$name</c> matches any expression and binds it, <c>$name$</c> matches a number literal holding a whole
/// number of at most 2^53 either way, or <c>-</c> on one (not <c>-0</c>), and binds its value, and <c>$$</c> matches any
/// expression without binding it. A hole used twice matches only the same thing both times: identifiers of the same name
/// (not binding), or the same integer.
/// <para>
/// A <c>let</c> statement declaring one <c>$name</c> (or <c>$$</c>) with a value is a write: <c>let $x = value;</c>
/// matches <c>x = value;</c> with plain <c>=</c> as well as <c>var</c>, <c>let</c> or <c>const</c> declaring <c>x</c> alone
/// with that value, and <see cref="Bindings.Spelling"/> says which. Other declarations, <c>let</c> in a <c>for</c> head
/// included, match their own kind only.
/// </para>
/// </summary>
/// <remarks>
/// Parse a pattern once and keep it (<c>static readonly</c>). Matching only reads it, so one pattern can be used from
/// several threads at once, and allocates nothing.
/// </remarks>
/// <example><c>JsPattern.Parse("$t = $o[$k] ^ $n$;")</c> matches <c>x = table[i] ^ 0x5f;</c>.</example>
public sealed class JsPattern
{
    private const sbyte Wildcard = -1;
    private const sbyte Write = -2;
    private const double MaxWhole = 9007199254740992; // 2^53: every whole number up to it is a double

    private readonly JsAst _ast;
    private readonly int[] _statements;
    private readonly sbyte[] _holes; // per template node: 0, Wildcard, Write on a write statement, or 1 + the index of its hole
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
        int head = 0; // the declaration heading the last for loop: not a statement, so not a write
        foreach (var node in block.Descendants(NodeKind.ForStatement, NodeKind.VariableDeclaration))
        {
            if (node.Kind == NodeKind.ForStatement)
            {
                head = node.AsForStatement().Initializer.Id;
                continue;
            }
            var declaration = node.AsVariableDeclaration();
            if (node.Id == head || declaration.VarKind != VarKind.Let || declaration.List.Count != 1) continue;
            var declarator = declaration.List[0];
            int target = holes[declarator.Target.Id];
            if (!declarator.Initializer.IsNone && (target == Wildcard || target > 0 && !IsInteger(integers, target - 1)))
                holes[node.Id] = Write;
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
    public bool Match(NodeList<Statement> statements, int start, out Bindings bindings) =>
        Run(statements, start, skip: false, out bindings, out _);

    /// <summary>
    /// Matches the template's statements against those of <paramref name="statements"/> from <paramref name="start"/> on,
    /// passing over bare declarations such as <c>var a, b;</c> between them: the first template statement must match the
    /// statement at <paramref name="start"/>, and a declaration with no value that the next template statement does not
    /// match is skipped. When the run matches, <paramref name="end"/> is the index after its last statement.
    /// </summary>
    public bool Match(NodeList<Statement> statements, int start, out Bindings bindings, out int end) =>
        Run(statements, start, skip: true, out bindings, out end);

    /// <summary>
    /// As <see cref="Match(NodeList{Statement}, int, out Bindings, out int)"/>, for statements gathered into a list.
    /// </summary>
    /// <exception cref="ArgumentException">The statements of the run belong to more than one arena.</exception>
    public bool Match(IReadOnlyList<Statement> statements, int start, out Bindings bindings, out int end) =>
        Run(statements, start, skip: true, out bindings, out end);

    internal int HoleIndex(string name, bool integer)
    {
        int index = Array.IndexOf(_names, name);
        if (index < 0) throw new KeyNotFoundException($"The template has no hole \"{name}\".");
        if (IsInteger(_integers, index) != integer)
            throw new InvalidOperationException(integer ? $"${name} is an expression hole: read it with the indexer." : $"${name}$ is an integer hole: read it with Int or Long.");
        return index;
    }

    private static bool IsInteger(int integers, int index) => ((integers >> index) & 1) != 0;

    private bool Run<TList>(TList statements, int start, bool skip, out Bindings bindings, out int end)
        where TList : IReadOnlyList<Statement>
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)start, (uint)statements.Count, nameof(start));
        var ast = start < statements.Count ? statements[start].Ast : null;
        bindings = new Bindings(this, ast);
        end = start;
        for (int i = 0; i < _statements.Length; i++)
        {
            while (true)
            {
                if (end == statements.Count) return false;
                var statement = statements[end++];
                if (!ReferenceEquals(statement.Ast, ast))
                    throw new ArgumentException("The statements belong to more than one arena.", nameof(statements));
                if (!skip || i == 0 || !IsBareDeclaration(statement))
                {
                    if (!Same(_statements[i], ast, statement.Id, ref bindings)) return false;
                    break;
                }
                var before = bindings;
                if (Same(_statements[i], ast, statement.Id, ref bindings)) break;
                bindings = before; // undo what the failed match bound
            }
        }
        return true;
    }

    // Whether the statement declares names with no values, as var a, b;
    private static bool IsBareDeclaration(Statement statement)
    {
        if (!statement.TryGetVariableDeclaration(out var declaration)) return false;
        foreach (var declarator in declaration.List)
        {
            if (!declarator.Initializer.IsNone) return false;
        }
        return true;
    }

    // Whether target node t of ast matches template node p, binding holes on the way.
    private bool Same(int p, JsAst ast, int t, ref Bindings bindings)
    {
        if (p == 0 || t == 0) return p == t;
        int hole = _holes[p];
        if (hole != 0) return hole == Write ? SameWrite(p, ast, t, ref bindings) : Fill(hole, ast, t, ref bindings);
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

    // Whether target node t is a write of what the write statement p, let $x = value;, describes.
    private bool SameWrite(int p, JsAst ast, int t, ref Bindings bindings)
    {
        var statement = new Statement(ast, t);
        WriteSpelling spelling;
        if (statement.IsAssignStatement(out var name, out var value))
        {
            spelling = WriteSpelling.Assign;
        }
        else if (statement.TryGetVariableDeclaration(out var declaration) && declaration.List.Count == 1)
        {
            name = new Expression(ast, declaration.List[0].Target.Id);
            value = declaration.List[0].Initializer;
            spelling = (WriteSpelling)((int)declaration.VarKind + 1);
        }
        else
        {
            return false;
        }
        if (name.Kind != NodeKind.Identifier || value.IsNone) return false;
        var write = new VariableDeclaration(_ast, p).List[0];
        int hole = _holes[write.Target.Id];
        if (!Fill(hole, ast, name.Id, ref bindings)) return false;
        if (hole != Wildcard) bindings.Spell(hole - 1, spelling);
        return Same(write.Initializer.Id, ast, value.Id, ref bindings);
    }

    private bool Fill(int hole, JsAst ast, int t, ref Bindings bindings)
    {
        var node = new Expression(ast, t);
        if (!NodeKinds.IsExpression(node.Kind)) return false;
        if (hole == Wildcard) return true;
        int index = hole - 1;
        if (IsInteger(_integers, index))
        {
            if (!IsWhole(node, out long value)) return false;
            if (bindings.TryGet(index, out long bound)) return bound == value;
            bindings.Set(index, value);
            return true;
        }
        if (bindings.TryGet(index, out long first))
        {
            return node.Kind == NodeKind.Identifier && ast._nodes[(int)first].Kind == NodeKind.Identifier
                && ast.GetLazySpan((int)first, 0).SequenceEqual(ast.GetLazySpan(t, 0));
        }
        bindings.Set(index, t);
        return true;
    }

    // Whether the expression is a number literal holding a whole number of at most 2^53 either way, or - on one; -0 is not.
    private static bool IsWhole(Expression node, out long value)
    {
        bool negated = node.TryGetUnaryExpression(out var u) && u.Operator == UnaryOperator.Negation;
        if ((negated ? u.Operand : node).TryGetNumberLiteral(out var number))
        {
            double v = negated ? -number.Value : number.Value;
            if (Math.Abs(v) <= MaxWhole && v == Math.Truncate(v) && (v != 0 || !double.IsNegative(v)))
            {
                value = (long)v;
                return true;
            }
        }
        value = 0;
        return false;
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
    private ulong _spellings; // 4 bits per hole: 1 + its WriteSpelling, or 0

    [InlineArray(Capacity)]
    private struct Values
    {
        private long _element0; // a node id, or an integer hole's value
    }

    internal Bindings(JsPattern pattern, JsAst? ast)
    {
        _pattern = pattern;
        _ast = ast;
    }

    /// <summary>The expression the hole <c>$name</c> matched.</summary>
    /// <exception cref="KeyNotFoundException">The template has no hole <paramref name="name"/>.</exception>
    /// <exception cref="InvalidOperationException">The hole is an integer hole, or the match failed before it.</exception>
    public readonly Expression this[string name] => new(_ast!, (int)Get(name, integer: false));

    /// <summary>The value the integer hole <c>$name$</c> matched.</summary>
    /// <exception cref="KeyNotFoundException">The template has no hole <paramref name="name"/>.</exception>
    /// <exception cref="InvalidOperationException">The hole is an expression hole, or the match failed before it.</exception>
    /// <exception cref="OverflowException">The value is outside <see cref="int"/>: read it with <see cref="Long"/>.</exception>
    public readonly int Int(string name)
    {
        long value = Get(name, integer: true);
        if (value is < int.MinValue or > int.MaxValue)
            throw new OverflowException($"${name}$ matched {value}, which is outside Int32: read it with Long.");
        return (int)value;
    }

    /// <summary>The value the integer hole <c>$name$</c> matched.</summary>
    /// <exception cref="KeyNotFoundException">The template has no hole <paramref name="name"/>.</exception>
    /// <exception cref="InvalidOperationException">The hole is an expression hole, or the match failed before it.</exception>
    public readonly long Long(string name) => Get(name, integer: true);

    /// <summary>
    /// How the statement matched by the first write statement <c>let $name = value;</c> of the template wrote the name.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The template has no hole <paramref name="name"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The hole is an integer hole, no write statement names it, or the match failed before it.
    /// </exception>
    public readonly WriteSpelling Spelling(string name)
    {
        if (_pattern is null) throw new InvalidOperationException("These bindings come from no match.");
        int spelling = (int)(_spellings >> (_pattern.HoleIndex(name, integer: false) * 4)) & 15;
        if (spelling == 0)
            throw new InvalidOperationException($"\"{name}\" is not written: no let ${name} = ...; statement names it, or the match failed before it.");
        return (WriteSpelling)(spelling - 1);
    }

    private readonly long Get(string name, bool integer)
    {
        if (_pattern is null) throw new InvalidOperationException("These bindings come from no match.");
        int index = _pattern.HoleIndex(name, integer);
        if (!TryGet(index, out long value)) throw new InvalidOperationException($"The hole \"{name}\" is not bound: the match failed before it.");
        return value;
    }

    internal readonly bool TryGet(int index, out long value)
    {
        value = _values[index];
        return ((_bound >> index) & 1) != 0;
    }

    internal void Set(int index, long value)
    {
        _values[index] = value;
        _bound |= 1 << index;
    }

    internal void Spell(int index, WriteSpelling spelling)
    {
        int shift = index * 4;
        if (((_spellings >> shift) & 15) == 0) _spellings |= (ulong)(spelling + 1) << shift;
    }
}

/// <summary>How a statement matched by a write statement <c>let $name = value;</c> of a template wrote the name.</summary>
public enum WriteSpelling : byte
{
    /// <summary><c>name = value;</c></summary>
    Assign,

    /// <summary><c>var name = value;</c></summary>
    Var,

    /// <summary><c>let name = value;</c></summary>
    Let,

    /// <summary><c>const name = value;</c></summary>
    Const,
}
