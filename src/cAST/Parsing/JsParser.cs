using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace cAST.Parsing;

/// <summary>
/// A fast JavaScript (ECMAScript script) parser producing a <see cref="JsAst"/>.
/// </summary>
/// <remarks>
/// Positions in the AST are UTF-16 offsets into the source string. Parsing never throws on syntax errors:
/// errors are collected in <see cref="JsAst.Errors"/> and the returned tree may be partial. Use
/// <see cref="ParseOrThrow(string)"/> or <see cref="JsAst.ThrowIfErrors"/> to fail fast.
/// </remarks>
public sealed unsafe partial class JsParser
{
    // ---- entry points ------------------------------------------------------------------------------

    /// <summary>Parses <paramref name="source"/> as a script. Syntax errors are reported in <see cref="JsAst.Errors"/>.</summary>
    public static JsAst Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = Rent();
        try
        {
            return parser.Run(source);
        }
        finally
        {
            Return(parser);
        }
    }

    /// <summary>Parses <paramref name="source"/> as a script.</summary>
    public static JsAst Parse(ReadOnlySpan<char> source) => Parse(new string(source));

    /// <summary>Parses UTF-8 encoded <paramref name="utf8"/> as a script.</summary>
    public static JsAst ParseUtf8(ReadOnlySpan<byte> utf8) => Parse(Encoding.UTF8.GetString(utf8));

    /// <summary>Reads and parses a UTF-8 (or BOM-detected) file.</summary>
    public static JsAst ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses <paramref name="source"/> and throws a <see cref="JsSyntaxException"/> on any syntax error.</summary>
    public static JsAst ParseOrThrow(string source) => Parse(source).ThrowIfErrors();

    // ---- pooling -----------------------------------------------------------------------------------

    [ThreadStatic] private static JsParser? t_cached;

    private static JsParser Rent()
    {
        var p = t_cached;
        if (p is null) return new JsParser();
        t_cached = null;
        return p;
    }

    private static void Return(JsParser parser)
    {
        parser.Reset();
        t_cached = parser;
    }

    private JsParser() { }

    // ---- state -------------------------------------------------------------------------------------

    private JsAst _ast = null!;
    private string _source = "";
    private List<ParseError> _errors = new();

    // Source: pinned for the duration of the parse. Strings are NUL-terminated, so _src[_len] == '\0'
    // acts as a sentinel that ends every scanning loop without explicit bounds checks.
    private char* _src;
    private int _len;
    private int _pos;

    // Current token.
    private TokenKind _kind;
    private bool _newLine;
    private bool _hasEscape;
    private int _tokStart;
    private int _tokEnd;
    private int _tokKnown;      // KnownStrings id for keyword-like names, else 0
    private string? _escaped;   // escape-processed text of the current token (when _hasEscape)
    private bool _numExact;     // the current Number token's value is in _numValue
    private double _numValue;
    private bool _templateEscapesValid; // the last cooked template chunk had only escapes valid in templates

    // One-token lookahead cache (go-fAST re-scans on every peek).
    private bool _peekValid;
    private TokenKind _peekKind;
    private bool _peekNewLine;
    private bool _peekHasEscape;
    private int _peekStart;
    private int _peekEnd;
    private int _peekKnown;
    private string? _peekEscaped;
    private bool _peekNumExact;
    private double _peekNumValue;
    private int _peekPos;

    // Error recovery bookkeeping (see NextStatement).
    private int _recoverIdx;
    private int _recoverCount;

    // Scratch stack used to build every node list without intermediate allocations.
    private int[] _scratch = new int[256];
    private int _scratchTop;

    // Escape-processing buffer.
    private char[] _buf = ArrayPool<char>.Shared.Rent(256);
    private int _bufLen;

    // Scope state (go-fAST's *scope chain, flattened into fields plus a stack).
    private ScopeState _scope;
    private ScopeState[] _scopeStack = new ScopeState[32];
    private int _scopeDepth;

    // Label stack: (start, length) source slices of the active labels.
    private (int Start, int Length)[] _labels = new (int, int)[16];
    private int _labelCount;

    // Stack-overflow protection: below this address the real remaining stack is checked (see StackIsLow).
    private nint _stackLimit;

    // How much deeper the stack may grow between real checks. TryEnsureSufficientExecutionStack
    // guarantees 128 KB of headroom on 64-bit, so 64 KB steps can never overshoot.
    private const int StackCheckInterval = 64 * 1024;

    private struct ScopeState
    {
        public bool AllowIn;
        public bool AllowLet;
        public bool InIteration;
        public bool InSwitch;
        public bool InFuncParams;
        public bool InFunction;
        public bool InAsync;
        public bool AllowAwait;
        public bool AllowYield;
        public int LabelBase;
    }

    /// <summary>Test hook: forces tiny initial arena capacities so growth paths are exercised.</summary>
    [ThreadStatic] internal static int t_capacityOverride;

    private JsAst Run(string source)
    {
        _source = source;
        _len = source.Length;
        _errors = new List<ParseError>();

        // Capacity heuristics measured on the benchmark files: formatted code has ~0.11-0.16 nodes per
        // char, minified code up to ~0.37. Estimate from the whitespace density of a prefix so large
        // files neither over-allocate by 3x nor pay for repeated growth copies. Strings are lazy (almost
        // nothing is interned while parsing), so the string table starts small.
        int nodeCap = EstimateNodeCount(source);
        int listCap = Math.Max(64, nodeCap / 3);
        int stringCap = 256;
        if (t_capacityOverride > 0) nodeCap = listCap = stringCap = t_capacityOverride;
        _ast = new JsAst(source, nodeCap, listCap, stringCap);

        byte probe;
        _stackLimit = (nint)(&probe) - StackCheckInterval;

        fixed (char* src = source)
        {
            _src = src;
            _pos = 0;
            OpenScope();
            Next();
            ListRange body = ParseSourceElements();
            CloseScope();
            _ast._root = _ast.CreateProgram(body);
            _src = null;
        }

        var ast = _ast;
        ast.SetErrors(_errors);
        return ast;
    }

    /// <summary>
    /// Estimates the node count from punctuation density: across real code there are ~1.65-1.87 nodes per
    /// <c>( , . ; = : { [</c> character. Four windows spread through the file keep large data blobs
    /// (base64 strings, lookup tables) from skewing the estimate; the 2.2 factor errs on the high side
    /// because a growth copy costs more than unused (never-touched) capacity.
    /// </summary>
    private static int EstimateNodeCount(string source)
    {
        const int Window = 8 * 1024;
        if (source.Length < 4 * Window) return Math.Max(64, source.Length / 2);
        long punct = 0;
        for (int w = 0; w < 4; w++)
        {
            var span = source.AsSpan((int)((long)(source.Length - Window) * w / 3), Window);
            punct += span.Count('(') + span.Count(',') + span.Count('.') + span.Count(';')
                   + span.Count('=') + span.Count(':') + span.Count('{') + span.Count('[');
        }
        double density = punct / (4.0 * Window);
        return (int)Math.Min(int.MaxValue / 64, source.Length * density * 2.2) + 256;
    }

    private void Reset()
    {
        _ast = null!;
        _source = "";
        _src = null;
        _len = _pos = 0;
        _escaped = null;
        _peekEscaped = null;
        _peekValid = false;
        _recoverIdx = _recoverCount = 0;
        _scratchTop = 0;
        _scopeDepth = 0;
        _labelCount = 0;
        if (_scratch.Length > 1 << 16) _scratch = new int[256];
        if (_buf.Length > 1 << 16)
        {
            ArrayPool<char>.Shared.Return(_buf);
            _buf = ArrayPool<char>.Shared.Rent(256);
        }
    }

    // ---- errors ------------------------------------------------------------------------------------

    private void ErrorAt(int start, int end, string message) => _errors.Add(new ParseError(message, start, end));

    /// <summary>Records an error at the current token (go-fAST's <c>errorf</c>).</summary>
    private void Error(string message) => ErrorAt(_tokStart, _tokStart, message);

    private void ErrorUnexpectedToken(TokenKind kind)
    {
        switch (kind)
        {
            case TokenKind.Eof: Error("Unexpected end of input"); return;
            case TokenKind.Identifier: Error("Unexpected identifier"); return;
            case TokenKind.Keyword: Error("Unexpected reserved word"); return;
            case TokenKind.EscapedReservedWord: Error("Keyword must not contain escaped characters"); return;
            case TokenKind.Number: Error("Unexpected number"); return;
            case TokenKind.String: Error("Unexpected string"); return;
        }
        Error("Unexpected token " + TokenKinds.ToText(kind));
    }

    // ---- checkpoints -------------------------------------------------------------------------------

    private struct Checkpoint
    {
        public int Pos;
        public TokenKind Kind;
        public bool NewLine;
        public bool HasEscape;
        public int Start;
        public int End;
        public int Known;
        public string? Escaped;
        public bool NumExact;
        public double NumValue;
        public int ErrorCount;
    }

    private Checkpoint Mark() => new()
    {
        Pos = _pos,
        Kind = _kind,
        NewLine = _newLine,
        HasEscape = _hasEscape,
        Start = _tokStart,
        End = _tokEnd,
        Known = _tokKnown,
        Escaped = _escaped,
        NumExact = _numExact,
        NumValue = _numValue,
        ErrorCount = _errors.Count,
    };

    private void Restore(in Checkpoint c)
    {
        _pos = c.Pos;
        _kind = c.Kind;
        _newLine = c.NewLine;
        _hasEscape = c.HasEscape;
        _tokStart = c.Start;
        _tokEnd = c.End;
        _tokKnown = c.Known;
        _escaped = c.Escaped;
        _numExact = c.NumExact;
        _numValue = c.NumValue;
        _peekValid = false;
        if (_errors.Count > c.ErrorCount)
            _errors.RemoveRange(c.ErrorCount, _errors.Count - c.ErrorCount);
    }

    // ---- token helpers -----------------------------------------------------------------------------

    /// <summary>Advances to the next token.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Next()
    {
        if (_peekValid)
            ApplyPeek();
        else
            Scan();
    }

    private void ApplyPeek()
    {
        _peekValid = false;
        _kind = _peekKind;
        _newLine = _peekNewLine;
        _hasEscape = _peekHasEscape;
        _tokStart = _peekStart;
        _tokEnd = _peekEnd;
        _tokKnown = _peekKnown;
        _escaped = _peekEscaped;
        _numExact = _peekNumExact;
        _numValue = _peekNumValue;
        _pos = _peekPos;
    }

    /// <summary>Returns the kind of the token after the current one, without consuming anything.</summary>
    /// <remarks>Out of line: its checkpoint holds a string, so inlined it would add stack zeroing to the caller's prologue.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private TokenKind Peek()
    {
        if (_peekValid) return _peekKind;

        var saved = Mark();
        Scan();
        TokenKind kind = _kind;
        if (_errors.Count == saved.ErrorCount)
        {
            // Cache the token so the following Next() does not scan it again.
            _peekKind = _kind;
            _peekNewLine = _newLine;
            _peekHasEscape = _hasEscape;
            _peekStart = _tokStart;
            _peekEnd = _tokEnd;
            _peekKnown = _tokKnown;
            _peekEscaped = _escaped;
            _peekNumExact = _numExact;
            _peekNumValue = _numValue;
            _peekPos = _pos;
            Restore(saved);
            _peekValid = true;
        }
        else
        {
            Restore(saved);
        }
        return kind;
    }

    private bool CanInsertSemicolon() =>
        _kind is TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.Eof || _newLine;

    private bool Semicolon()
    {
        if (!CanInsertSemicolon()) return false;
        if (_kind == TokenKind.Semicolon) Next();
        return true;
    }

    private void RequireSemicolon()
    {
        if (!Semicolon()) ErrorUnexpectedToken(_kind);
    }

    /// <summary>Consumes the current token, which must be <paramref name="kind"/>, returning its start offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Expect(TokenKind kind)
    {
        int idx = _tokStart;
        if (_kind != kind) ErrorUnexpectedToken(_kind);
        Next();
        return idx;
    }

    // ---- scratch lists -----------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(int id)
    {
        if (_scratchTop == _scratch.Length) GrowScratch();
        _scratch[_scratchTop++] = id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowScratch() => Array.Resize(ref _scratch, _scratch.Length * 2);

    /// <summary>Copies scratch[mark..top] into the arena's list pool and pops it.</summary>
    /// <remarks>
    /// Most lists have one to a few elements, so short ones are copied inline rather than through memmove;
    /// no span temporaries are involved (the JIT would zero-initialize them in every caller's prologue).
    /// </remarks>
    private ListRange FinishList(int mark)
    {
        int count = _scratchTop - mark;
        if (count <= 0) return default;
        int start = _ast.AllocList(count); // guarantees start + count <= _lists.Length
        ref int dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_ast._lists), (nint)(uint)start);
        ref int src = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_scratch), (nint)(uint)mark); // mark + count == _scratchTop
        if (count <= 8)
        {
            for (int i = 0; i < count; i++) Unsafe.Add(ref dst, i) = Unsafe.Add(ref src, i);
        }
        else
        {
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<int, byte>(ref dst), ref Unsafe.As<int, byte>(ref src), (uint)count * sizeof(int));
        }
        _scratchTop = mark;
        return new ListRange(start, count);
    }

    // ---- scopes ------------------------------------------------------------------------------------

    private void OpenScope()
    {
        if (_scopeDepth == _scopeStack.Length) Array.Resize(ref _scopeStack, _scopeStack.Length * 2);
        _scopeStack[_scopeDepth++] = _scope;
        _scope = new ScopeState { AllowIn = true, LabelBase = _labelCount };
    }

    private void CloseScope() => _scope = _scopeStack[--_scopeDepth];

    private bool HasLabel(int start, int length)
    {
        var name = new ReadOnlySpan<char>(_src + start, length);
        for (int i = _scope.LabelBase; i < _labelCount; i++)
        {
            var (s, l) = _labels[i];
            if (l == length && name.SequenceEqual(new ReadOnlySpan<char>(_src + s, l))) return true;
        }
        return false;
    }

    // ---- node helpers ------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NodeKind K(int id) => _ast._nodes[id].Kind;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref NodeRecord N(int id) => ref _ast._nodes[id];

    private int Invalid(int from, int to) => _ast.CreateInvalidExpression(from, to);

    /// <summary>Creates an Identifier node for the current (identifier-like) token.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CurrentIdentifier()
    {
        int name = _hasEscape ? _ast.Intern(_escaped!) : _tokKnown;
        return _ast.CreateIdentifier(name, ScopeContext.Unresolved, _tokStart, _tokEnd - _tokStart);
    }

    /// <summary>
    /// Guards deep recursion with a single pointer compare: only when the stack has grown another
    /// <see cref="StackCheckInterval"/> bytes since the last check is the real remaining stack examined.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool StackIsLow()
    {
        byte probe;
        return (nint)(&probe) < _stackLimit && StackIsReallyLow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool StackIsReallyLow()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
        byte probe;
        _stackLimit = (nint)(&probe) - StackCheckInterval;
        return false;
    }

    /// <summary>Continues a deeply nested parse on a fresh thread with a large stack.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private T OnNewStack<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        nint savedLimit = _stackLimit;
        var thread = new Thread(() =>
        {
            byte probe;
            _stackLimit = (nint)(&probe) - StackCheckInterval;
            try { result = work(); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        _stackLimit = savedLimit;
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}

public sealed unsafe partial class JsParser
{
    /// <summary>Benchmark hook: runs only the scanner over <paramref name="source"/> and returns the token count.</summary>
    internal static int TokenizeOnly(string source)
    {
        var parser = Rent();
        try
        {
            parser._source = source;
            parser._len = source.Length;
            parser._errors = new List<ParseError>();
            parser._ast = new JsAst(source, 16, 16, 256);
            int count = 0;
            fixed (char* src = source)
            {
                parser._src = src;
                parser._pos = 0;
                do
                {
                    parser.Scan();
                    count++;
                } while (parser._kind != TokenKind.Eof);
                parser._src = null;
            }
            return count;
        }
        finally
        {
            Return(parser);
        }
    }
}
