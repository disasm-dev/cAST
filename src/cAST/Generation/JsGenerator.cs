using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace cAST.Generation;

/// <summary>Code generation options.</summary>
public sealed record GeneratorOptions
{
    /// <summary>
    /// Disables pretty printing: no newlines, no indentation, and line comments are omitted so the output
    /// stays on a single line.
    /// </summary>
    public bool Minified { get; init; }

    /// <summary>The default (pretty) options.</summary>
    public static GeneratorOptions Pretty { get; } = new();

    /// <summary>Minified options.</summary>
    public static GeneratorOptions Minify { get; } = new() { Minified = true };
}

/// <summary>Renders an AST (or any node of it) back to JavaScript source.</summary>
public static class JsGenerator
{
    /// <summary>Renders the whole program of <paramref name="ast"/> using pretty printing.</summary>
    public static string Generate(JsAst ast) => Generate(ast.Program, GeneratorOptions.Pretty);

    /// <summary>Renders <paramref name="node"/> using pretty printing.</summary>
    public static string Generate(Node node) => Generate(node, GeneratorOptions.Pretty);

    /// <summary>Renders <paramref name="node"/> without pretty printing.</summary>
    public static string GenerateMinified(Node node) => Generate(node, GeneratorOptions.Minify);

    /// <summary>Renders the whole program of <paramref name="ast"/> without pretty printing.</summary>
    public static string GenerateMinified(JsAst ast) => Generate(ast.Program, GeneratorOptions.Minify);

    /// <summary>Renders <paramref name="node"/> with the given options.</summary>
    public static string Generate(Node node, GeneratorOptions options)
    {
        if (node.IsNone) return string.Empty;
        var g = new Generator(node.Ast, options.Minified, node.Ast.Source.Length + 64);
        g.InitStack();
        g.Gen(node.Id);
        return g.ToStringAndDispose();
    }

    /// <summary>Quotes a string as a JavaScript double-quoted string literal, as the generator writes one.</summary>
    public static string Quote(ReadOnlySpan<char> value) => Generator.Quote(value);

    /// <summary>Formats a finite number as the generator writes a number literal.</summary>
    public static string FormatNumber(double value) => Generator.FormatNumber(value);
}

// Performance notes. About half of all nodes are identifiers, so they never enter ExprCore: Expr writes them
// directly. ExprCore and StatementCore only dispatch; each node kind is emitted by its own small method, so a
// node pays for the registers and stack its own kind needs rather than for the union of all kinds. Methods on
// the hot path avoid locals of span type that are passed to non-inlined calls: those become GC-tracked stack
// slots the JIT zero-initializes in the prologue.
internal sealed class Generator
{
    [Flags]
    private enum Ctx : byte
    {
        None = 0,
        ForbidIn = 1,   // for-init position: a bare `in` needs parentheses
        ForbidCall = 2, // new-callee position: a bare call needs parentheses
    }

    private readonly JsAst _ast;
    private readonly string _source;
    private readonly bool _minified;
    private char[] _buf;
    private int _len;
    private int _indent;
    private nint _stackLimit;
    private const int StackCheckInterval = 64 * 1024;

    /// <summary>A pending operator of a linearized binary chain (see <see cref="Binary"/>).</summary>
    private struct BinaryEntry
    {
        public int Right;
        public byte Op;        // BinaryOperator, or LogicalOperator when Logical
        public bool Logical;
        public bool Wrap;
        public Precedence RightPrec;
        public Ctx Ctx;
    }

    private BinaryEntry[] _binaryStack = new BinaryEntry[32];
    private int _binaryTop;

    internal Generator(JsAst ast, bool minified, int capacityHint)
    {
        _ast = ast;
        _source = ast.Source;
        _minified = minified;
        _buf = ArrayPool<char>.Shared.Rent(Math.Clamp(capacityHint, 256, 1 << 26));
    }

    internal string ToStringAndDispose()
    {
        var s = new string(_buf, 0, _len);
        ArrayPool<char>.Shared.Return(_buf);
        _buf = [];
        return s;
    }

    // ---- output ------------------------------------------------------------------------------------
    //
    // Two tokens written back to back must not fuse into a different one: `a - -b` must not become `a--b`, and
    // a division followed by a regex or a comment must not become `//`. Only text starting with + - / * can
    // fuse with what precedes it, so only such writes look at the previous character (SeparateFrom); names,
    // keywords, punctuation, strings and numbers are appended directly.

    /// <summary>
    /// Whether emitting <paramref name="next"/> right after <paramref name="prev"/> would form a different
    /// token: <c>++</c>, <c>--</c>, or a comment after a division/regex.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Mergeable(char prev, char next) => prev switch
    {
        '+' => next == '+',
        '-' => next == '-',
        '/' => next is '/' or '*',
        _ => false,
    };

    /// <summary>Whether text starting with <paramref name="first"/> can fuse with the previous character.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanFuse(char first) => first is '+' or '-' or '/' or '*';

    /// <summary>Inserts a space if <paramref name="next"/> would fuse with the last character written.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SeparateFrom(char next)
    {
        if (_len > 0 && Mergeable(_buf[_len - 1], next)) Raw(' ');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private char[] Grow(int extra)
    {
        var bigger = ArrayPool<char>.Shared.Rent(Math.Max(_buf.Length * 2, _len + extra));
        _buf.AsSpan(0, _len).CopyTo(bigger);
        ArrayPool<char>.Shared.Return(_buf);
        _buf = bigger;
        return bigger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Raw(char c)
    {
        char[] buf = _buf;
        int len = _len;
        if ((uint)len >= (uint)buf.Length) buf = Grow(1);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), (nint)(uint)len) = c;
        _len = len + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Raw(ReadOnlySpan<char> s)
    {
        char[] buf = _buf;
        int len = _len;
        if ((uint)(len + s.Length) > (uint)buf.Length) buf = Grow(s.Length);
        s.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), (nint)(uint)len), s.Length));
        _len = len + s.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(char c)
    {
        if (CanFuse(c)) SeparateFrom(c);
        Raw(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(string s)
    {
        if (s.Length == 0) return;
        if (CanFuse(s[0])) SeparateFrom(s[0]);
        Raw(s.AsSpan());
    }

    private void Write(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return;
        if (CanFuse(s[0])) SeparateFrom(s[0]);
        Raw(s);
    }

    /// <summary>
    /// Appends the source text [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>),
    /// which must not start with + - / *. Short slices are copied with one vector load and store instead of a
    /// memmove call.
    /// </summary>
    private void WriteSource(int start, int length)
    {
        if (length == 0) return;
        ReadOnlySpan<char> text = _source.AsSpan(start, length);
        char[] buf = _buf;
        int len = _len;
        int reserve = length + ShortName;
        if ((uint)(len + reserve) > (uint)buf.Length) buf = Grow(reserve);
        ref char dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), (nint)(uint)len);
        if (length <= ShortName && start <= _source.Length - ShortName)
        {
            // Both sides have room for ShortName chars: the source has that many readable chars from start, and
            // the buffer was sized for length + ShortName. Chars written past the slice are overwritten later.
            ref ushort src = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(text));
            if (Vector256.IsHardwareAccelerated) Vector256.LoadUnsafe(ref src).StoreUnsafe(ref Unsafe.As<char, ushort>(ref dst));
            else Vector128.LoadUnsafe(ref src).StoreUnsafe(ref Unsafe.As<char, ushort>(ref dst));
        }
        else
        {
            text.CopyTo(MemoryMarshal.CreateSpan(ref dst, length));
        }
        _len = len + length;
    }

    private void Line()
    {
        if (!_minified) Raw('\n');
    }

    /// <summary>A newline plus one tab per indentation level (pretty printing only), written in one go.</summary>
    private void LineAndPad()
    {
        if (_minified) return;
        int indent = _indent;
        char[] buf = _buf;
        int len = _len;
        if ((uint)(len + 1 + indent) > (uint)buf.Length) buf = Grow(1 + indent);
        ref char dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), (nint)(uint)len);
        dst = '\n';
        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref dst, 1), indent).Fill('\t');
        _len = len + 1 + indent;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Space()
    {
        if (!_minified) Raw(' ');
    }

    // ---- helpers -----------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref NodeRecord R(int id) => ref _ast._nodes[id];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NodeKind K(int id) => _ast._nodes[id].Kind;

    /// <summary>Writes the name of an Identifier node (or, defensively, the text of whatever sits in its place).</summary>
    /// <remarks>
    /// The common case, a lazy name of at most <see cref="ShortName"/> chars, is one vector load and store with
    /// no calls, so this method needs no callee-saved registers; everything else goes to <see cref="NameSlow"/>.
    /// </remarks>
    private void Name(int id)
    {
        ref NodeRecord r = ref R(id);
        int start = r.C, length = r.D, len = _len;
        string source = _source;
        char[] buf = _buf;
        if (r.Kind == NodeKind.Identifier && r.A == 0 && (uint)(length - 1) < (uint)ShortName
            && start >= 0 && start <= source.Length - ShortName && len <= buf.Length - ShortName)
        {
            // A lazy name is the identifier's own source text, which starts with an identifier character (no
            // token fusing). ShortName chars are readable at start and writable at len; the extra chars written
            // past the name are overwritten by later output.
            ref ushort src = ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source.AsSpan()), (nint)(uint)start));
            ref ushort dst = ref Unsafe.As<char, ushort>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buf), (nint)(uint)len));
            if (Vector256.IsHardwareAccelerated) Vector256.LoadUnsafe(ref src).StoreUnsafe(ref dst);
            else Vector128.LoadUnsafe(ref src).StoreUnsafe(ref dst);
            _len = len + length;
            return;
        }
        NameSlow(id);
    }

    /// <summary>The longest name <see cref="Name"/> copies with a single vector (32 or 16 bytes).</summary>
    private static int ShortName => Vector256.IsHardwareAccelerated ? 16 : 8;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NameSlow(int id)
    {
        ref NodeRecord r = ref R(id);
        if (r.Kind == NodeKind.Identifier)
        {
            if (r.A == 0) WriteSource(r.C, r.D);
            else Write(_ast.GetStringSpan(r.A));
            return;
        }
        Write(_ast.GetLazySpan(id, 0));
    }

    /// <summary>Writes lazy string slot <paramref name="slot"/> of node <paramref name="id"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteLazy(int id, int slot) => Write(_ast.GetLazySpan(id, slot));

    /// <summary>Starts stack-overflow tracking for a generation run on the current thread.</summary>
    internal unsafe void InitStack()
    {
        byte probe;
        _stackLimit = (nint)(&probe) - StackCheckInterval;
    }

    /// <summary>Guards deep recursion (deeply nested input) with one pointer compare; see JsParser.StackIsLow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool StackIsLow()
    {
        byte probe;
        return (nint)(&probe) < _stackLimit && StackIsReallyLow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe bool StackIsReallyLow()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
        byte probe;
        _stackLimit = (nint)(&probe) - StackCheckInterval;
        return false;
    }

    // Cold paths: the lambdas live in their own methods so the hot callers never allocate a closure.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExprOnNewStack(int id, Precedence prec, Ctx ctx) => OnNewStack(() => ExprCore(id, prec, ctx));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StatementOnNewStack(int id) => OnNewStack(() => StatementCore(id));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PatternOnNewStack(int id) => OnNewStack(() => PatternCore(id));

    private void OnNewStack(Action work)
    {
        Exception? error = null;
        nint savedLimit = _stackLimit;
        var thread = new Thread(() =>
        {
            InitStack();
            try { work(); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        _stackLimit = savedLimit;
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    // ---- entry -------------------------------------------------------------------------------------

    /// <summary>Generates any node (go-fAST's <c>g.gen(node)</c>).</summary>
    internal void Gen(int id)
    {
        if (id == 0) return;
        NodeKind kind = K(id);
        if (NodeKinds.IsStatement(kind) || kind == NodeKind.Program) { Statement(id); return; }
        switch (kind)
        {
            case NodeKind.ArrayPattern:
            case NodeKind.ObjectPattern:
            case NodeKind.AssignmentPattern:
                Pattern(id);
                return;
            case NodeKind.VariableDeclarator:
                Declarator(id);
                return;
            case NodeKind.ParameterList:
                ParameterList(id);
                return;
            case NodeKind.SwitchCase:
                SwitchCase(id);
                return;
            case NodeKind.CatchClause:
                {
                    ref NodeRecord r = ref R(id);
                    int param = r.A, body = r.B;
                    if (param != 0) Pattern(param);
                    Statement(body);
                    return;
                }
            case NodeKind.TemplateElement:
                WriteLazy(id, 0);
                return;
            case NodeKind.ComputedProperty:
            case NodeKind.PropertyGetter:
            case NodeKind.PropertyKeyValue:
            case NodeKind.PropertyMethod:
            case NodeKind.PropertySetter:
            case NodeKind.PropertyShort:
            case NodeKind.PatternKeyValue:
            case NodeKind.PatternShorthand:
            case NodeKind.FieldDefinition:
            case NodeKind.MethodDefinition:
            case NodeKind.ClassStaticBlock:
                Member(id);
                return;
        }
        Expr(id, Precedence.Lowest, Ctx.None);
    }

    // ---- expressions -------------------------------------------------------------------------------

    /// <summary>Emits an expression; identifiers are written without entering <see cref="ExprCore"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Expr(int id, Precedence prec, Ctx ctx)
    {
        if (K(id) == NodeKind.Identifier) Name(id);
        else ExprCore(id, prec, ctx);
    }

    /// <summary>
    /// Dispatches an expression to the emitter for its kind. Every case is a single call in tail position, so
    /// this compiles to a jump table of tail jumps with no frame. The emitters that recurse check the stack
    /// themselves (a stack probe here would take a local's address, which rules out tail calls).
    /// </summary>
    private void ExprCore(int id, Precedence prec, Ctx ctx)
    {
        switch (K(id))
        {
            case NodeKind.BinaryExpression:
            case NodeKind.LogicalExpression:
                Binary(id, prec, ctx);
                return;
            case NodeKind.Identifier:
                Name(id);
                return;
            case NodeKind.StringLiteral:
                StringLiteral(id);
                return;
            case NodeKind.NumberLiteral:
                NumberLiteral(id, prec);
                return;
            case NodeKind.BigIntLiteral:
                BigIntLiteral(id, prec);
                return;
            case NodeKind.BooleanLiteral:
                BooleanLiteral(id);
                return;
            case NodeKind.NullLiteral:
                Keyword("null");
                return;
            case NodeKind.ThisExpression:
                Keyword("this");
                return;
            case NodeKind.SuperExpression:
                Keyword("super");
                return;
            case NodeKind.RegExpLiteral:
                WriteLazy(id, 0);
                return;
            case NodeKind.MetaProperty:
                MetaProperty(id);
                return;
            case NodeKind.PrivateIdentifier:
                PrivateName(id);
                return;
            case NodeKind.InvalidExpression:
                return;
            case NodeKind.AssignExpression:
                Assign(id, prec, ctx);
                return;
            case NodeKind.ConditionalExpression:
                Conditional(id, prec, ctx);
                return;
            case NodeKind.UnaryExpression:
                Unary(id, prec, ctx);
                return;
            case NodeKind.UpdateExpression:
                Update(id, prec, ctx);
                return;
            case NodeKind.SequenceExpression:
                Sequence(id, prec, ctx);
                return;
            case NodeKind.YieldExpression:
                Yield(id, prec, ctx);
                return;
            case NodeKind.AwaitExpression:
                Await(id, prec, ctx);
                return;
            case NodeKind.SpreadElement:
                Spread(id, prec, ctx);
                return;
            case NodeKind.CallExpression:
                Call(id, prec, ctx);
                return;
            case NodeKind.NewExpression:
                New(id, prec, ctx);
                return;
            case NodeKind.MemberExpression:
                MemberAccess(id, prec, ctx);
                return;
            case NodeKind.PrivateDotExpression:
                PrivateDot(id, prec, ctx);
                return;
            case NodeKind.OptionalChain:
            case NodeKind.Optional:
                OptionalInner(id, prec, ctx);
                return;
            case NodeKind.ArrowFunctionLiteral:
                Arrow(id, prec, ctx);
                return;
            case NodeKind.FunctionLiteral:
                Function(id);
                return;
            case NodeKind.ClassLiteral:
                Class(id);
                return;
            case NodeKind.TemplateLiteral:
                Template(id, prec, ctx);
                return;
            case NodeKind.ArrayLiteral:
                ArrayLiteral(id, prec, ctx);
                return;
            case NodeKind.ObjectLiteral:
                ObjectLiteral(id, prec, ctx);
                return;
            // Pattern-only nodes reached through an expression slot (e.g. a hand-built tree).
            case NodeKind.ArrayPattern:
            case NodeKind.ObjectPattern:
            case NodeKind.AssignmentPattern:
                PatternNode(id);
                return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PatternNode(int id) => Pattern(id);

    // Leaf emitters. The small ones are kept out of line: inlined into ExprCore, their buffer-growth calls would
    // give the dispatcher a frame again.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Keyword(string text) => Write(text);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BooleanLiteral(int id) => Write((R(id).Flags & 1) != 0 ? "true" : "false");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MetaProperty(int id) => Write(((MetaPropertyKind)R(id).Op).Text);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrivateName(int id)
    {
        Write('#');
        Name(R(id).A);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BigIntLiteral(int id, Precedence prec)
    {
        if (_ast.HasLazyString(id, 1))
        {
            WriteLazy(id, 1);
            return;
        }
        var value = new BigIntLiteral(_ast, id).Value;
        // A negative value prints as a unary minus, which binds looser than member access or `**`.
        bool wrap = value.Sign < 0 && prec > Precedence.Prefix;
        if (wrap) Write('(');
        Write(value.ToString(CultureInfo.InvariantCulture));
        Write('n');
        if (wrap) Write(')');
    }

    // Emitters that recurse: each starts with the stack check and, when the stack runs low, restarts the
    // node on a fresh thread through ExprOnNewStack. Every recursion cycle passes through one of them (or
    // through Statement / Pattern, which check too).

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Spread(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        Write("...");
        Expr(R(id).A, Precedence.Assign, Ctx.None);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OptionalInner(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        Expr(R(id).A, Precedence.Call, Ctx.None);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Assign(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int left = r.A, right = r.B;
        var op = (AssignmentOperator)r.Op;
        bool wrap = prec > Precedence.Assign;
        if (wrap)
        {
            Write('(');
            ctx &= ~Ctx.ForbidIn;
        }
        Pattern(left);
        Space();
        Write(op.Text);
        Space();
        Expr(right, Precedence.Assign, ctx & Ctx.ForbidIn);
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Conditional(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int test = r.A, consequent = r.B, alternate = r.C;
        bool wrap = prec > Precedence.Conditional;
        if (wrap)
        {
            Write('(');
            ctx &= ~Ctx.ForbidIn;
        }
        Expr(test, Precedence.Conditional + 1, ctx & Ctx.ForbidIn);
        Space();
        Write('?');
        Space();
        Expr(consequent, Precedence.Assign, Ctx.None);
        Space();
        Write(':');
        Space();
        Expr(alternate, Precedence.Assign, ctx & Ctx.ForbidIn);
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Unary(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int operand = r.A;
        var op = (UnaryOperator)r.Op;
        bool wrap = prec > Precedence.Prefix;
        if (wrap) Write('(');
        Write(op.Text);
        if (op.IsKeyword) Write(' ');
        Expr(operand, Precedence.Prefix, Ctx.None);
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Update(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int operand = r.A;
        var op = (UpdateOperator)r.Op;
        if ((r.Flags & 1) != 0)
        {
            bool wrap = prec > Precedence.Postfix;
            if (wrap) Write('(');
            Expr(operand, Precedence.Postfix, Ctx.None);
            Write(op.Text);
            if (wrap) Write(')');
        }
        else
        {
            bool wrap = prec > Precedence.Prefix;
            if (wrap) Write('(');
            Write(op.Text);
            Expr(operand, Precedence.Prefix, Ctx.None);
            if (wrap) Write(')');
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Sequence(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B;
        bool wrap = prec > Precedence.Comma;
        if (wrap)
        {
            Write('(');
            ctx &= ~Ctx.ForbidIn;
        }
        for (int i = 0; i < count; i++)
        {
            Expr(_ast._lists[start + i], Precedence.Assign, ctx & Ctx.ForbidIn);
            if (i < count - 1)
            {
                Write(',');
                Space();
            }
        }
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Yield(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int argument = r.A;
        bool @delegate = (r.Flags & 1) != 0;
        bool wrap = prec > Precedence.Yield;
        if (wrap)
        {
            Write('(');
            ctx = Ctx.None;
        }
        Write("yield");
        if (@delegate) Write('*');
        if (argument != 0)
        {
            Write(' ');
            // YieldExpression[In]: the argument inherits [In], so a bare `in` stays forbidden in a for-init.
            Expr(argument, Precedence.Assign, ctx & Ctx.ForbidIn);
        }
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Await(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        int argument = R(id).A;
        bool wrap = prec > Precedence.Prefix;
        if (wrap) Write('(');
        Write("await ");
        Expr(argument, Precedence.Prefix, Ctx.None);
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Call(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int callee = r.A, start = r.B, count = r.C;
        bool wrap = (ctx & Ctx.ForbidCall) != 0;
        if (wrap) Write('(');
        bool optional = OptionalBase(ref callee);
        AccessHead(callee, Precedence.Call, wrapNumber: false, Ctx.None);
        if (optional) Write("?.");
        Arguments(start, count);
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void New(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int callee = r.A, start = r.B, count = r.C;
        Write("new ");
        // The callee is a MemberExpression: a call anywhere along its head chain would end it (new a().b() is
        // (new a()).b()), so ForbidCall reaches through member accesses and tags; `?.` cannot appear at all.
        if (K(callee) == NodeKind.OptionalChain)
        {
            Write('(');
            Expr(callee, Precedence.Lowest, Ctx.None);
            Write(')');
        }
        else
        {
            Expr(callee, Precedence.New, Ctx.ForbidCall);
        }
        Arguments(start, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MemberAccess(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int obj = r.A, property = r.B;
        bool optional = OptionalBase(ref obj);
        AccessHead(obj, Precedence.Member, wrapNumber: true, ctx & Ctx.ForbidCall);
        MemberProperty(property, optional);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrivateDot(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int left = r.A, ident = r.B;
        bool optional = OptionalBase(ref left);
        AccessHead(left, Precedence.Member, wrapNumber: true, ctx & Ctx.ForbidCall);
        if (optional) Write('?');
        Write(".#");
        Name(R(ident).A);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Arrow(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int parameters = r.A, body = r.B;
        bool async = (r.Flags & 1) != 0;
        bool wrap = prec > Precedence.Assign;
        if (wrap)
        {
            Write('(');
            ctx = Ctx.None;
        }
        if (async) Write("async ");
        ParameterList(parameters);
        Space();
        Write("=>");
        Space();
        NodeKind bodyKind = K(body);
        if (bodyKind == NodeKind.BlockStatement)
        {
            Statement(body);
        }
        else if (bodyKind == NodeKind.ObjectLiteral)
        {
            Write('(');
            Expr(body, Precedence.Lowest, Ctx.None);
            Write(')');
        }
        else
        {
            // ConciseBody[In]: [lookahead != {] ExpressionBody[?In]. Anything that prints starting with `{`
            // (`({} + 1)`, `({} = x)`, `({}).x ? a : b` ...) would read as a block body, so it is wrapped.
            int start = _len;
            Expr(body, Precedence.Assign, ctx & Ctx.ForbidIn);
            if (_len > start && _buf[start] == '{') WrapFrom(start);
        }
        if (wrap) Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Template(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int tag = r.A, elemStart = r.B, elemCount = r.C, exprStart = r.D, exprCount = r.E;
        if (tag != 0) AccessHead(tag, Precedence.Call, wrapNumber: false, ctx & Ctx.ForbidCall);
        Write('`');
        for (int i = 0; i < elemCount; i++)
        {
            TemplateChunk(_ast._lists[elemStart + i]);
            if (i < exprCount)
            {
                Raw("${");
                Expr(_ast._lists[exprStart + i], Precedence.Lowest, Ctx.None);
                Write('}');
            }
        }
        Write('`');
    }

    /// <summary>The raw text of a template element, written as is (it sits between backticks/braces).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TemplateChunk(int element) => Raw(_ast.GetLazySpan(element, 0));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ArrayLiteral(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B;
        Write('[');
        for (int i = 0; i < count; i++)
        {
            int element = _ast._lists[start + i];
            if (element != 0) Expr(element, Precedence.Assign, Ctx.None);
            if (i < count - 1)
            {
                Write(',');
                Space();
            }
            else if (element == 0)
            {
                Write(','); // keep a trailing elision: [a,,] has length 2
            }
        }
        Write(']');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ObjectLiteral(int id, Precedence prec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, prec, ctx);
            return;
        }
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B;
        Write('{');
        _indent++;
        for (int i = 0; i < count; i++)
        {
            LineAndPad();
            Member(_ast._lists[start + i]);
            if (i < count - 1) Write(',');
        }
        _indent--;
        if (count > 0) LineAndPad();
        Write('}');
    }

    /// <summary>Unwraps an Optional (the object/callee right before <c>?.</c>).</summary>
    private bool OptionalBase(ref int expr)
    {
        if (K(expr) == NodeKind.Optional)
        {
            expr = R(expr).A;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Emits the object/callee of a member or call, parenthesizing literals that would misparse
    /// (object/function/class), nested optional chains, and (for member access) numbers. <paramref name="ctx"/>
    /// carries ForbidCall from a <c>new</c> callee down the head chain.
    /// </summary>
    private void AccessHead(int expr, Precedence prec, bool wrapNumber, Ctx ctx)
    {
        bool wrap;
        switch (K(expr))
        {
            case NodeKind.Identifier:
                Name(expr);
                return;
            case NodeKind.ObjectLiteral:
            case NodeKind.FunctionLiteral:
            case NodeKind.ClassLiteral:
            case NodeKind.OptionalChain:
                wrap = true;
                break;
            case NodeKind.NumberLiteral:
                wrap = wrapNumber;
                break;
            default:
                wrap = false;
                break;
        }
        if (wrap)
        {
            Write('(');
            Expr(expr, Precedence.Lowest, Ctx.None);
            Write(')');
            return;
        }
        ExprCore(expr, prec, ctx);
    }

    private void Arguments(int start, int count)
    {
        Write('(');
        for (int i = 0; i < count; i++)
        {
            Expr(_ast._lists[start + i], Precedence.Assign, Ctx.None);
            if (i < count - 1)
            {
                Write(',');
                Space();
            }
        }
        Write(')');
    }

    private void MemberProperty(int property, bool optional)
    {
        ref NodeRecord p = ref R(property);
        if (p.Kind == NodeKind.ComputedProperty)
        {
            int expr = p.A;
            if (optional) Write("?.");
            Write('[');
            // Assignment precedence keeps a comma sequence wrapped: a[(b,c)].
            Expr(expr, Precedence.Assign, Ctx.None);
            Write(']');
            return;
        }
        if (optional) Write("?.");
        else Write('.');
        Name(property);
    }

    /// <summary>
    /// Linearizes nested binary/logical trees: walks the left spine iteratively, then emits the operators and
    /// right operands while unwinding (so a+b+c+... does not recurse).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Binary(int expr, Precedence minPrec, Ctx ctx)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(expr, minPrec, ctx);
            return;
        }
        int baseTop = _binaryTop;
        while (true)
        {
            ref NodeRecord r = ref R(expr);
            Precedence opPrec, leftPrec, rightPrec;
            int left, right;
            bool isIn = false, logical;

            if (r.Kind == NodeKind.BinaryExpression)
            {
                var op = (BinaryOperator)r.Op;
                logical = false;
                opPrec = op.Precedence;
                left = r.A;
                right = r.B;
                isIn = op == BinaryOperator.In;
                leftPrec = opPrec;
                rightPrec = opPrec + 1;
                if (opPrec.IsRightAssociative)
                {
                    leftPrec = opPrec + 1;
                    rightPrec = opPrec;
                }
                // -x ** y is a syntax error: force parentheses on a unary (or negative literal) left operand.
                if (op == BinaryOperator.Exponential && (K(left) is NodeKind.UnaryExpression or NodeKind.AwaitExpression || IsNegativeNumber(left)))
                    leftPrec = Precedence.Call;
            }
            else if (r.Kind == NodeKind.LogicalExpression)
            {
                var op = (LogicalOperator)r.Op;
                logical = true;
                opPrec = op.Precedence;
                left = r.A;
                right = r.B;
                leftPrec = opPrec;
                rightPrec = opPrec + 1;
                // ?? cannot be mixed with && or || without parentheses.
                if (op == LogicalOperator.Coalesce)
                {
                    leftPrec = Precedence.LogicalAnd + 1;
                    rightPrec = leftPrec;
                }
            }
            else
            {
                // Leftmost leaf.
                Expr(expr, minPrec, ctx);
                break;
            }

            bool wrap = opPrec < minPrec || (isIn && (ctx & Ctx.ForbidIn) != 0);
            if (wrap) Write('(');
            ctx = wrap ? Ctx.None : ctx & Ctx.ForbidIn;

            if (_binaryTop == _binaryStack.Length) Array.Resize(ref _binaryStack, _binaryStack.Length * 2);
            _binaryStack[_binaryTop++] = new BinaryEntry { Right = right, Op = r.Op, Logical = logical, Wrap = wrap, RightPrec = rightPrec, Ctx = ctx };

            expr = left;
            minPrec = leftPrec;
        }

        while (_binaryTop > baseTop)
        {
            var e = _binaryStack[--_binaryTop];
            if (e.Logical)
            {
                Space();
                Write(((LogicalOperator)e.Op).Text);
                Space();
            }
            else
            {
                var op = (BinaryOperator)e.Op;
                if (op.IsKeyword)
                {
                    Raw(' ');
                    Write(op.Text);
                    Raw(' ');
                }
                else
                {
                    Space();
                    Write(op.Text);
                    Space();
                }
            }
            Expr(e.Right, e.RightPrec, e.Ctx);
            if (e.Wrap) Write(')');
        }
    }

    /// <summary>Whether a number or BigInt literal prints with a leading minus (its value is negative, or -0).</summary>
    private bool IsNegativeNumber(int id)
    {
        switch (K(id))
        {
            case NodeKind.NumberLiteral:
                {
                    double v = new NumberLiteral(_ast, id).Value;
                    return v < 0 || (v == 0 && double.IsNegative(v));
                }
            case NodeKind.BigIntLiteral:
                return !_ast.HasLazyString(id, 1) && new BigIntLiteral(_ast, id).Value.Sign < 0;
            default:
                return false;
        }
    }

    /// <summary>A string literal in expression position.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StringLiteral(int id)
    {
        ref NodeRecord r = ref R(id);
        // The raw text of an identifier-named key (Unquoted) is a bare name: as an expression it must be quoted.
        if ((r.Flags & 1) == 0)
        {
            // Raw text still in the source: a quoted literal (never starts with + - / *).
            if (r.B == 0 && r.D > 0)
            {
                WriteSource(r.C, r.D);
                return;
            }
            if (r.B != 0)
            {
                WriteLazy(id, 1);
                return;
            }
        }
        QuotedValue(id);
    }

    /// <summary>A string literal used as a property key: identifier-named keys keep their bare form.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void KeyString(int id)
    {
        ref NodeRecord r = ref R(id);
        if (r.B == 0 && r.D > 0)
        {
            WriteSource(r.C, r.D);
            return;
        }
        if (r.B != 0)
        {
            WriteLazy(id, 1);
            return;
        }
        ReadOnlySpan<char> value = _ast.GetLazySpan(id, 0);
        if ((r.Flags & 1) != 0 && Parsing.CharInfo.IsIdentifierName(value)) Write(value);
        else QuotedValue(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QuotedValue(int id) => Write(Quote(_ast.GetLazySpan(id, 0)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NumberLiteral(int id, Precedence prec)
    {
        ref NodeRecord r = ref R(id);
        // Raw digits still in the source (a numeric literal never starts with + - / *).
        if (r.C == 0 && r.E > 0)
        {
            WriteSource(r.D, r.E);
            return;
        }
        if (_ast.HasLazyString(id, 2))
        {
            WriteLazy(id, 2);
            return;
        }
        double value = new NumberLiteral(_ast, id).Value;
        if (double.IsPositiveInfinity(value))
        {
            Write("Infinity");
            return;
        }
        bool negative = value < 0 || double.IsNegativeInfinity(value) || (value == 0 && double.IsNegative(value));
        bool wrap = negative && prec > Precedence.Prefix;
        if (wrap) Write('(');
        Write(double.IsNegativeInfinity(value) ? "-Infinity" : FormatNumber(value));
        if (wrap) Write(')');
    }

    /// <summary>Formats a double like Go's <c>strconv.FormatFloat(v, 'f', -1, 64)</c>: shortest digits, no exponent.</summary>
    internal static string FormatNumber(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        string r = value.ToString("R", CultureInfo.InvariantCulture);
        int e = r.IndexOfAny(['E', 'e']);
        if (e < 0) return r;

        // Expand d.dddE±x to plain decimal notation.
        bool negative = r[0] == '-';
        string mantissa = r[(negative ? 1 : 0)..e];
        int exponent = int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.');
        string digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int pointPos = (dot < 0 ? mantissa.Length : dot) + exponent;

        var sb = new System.Text.StringBuilder(digits.Length + Math.Abs(exponent) + 3);
        if (negative) sb.Append('-');
        if (pointPos <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -pointPos);
            sb.Append(digits);
        }
        else if (pointPos >= digits.Length)
        {
            sb.Append(digits);
            sb.Append('0', pointPos - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, pointPos);
            sb.Append('.');
            sb.Append(digits, pointPos, digits.Length - pointPos);
        }
        return sb.ToString();
    }

    /// <summary>Quotes a string as a JavaScript double-quoted string literal.</summary>
    internal static string Quote(ReadOnlySpan<char> value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\v': sb.Append("\\v"); break;
                case '\x2028': sb.Append("\\u2028"); break;
                case '\x2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20 || c == 0x7F)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])))
                    {
                        // Lone surrogate: escape so the output stays valid UTF-16/UTF-8.
                        if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1])) sb.Append(c);
                        else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ---- functions and classes ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Function(int id)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, Precedence.Lowest, Ctx.None); // dispatches back here: a FunctionLiteral
            return;
        }
        ref NodeRecord r = ref R(id);
        int name = r.A, parameters = r.B, body = r.C;
        bool async = (r.Flags & 1) != 0, generator = (r.Flags & 2) != 0;
        if (async) Write("async ");
        Write("function");
        if (generator) Write('*');
        if (name != 0)
        {
            Write(' ');
            Name(name);
        }
        ParameterList(parameters);
        Space();
        Statement(body);
    }

    private void ParameterList(int id)
    {
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B, rest = r.C;
        Write('(');
        for (int i = 0; i < count; i++)
        {
            Declarator(_ast._lists[start + i]);
            if (i < count - 1)
            {
                Write(',');
                Space();
            }
        }
        if (rest != 0)
        {
            if (count > 0)
            {
                Write(',');
                Space();
            }
            Write("...");
            Pattern(rest);
        }
        Write(')');
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Class(int id)
    {
        if (StackIsLow())
        {
            ExprOnNewStack(id, Precedence.Lowest, Ctx.None); // dispatches back here: a ClassLiteral
            return;
        }
        ref NodeRecord r = ref R(id);
        int name = r.A, superClass = r.B, start = r.C, count = r.D;
        Write("class");
        if (name != 0)
        {
            Write(' ');
            Name(name);
        }
        if (superClass != 0)
        {
            Write(" extends ");
            // ClassHeritage: extends LeftHandSideExpression. Anything looser (a ? b : c, a + b, x = y, arrows,
            // unary and update expressions) needs parentheses.
            Expr(superClass, Precedence.Call, Ctx.None);
        }
        if (name == 0 && superClass == 0) Write(' ');
        else Space();
        Write('{');

        _indent++;
        for (int i = 0; i < count; i++)
        {
            LineAndPad();
            Member(_ast._lists[start + i]);
        }
        _indent--;

        LineAndPad();
        Write('}');
    }

    private void PropertyKey(int key)
    {
        ref NodeRecord r = ref R(key);
        switch (r.Kind)
        {
            case NodeKind.ComputedProperty:
                {
                    int expr = r.A;
                    Write('[');
                    Expr(expr, Precedence.Assign, Ctx.None);
                    Write(']');
                    return;
                }
            case NodeKind.StringLiteral:
                KeyString(key);
                return;
            case NodeKind.NumberLiteral or NodeKind.BigIntLiteral when IsNegativeNumber(key):
                // A negative value has no literal key form; the computed key [-1] names the same property.
                Write('[');
                Expr(key, Precedence.Assign, Ctx.None);
                Write(']');
                return;
            default:
                Expr(key, Precedence.Lowest, Ctx.None);
                return;
        }
    }

    private void MethodBody(int function)
    {
        ref NodeRecord f = ref R(function);
        int parameters = f.B, body = f.C;
        ParameterList(parameters);
        Space();
        Statement(body);
    }

    /// <summary>Object literal properties, object pattern properties and class elements.</summary>
    private void Member(int id)
    {
        ref NodeRecord r = ref R(id);
        switch (r.Kind)
        {
            case NodeKind.PropertyKeyValue:
                {
                    int key = r.A, value = r.B;
                    PropertyKey(key);
                    Write(':');
                    Space();
                    Expr(value, Precedence.Assign, Ctx.None);
                    return;
                }
            case NodeKind.PropertyMethod:
                {
                    int key = r.A, body = r.B;
                    ref NodeRecord f = ref R(body);
                    bool async = (f.Flags & 1) != 0, generator = (f.Flags & 2) != 0;
                    if (async)
                    {
                        Write("async");
                        if (!generator) Write(' ');
                    }
                    if (generator) Write('*');
                    PropertyKey(key);
                    MethodBody(body);
                    return;
                }
            case NodeKind.PropertyGetter:
            case NodeKind.PropertySetter:
                {
                    int key = r.A, body = r.B;
                    Write(r.Kind == NodeKind.PropertyGetter ? "get " : "set ");
                    PropertyKey(key);
                    MethodBody(body);
                    return;
                }
            case NodeKind.PropertyShort:
                {
                    int name = r.A, initializer = r.B;
                    Name(name);
                    if (initializer != 0)
                    {
                        Space();
                        Write('=');
                        Space();
                        Expr(initializer, Precedence.Assign, Ctx.None);
                    }
                    return;
                }
            case NodeKind.SpreadElement:
                Write("...");
                Expr(r.A, Precedence.Assign, Ctx.None);
                return;
            case NodeKind.PatternKeyValue:
                {
                    int key = r.A, value = r.B;
                    PropertyKey(key);
                    Write(':');
                    Space();
                    Pattern(value);
                    return;
                }
            case NodeKind.PatternShorthand:
                {
                    int name = r.A, initializer = r.B;
                    Name(name);
                    if (initializer != 0)
                    {
                        Space();
                        Write('=');
                        Space();
                        Expr(initializer, Precedence.Assign, Ctx.None);
                    }
                    return;
                }
            case NodeKind.MethodDefinition:
                {
                    int key = r.A, body = r.B;
                    var kind = (MethodKind)r.Op;
                    bool isStatic = (r.Flags & 1) != 0;
                    if (isStatic) Write("static ");
                    switch (kind)
                    {
                        case MethodKind.Get: Write("get "); break;
                        case MethodKind.Set: Write("set "); break;
                        default:
                            {
                                ref NodeRecord f = ref R(body);
                                bool async = (f.Flags & 1) != 0, generator = (f.Flags & 2) != 0;
                                if (async)
                                {
                                    Write("async");
                                    if (!generator) Write(' ');
                                }
                                if (generator) Write('*');
                                break;
                            }
                    }
                    PropertyKey(key);
                    MethodBody(body);
                    return;
                }
            case NodeKind.FieldDefinition:
                {
                    int key = r.A, initializer = r.B;
                    bool isStatic = (r.Flags & 1) != 0;
                    if (isStatic) Write("static ");
                    PropertyKey(key);
                    if (initializer != 0)
                    {
                        Space();
                        Write('=');
                        Space();
                        Expr(initializer, Precedence.Assign, Ctx.None);
                    }
                    Write(';');
                    return;
                }
            case NodeKind.ClassStaticBlock:
                {
                    int block = r.A;
                    Write("static");
                    Space();
                    Statement(block);
                    return;
                }
            case NodeKind.ComputedProperty:
                PropertyKey(id);
                return;
        }
        Gen(id);
    }

    // ---- patterns ----------------------------------------------------------------------------------

    private void Pattern(int id)
    {
        if (id == 0) return;
        if (K(id) == NodeKind.Identifier)
        {
            Name(id);
            return;
        }
        if (StackIsLow())
        {
            PatternOnNewStack(id);
            return;
        }
        PatternCore(id);
    }

    private void PatternCore(int id)
    {
        ref NodeRecord r = ref R(id);
        switch (r.Kind)
        {
            case NodeKind.ArrayPattern:
                {
                    int start = r.A, count = r.B, rest = r.C;
                    Write('[');
                    for (int i = 0; i < count; i++)
                    {
                        int element = _ast._lists[start + i];
                        if (element != 0) Pattern(element);
                        if (i < count - 1)
                        {
                            Write(',');
                            Space();
                        }
                        else if (element == 0 && rest == 0)
                        {
                            Write(',');
                        }
                    }
                    if (rest != 0)
                    {
                        if (count > 0)
                        {
                            Write(',');
                            Space();
                        }
                        Write("...");
                        Pattern(rest);
                    }
                    Write(']');
                    return;
                }
            case NodeKind.ObjectPattern:
                {
                    int start = r.A, count = r.B, rest = r.C;
                    Write('{');
                    for (int i = 0; i < count; i++)
                    {
                        Member(_ast._lists[start + i]);
                        if (i < count - 1)
                        {
                            Write(',');
                            Space();
                        }
                    }
                    if (rest != 0)
                    {
                        if (count > 0)
                        {
                            Write(',');
                            Space();
                        }
                        Write("...");
                        Pattern(rest);
                    }
                    Write('}');
                    return;
                }
            case NodeKind.AssignmentPattern:
                {
                    int left = r.A, right = r.B;
                    Pattern(left);
                    Space();
                    Write('=');
                    Space();
                    Expr(right, Precedence.Assign, Ctx.None);
                    return;
                }
            default:
                // identifier / member / private-dot / invalid
                Expr(id, Precedence.Lowest, Ctx.None);
                return;
        }
    }

    private void Declarator(int id)
    {
        ref NodeRecord r = ref R(id);
        int target = r.A, initializer = r.B;
        Pattern(target);
        if (initializer != 0)
        {
            Space();
            Write('=');
            Space();
            Expr(initializer, Precedence.Assign, Ctx.None);
        }
    }

    // ---- statements --------------------------------------------------------------------------------

    private void Statements(int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            LineAndPad();
            Statement(_ast._lists[start + i]);
        }
    }

    /// <summary>A statement body that is not a block gets its own indentation level.</summary>
    private void Body(int id)
    {
        if (K(id) is NodeKind.EmptyStatement or NodeKind.BlockStatement)
        {
            Statement(id);
            return;
        }
        _indent++;
        Statement(id);
        _indent--;
        LineAndPad();
    }

    private void Statement(int id)
    {
        if (StackIsLow())
        {
            StatementOnNewStack(id);
            return;
        }
        StatementCore(id);
    }

    /// <summary>Dispatches a statement to the emitter for its kind (single calls, like <see cref="ExprCore"/>).</summary>
    private void StatementCore(int id)
    {
        switch (K(id))
        {
            case NodeKind.Program:
                ProgramBody(id);
                return;
            case NodeKind.BlockStatement:
                Block(id);
                return;
            case NodeKind.ExpressionStatement:
                ExpressionStatement(id);
                return;
            case NodeKind.VariableDeclaration:
                VariableDeclarationStatement(id);
                return;
            case NodeKind.FunctionDeclaration:
                FunctionDeclaration(id);
                return;
            case NodeKind.ClassDeclaration:
                ClassDeclaration(id);
                return;
            case NodeKind.ReturnStatement:
                Return(id);
                return;
            case NodeKind.ThrowStatement:
                Throw(id);
                return;
            case NodeKind.IfStatement:
                If(id);
                return;
            case NodeKind.ForStatement:
                For(id);
                return;
            case NodeKind.ForInStatement:
            case NodeKind.ForOfStatement:
                ForInOf(id);
                return;
            case NodeKind.DoWhileStatement:
                DoWhile(id);
                return;
            case NodeKind.WhileStatement:
                While(id);
                return;
            case NodeKind.SwitchStatement:
                Switch(id);
                return;
            case NodeKind.TryStatement:
                Try(id);
                return;
            case NodeKind.BreakStatement:
            case NodeKind.ContinueStatement:
                BreakContinue(id);
                return;
            case NodeKind.LabelledStatement:
                Labelled(id);
                return;
            case NodeKind.WithStatement:
                With(id);
                return;
            case NodeKind.DebuggerStatement:
                Keyword("debugger;");
                return;
            case NodeKind.EmptyStatement:
                Keyword(";");
                return;
            case NodeKind.BadStatement:
                return;
        }
        Gen(id);
    }

    private void FunctionDeclaration(int id)
    {
        LineAndPad();
        Function(R(id).A);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ClassDeclaration(int id) => Class(R(id).A);

    private void Throw(int id)
    {
        Write("throw ");
        Expr(R(id).A, Precedence.Assign, Ctx.None);
        Write(';');
    }

    private void Labelled(int id)
    {
        ref NodeRecord r = ref R(id);
        int label = r.A, statement = r.B;
        Name(label);
        Write(':');
        Space();
        Statement(statement);
    }

    private void ProgramBody(int id)
    {
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B;
        for (int i = 0; i < count; i++)
        {
            Statement(_ast._lists[start + i]);
            Line();
        }
    }

    private void Block(int id)
    {
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B;
        Write('{');
        _indent++;
        Statements(start, count);
        _indent--;
        if (count > 0) LineAndPad();
        Write('}');
    }

    private void VariableDeclarationStatement(int id)
    {
        ref NodeRecord r = ref R(id);
        int start = r.A, count = r.B, comment = r.C;
        VariableDeclaration((VarKind)r.Op, start, count);
        Write(';');
        if (comment != 0 && !_minified) LineComment(comment);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LineComment(int comment)
    {
        Write(" // ");
        Write(_ast.GetStringSpan(comment));
    }

    private void Return(int id)
    {
        int argument = R(id).A;
        Write("return");
        if (argument != 0)
        {
            Write(' ');
            Expr(argument, Precedence.Assign, Ctx.None);
        }
        Write(';');
    }

    private void If(int id)
    {
        ref NodeRecord r = ref R(id);
        int test = r.A, consequent = r.B, alternate = r.C;
        Write("if");
        Space();
        Write('(');
        Expr(test, Precedence.Lowest, Ctx.None);
        Write(')');
        Space();
        if (alternate != 0 && EndsWithElselessIf(consequent)) BracedStatement(consequent);
        else Body(consequent);
        if (alternate != 0)
        {
            Write(" else ");
            if (K(alternate) == NodeKind.IfStatement) Statement(alternate);
            else Body(alternate);
        }
    }

    /// <summary>
    /// Whether <paramref name="statement"/> ends with an <c>if</c> that has no <c>else</c> (directly or as the body
    /// of a loop, label or with): printed as the consequent of an if/else, the else would attach to it.
    /// </summary>
    private bool EndsWithElselessIf(int statement)
    {
        while (true)
        {
            ref NodeRecord r = ref R(statement);
            switch (r.Kind)
            {
                case NodeKind.IfStatement:
                    if (r.C == 0) return true;
                    statement = r.C;
                    continue;
                case NodeKind.ForStatement:
                    statement = r.D;
                    continue;
                case NodeKind.ForInStatement:
                case NodeKind.ForOfStatement:
                    statement = r.C;
                    continue;
                case NodeKind.WhileStatement:
                case NodeKind.WithStatement:
                case NodeKind.LabelledStatement:
                    statement = r.B;
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>A statement wrapped in braces (one extra block, which declares nothing).</summary>
    private void BracedStatement(int id)
    {
        Write('{');
        _indent++;
        LineAndPad();
        Statement(id);
        _indent--;
        LineAndPad();
        Write('}');
    }

    private void For(int id)
    {
        ref NodeRecord r = ref R(id);
        int init = r.A, update = r.B, test = r.C, body = r.D;
        Write("for");
        Space();
        Write('(');
        if (init != 0)
        {
            ref NodeRecord ir = ref R(init);
            if (ir.Kind == NodeKind.VariableDeclaration)
            {
                VariableDeclaration((VarKind)ir.Op, ir.A, ir.B, Ctx.ForbidIn);
            }
            else
            {
                int start = _len;
                Expr(init, Precedence.Lowest, Ctx.ForbidIn);
                if (StartsWithLetBracket(start)) WrapFrom(start);
            }
        }
        Write(';');
        Space();
        if (test != 0) Expr(test, Precedence.Lowest, Ctx.None);
        Write(';');
        Space();
        if (update != 0) Expr(update, Precedence.Lowest, Ctx.None);
        Write(')');
        Space();
        Body(body);
    }

    private void ForInOf(int id)
    {
        ref NodeRecord r = ref R(id);
        bool isOf = r.Kind == NodeKind.ForOfStatement;
        int into = r.A, source = r.B, body = r.C;
        bool await = isOf && (r.Flags & 1) != 0;
        Write("for");
        if (await) Write(" await");
        Space();
        Write('(');
        ref NodeRecord ir = ref R(into);
        if (ir.Kind == NodeKind.VariableDeclaration)
        {
            VariableDeclaration((VarKind)ir.Op, ir.A, ir.B);
        }
        else
        {
            // for ( [lookahead != let [] LHS in ...), for ( [lookahead not in {let, async of}] LHS of ...), and
            // for await ( [lookahead != let] LHS of ...).
            int start = _len;
            Pattern(into);
            var head = _buf.AsSpan(start, _len - start);
            bool ambiguous = !isOf ? StartsWithLetBracket(start)
                : StartsWithWord(head, "let") || (!await && head.SequenceEqual("async"));
            if (ambiguous) WrapFrom(start);
        }
        Write(isOf ? " of " : " in ");
        // for-of takes an AssignmentExpression: a comma sequence must stay parenthesized.
        Expr(source, isOf ? Precedence.Assign : Precedence.Lowest, Ctx.None);
        Write(')');
        Space();
        Statement(body);
    }

    private void DoWhile(int id)
    {
        ref NodeRecord r = ref R(id);
        int test = r.A, body = r.B;
        Write("do ");
        Statement(body);
        Write(" while(");
        Expr(test, Precedence.Lowest, Ctx.None);
        Write(");");
    }

    private void While(int id)
    {
        ref NodeRecord r = ref R(id);
        int test = r.A, body = r.B;
        Write("while");
        Space();
        Write('(');
        Expr(test, Precedence.Lowest, Ctx.None);
        Write(')');
        Space();
        Statement(body);
    }

    private void Switch(int id)
    {
        ref NodeRecord r = ref R(id);
        int discriminant = r.A, start = r.B, count = r.C;
        Write("switch");
        Space();
        Write('(');
        Expr(discriminant, Precedence.Lowest, Ctx.None);
        Write(')');
        Space();
        Write('{');
        _indent++;
        for (int i = 0; i < count; i++)
        {
            LineAndPad();
            SwitchCase(_ast._lists[start + i]);
        }
        _indent--;
        if (count > 0) LineAndPad();
        Write('}');
    }

    private void Try(int id)
    {
        ref NodeRecord r = ref R(id);
        int block = r.A, handler = r.B, finalizer = r.C;
        Write("try");
        Space();
        Statement(block);
        if (handler != 0)
        {
            ref NodeRecord h = ref R(handler);
            int param = h.A, catchBody = h.B;
            Space();
            Write("catch");
            Space();
            if (param != 0)
            {
                Write('(');
                Pattern(param);
                Write(')');
                Space();
            }
            Statement(catchBody);
        }
        if (finalizer != 0)
        {
            Space();
            Write("finally");
            Space();
            Statement(finalizer);
        }
    }

    private void BreakContinue(int id)
    {
        ref NodeRecord r = ref R(id);
        int label = r.A;
        Write(r.Kind == NodeKind.BreakStatement ? "break" : "continue");
        if (label != 0)
        {
            Write(' ');
            Name(label);
        }
        Write(';');
    }

    private void With(int id)
    {
        ref NodeRecord r = ref R(id);
        int obj = r.A, body = r.B;
        Write("with");
        Space();
        Write('(');
        Expr(obj, Precedence.Lowest, Ctx.None);
        Write(')');
        Space();
        Statement(body);
    }

    private void VariableDeclaration(VarKind kind, int start, int count, Ctx ctx = Ctx.None)
    {
        Write(kind.Text);
        Write(' ');
        for (int i = 0; i < count; i++)
        {
            ref NodeRecord d = ref R(_ast._lists[start + i]);
            int target = d.A, initializer = d.B;
            Pattern(target);
            if (initializer != 0)
            {
                Space();
                Write('=');
                Space();
                Expr(initializer, Precedence.Assign, ctx);
            }
            if (i < count - 1)
            {
                Write(',');
                Space();
            }
        }
    }

    private void SwitchCase(int id)
    {
        ref NodeRecord r = ref R(id);
        int test = r.A, start = r.B, count = r.C;
        if (test != 0)
        {
            Write("case ");
            Expr(test, Precedence.Lowest, Ctx.None);
            Write(':');
        }
        else
        {
            Write("default:");
        }
        _indent++;
        Statements(start, count);
        _indent--;
    }

    private void ExpressionStatement(int id)
    {
        ref NodeRecord r = ref R(id);
        int expr = r.A, comment = r.B;
        ExpressionStatementBody(expr);
        if (comment != 0 && !_minified) LineComment(comment);
    }

    private void ExpressionStatementBody(int expr)
    {
        switch (K(expr))
        {
            case NodeKind.ObjectLiteral:
            case NodeKind.FunctionLiteral:
            case NodeKind.ClassLiteral:
                Write('(');
                Expr(expr, Precedence.Lowest, Ctx.None);
                Write(')');
                Write(';');
                return;
            case NodeKind.AssignExpression when K(R(expr).A) is NodeKind.ObjectPattern or NodeKind.ArrayPattern:
                Write('(');
                Expr(expr, Precedence.Lowest, Ctx.None);
                Write(')');
                Write(';');
                return;
        }

        // Anything else that happens to start with `{`, `function`, `class`, `let [` or `async function`
        // (e.g. `({}).x` is fine, but `({} + 1)` is not) is wrapped after the fact.
        int start = _len;
        Expr(expr, Precedence.Lowest, Ctx.None);
        if (NeedsStatementParens(start)) WrapFrom(start);
        Write(';');
    }

    /// <summary>Wraps everything written since <paramref name="start"/> in parentheses.</summary>
    private void WrapFrom(int start)
    {
        if (_len == _buf.Length) Grow(2);
        Array.Copy(_buf, start, _buf, start + 1, _len - start);
        _buf[start] = '(';
        _len++;
        Raw(')');
    }

    /// <summary>Whether the output since <paramref name="start"/> begins with the tokens <c>let [</c>.</summary>
    private bool StartsWithLetBracket(int start)
    {
        var s = _buf.AsSpan(start, _len - start);
        return StartsWithWord(s, "let") && s[3..].TrimStart(' ').StartsWith('[');
    }

    private bool NeedsStatementParens(int start)
    {
        var s = _buf.AsSpan(start, _len - start);
        if (s.IsEmpty) return false;
        char first = s[0];
        if (first == '{') return true;
        // Only the letters that begin `function`, `class`, `async function` and `let [` need a closer look.
        if (first is not ('f' or 'c' or 'a' or 'l')) return false;
        return StartsWithWord(s, "function") || StartsWithWord(s, "class")
            || (StartsWithWord(s, "async") && StartsWithWord(s[5..].TrimStart(' '), "function"))
            || StartsWithLetBracket(start);
    }

    private static bool StartsWithWord(ReadOnlySpan<char> s, string word) =>
        s.StartsWith(word) && (s.Length == word.Length || !Parsing.CharInfo.IsIdentifierPart(s[word.Length]));
}
