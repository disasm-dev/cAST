namespace cAST.Resolution;

/// <summary>
/// Resolves identifier scopes: every <see cref="Identifier"/> that names a variable gets a <see cref="ScopeContext"/>
/// such that two identifiers with the same name share a context exactly when they refer to the same binding. A port
/// of go-fAST's resolver (hoisting pre-pass, global symbol table with an undo trail), using integer name ids instead
/// of string maps.
/// </summary>
/// <remarks>
/// Resolution can be run again at any time, for example after a transform added, moved or removed code: every
/// context is recomputed from the current tree (old contexts are cleared first, detached nodes included). Scopes get
/// numbers never used before in the arena, so a context copied from an earlier run cannot equal a current one.
/// References to names that no enclosing scope declares resolve to <see cref="ScopeContext.TopLevel"/>, the global
/// scope, like top-level declarations. Identifiers that do not name a variable (member property names, labels,
/// private names) are left <see cref="ScopeContext.Unresolved"/>.
/// </remarks>
public static class JsResolver
{
    /// <summary>Resolves (or re-resolves) every identifier of <paramref name="ast"/>.</summary>
    public static void Resolve(JsAst ast) => Resolve(ast.Program);

    /// <summary>
    /// Resolves the identifiers under <paramref name="node"/> as if it were a whole program: names it does not declare
    /// resolve to <see cref="ScopeContext.TopLevel"/>. Contexts outside <paramref name="node"/> are left unchanged.
    /// </summary>
    public static void Resolve(Node node)
    {
        if (node.IsNone) return;
        // One resolver for each arena, its tables kept from one run to the next.
        (node.Ast._resolver ??= new Resolver(node.Ast)).Run(node);
    }
}

internal enum DeclKind : byte
{
    Var = 0,
    Function = 1,
    Class = 2,
}

internal sealed class Resolver : AstVisitor
{
    private struct SymEntry
    {
        public ScopeContext Ctx; // Unresolved = not bound
        public DeclKind Kind;
        public int Number; // the binding's number (Identifier.BindingNumber)
    }

    private struct TrailEntry
    {
        public int Name;
        public SymEntry Prev;
    }

    private sealed class Scope
    {
        public Scope? Parent;
        public ScopeContext Ctx;
        public int TrailBase;
    }

    internal readonly JsAst Ast;
    private Scope? _current;
    private bool _binding; // identType: false = reference, true = binding
    internal DeclKind DeclKind;

    private SymEntry[] _symbols;
    private TrailEntry[] _trail = new TrailEntry[64];
    private int _trailCount;
    private readonly Stack<Scope> _scopePool = new();

    private readonly Binder _binder;
    internal readonly Hoister Hoister;

    internal Resolver(JsAst ast)
    {
        Ast = ast;
        _symbols = new SymEntry[Math.Max(ast.StringCount * 2, 64)];
        _binder = new Binder(this);
        Hoister = new Hoister(this);
    }

    internal void Run(Node node)
    {
        // What an earlier run left: the program scope's symbols and the scope itself.
        Array.Clear(_symbols);
        _trailCount = 0;
        if (_current is not null)
        {
            _current.Parent = null;
            _scopePool.Push(_current);
            _current = null;
        }
        _binding = false;
        DeclKind = default;

        // During the walk a non-zero context means "resolved in this run" (the hoister resolves declarations before
        // the walk reaches them), so contexts from before are cleared first; a fresh parse has none to clear.
        if (Ast._scopeContextsSet)
        {
            if (node.Kind == NodeKind.Program) Ast.ClearScopeContexts();
            else Ast.ClearScopeContexts(node.Id);
        }
        Ast._scopeContextsSet = true;
        PushScope(); // the root (global) scope: TopLevel
        node.Accept(this);
    }

    // ---- symbol table ------------------------------------------------------------------------------

    private ref SymEntry Sym(int name)
    {
        if (name >= _symbols.Length) Array.Resize(ref _symbols, Math.Max(name + 1, _symbols.Length * 2));
        return ref _symbols[name];
    }

    internal int NameOf(Identifier id) => Ast.IdentifierNameId(id.Id);

    private void PushScope()
    {
        var ctx = _current is null ? ScopeContext.TopLevel : Ast.NextScopeContext();
        var s = _scopePool.Count > 0 ? _scopePool.Pop() : new Scope();
        s.Parent = _current;
        s.Ctx = ctx;
        s.TrailBase = _trailCount;
        _current = s;
    }

    private void PopScope()
    {
        var s = _current!;
        if (s.Parent is null) return; // the root scope never pops

        for (int i = _trailCount - 1; i >= s.TrailBase; i--)
        {
            ref TrailEntry u = ref _trail[i];
            Sym(u.Name) = u.Prev;
        }
        _trailCount = s.TrailBase;

        _current = s.Parent;
        s.Parent = null;
        _scopePool.Push(s);
    }

    /// <summary>Binds <paramref name="name"/> in the current scope (last writer wins) and returns its context and number.</summary>
    private ScopeContext Declare(int name, DeclKind kind, out int number)
    {
        var ctx = _current!.Ctx;
        ref SymEntry e = ref Sym(name);
        if (e.Ctx == ctx)
        {
            e.Kind = kind; // redeclaration in the same scope
            number = e.Number;
            return ctx;
        }
        if (_current.Parent is not null)
        {
            if (_trailCount == _trail.Length) Array.Resize(ref _trail, _trail.Length * 2);
            _trail[_trailCount++] = new TrailEntry { Name = name, Prev = e };
        }
        e = ref Sym(name);
        e.Ctx = ctx;
        e.Kind = kind;
        // A global's number is the arena's for its name, whichever run gives it; any other binding's is new.
        e.Number = number = ctx == ScopeContext.TopLevel ? Ast.GlobalBindingNumber(name) : Ast.NextBindingNumber();
        return ctx;
    }

    internal bool Lookup(int name, out DeclKind kind, out ScopeContext ctx, out int number)
    {
        if (name < _symbols.Length)
        {
            ref SymEntry e = ref _symbols[name];
            if (e.Ctx != ScopeContext.Unresolved)
            {
                kind = e.Kind;
                ctx = e.Ctx;
                number = e.Number;
                return true;
            }
        }
        kind = default;
        ctx = ScopeContext.Unresolved;
        number = 0;
        return false;
    }

    /// <summary>Gives <paramref name="id"/> its binding's context and number.</summary>
    internal static void Bind(Identifier id, ScopeContext ctx, int number)
    {
        id.SetScopeContextUnflagged(ctx);
        id.SetBindingNumber(number, ctx);
    }

    /// <summary>Declares <paramref name="id"/> in the current scope, unless this run already resolved it.</summary>
    internal void Modify(Identifier id, DeclKind kind)
    {
        if (id.ScopeContext != ScopeContext.Unresolved) return;
        var ctx = Declare(NameOf(id), kind, out int number);
        Bind(id, ctx, number);
    }

    // ---- visitor -----------------------------------------------------------------------------------

    public override void VisitArrowFunctionLiteral(ArrowFunctionLiteral n)
    {
        PushScope();
        n.SetScopeContextUnflagged(_current!.Ctx);

        bool old = _binding;
        _binding = true;
        VisitParameterList(n.ParameterList);

        _binding = false;
        var body = n.Body;
        if (body.TryGetBlockStatement(out var block))
        {
            block.SetScopeContextUnflagged(_current.Ctx);
            block.VisitChildren(this); // reuse the arrow scope for the body
        }
        else
        {
            VisitExpression(body.AsExpression());
        }
        _binding = old;
        PopScope();
    }

    public override void VisitBlockStatement(BlockStatement n)
    {
        PushScope();
        n.SetScopeContextUnflagged(_current!.Ctx);
        n.VisitChildren(this);
        PopScope();
    }

    public override void VisitForInStatement(ForInStatement n) => VisitForInOf(n.Into, n.Source, n.Body);

    public override void VisitForOfStatement(ForOfStatement n) => VisitForInOf(n.Into, n.Source, n.Body);

    private void VisitForInOf(ForInto into, Expression source, Statement body)
    {
        PushScope();
        bool old = _binding;
        _binding = false;
        VisitForInto(into);
        VisitExpression(source);
        if (body.TryGetBlockStatement(out var block)) block.SetScopeContextUnflagged(_current!.Ctx);
        VisitStatement(body);
        _binding = old;
        PopScope();
    }

    public override void VisitForStatement(ForStatement n)
    {
        PushScope();
        bool old = _binding;
        // A declaration initializer binds its targets itself (VisitVariableDeclaration); an expression
        // initializer (for (i = 0; ...)) only references.
        _binding = false;
        var init = n.Initializer;
        if (!init.IsNone) VisitForInit(init);

        var test = n.Test;
        if (!test.IsNone) VisitExpression(test);
        var update = n.Update;
        if (!update.IsNone) VisitExpression(update);

        _binding = old;
        VisitStatement(n.Body);
        PopScope();
    }

    public override void VisitFunctionLiteral(FunctionLiteral n)
    {
        PushScope();
        var name = n.Name;
        if (!name.IsNone) Modify(name, DeclKind.Function);
        // Every non-arrow function has its own `arguments` (a parameter or var of that name is the same binding).
        Declare(KnownStrings.Arguments, DeclKind.Var, out _);

        n.SetScopeContextUnflagged(_current!.Ctx);

        bool old = _binding;
        _binding = true;
        VisitParameterList(n.ParameterList);

        _binding = false;
        var body = n.Body;
        body.SetScopeContextUnflagged(_current.Ctx);
        body.VisitChildren(this); // reuse the function scope for the body

        _binding = old;
        PopScope();
    }

    public override void VisitClassDeclaration(ClassDeclaration n)
    {
        var cls = n.Class;
        var name = cls.Name;
        if (!name.IsNone) Modify(name, DeclKind.Class);
        VisitClassLiteral(cls);
    }

    public override void VisitClassLiteral(ClassLiteral n)
    {
        // A class expression's name is bound inside the class only; a declaration's was resolved before.
        var name = n.Name;
        bool innerNameScope = !name.IsNone && name.ScopeContext == ScopeContext.Unresolved;
        if (innerNameScope)
        {
            PushScope();
            Modify(name, DeclKind.Class);
        }

        var superClass = n.SuperClass;
        if (!superClass.IsNone) VisitExpression(superClass);
        VisitClassElements(n.Body);

        if (innerNameScope) PopScope();
    }

    public override void VisitParameterList(ParameterList n)
    {
        // Pre-declare all parameter bindings first so a default can forward-reference a later parameter
        // (function f(a = b, b) {}); the binder skips defaults and computed keys.
        n.VisitChildren(_binder);

        // Then resolve those defaults and computed keys as references; bindings already have a context.
        bool old = _binding;
        _binding = false;
        n.VisitChildren(this);
        _binding = old;
    }

    public override void VisitCatchClause(CatchClause n)
    {
        PushScope();
        var parameter = n.Parameter;
        if (!parameter.IsNone)
        {
            bool old = _binding;
            _binding = true;
            VisitPattern(parameter);
            _binding = old;
        }
        var body = n.Body;
        body.SetScopeContextUnflagged(_current!.Ctx);
        body.VisitChildren(this);
        PopScope();
    }

    public override void VisitProgram(Program n) => n.VisitChildren(this); // Run pushed the root scope

    public override void VisitStatements(NodeList<Statement> list)
    {
        // Hoisting pre-pass (declares var and function bindings before references are resolved).
        Hoister.Reset();
        Hoister.VisitStatements(list);
        base.VisitStatements(list);
    }

    public override void VisitSwitchStatement(SwitchStatement n)
    {
        VisitExpression(n.Discriminant);
        // The case clauses form one block: a declaration in one case is in scope in all of them, so every case is
        // hoisted before any is resolved.
        PushScope();
        var cases = n.Body;
        for (int i = 0; i < cases.Count; i++)
        {
            Hoister.Reset();
            Hoister.VisitStatements(cases[i].Consequent);
        }
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var test = c.Test;
            if (!test.IsNone) VisitExpression(test);
            base.VisitStatements(c.Consequent); // already hoisted
        }
        PopScope();
    }

    public override void VisitVariableDeclaration(VariableDeclaration n)
    {
        var oldDecl = DeclKind;
        DeclKind = DeclKind.Var;
        bool old = _binding;
        var list = n.List;
        for (int i = 0; i < list.Count; i++)
        {
            var decl = list[i];
            _binding = true;
            VisitPattern(decl.Target);

            _binding = false;
            var init = decl.Initializer;
            if (!init.IsNone) VisitExpression(init);
        }
        _binding = old;
        DeclKind = oldDecl;
    }

    // Binding mode (_binding) is only ever switched on for a declaration target or catch parameter, i.e. while
    // walking a binding pattern. The only expressions inside a binding pattern are defaults, shorthand
    // initializers and computed keys, and those are references: they switch binding mode off. (Every other
    // expression is reached with binding mode already off, so expressions need no hook of their own.)

    public override void VisitAssignmentPattern(AssignmentPattern n)
    {
        VisitPattern(n.Left);
        bool old = _binding;
        _binding = false;
        VisitExpression(n.Right);
        _binding = old;
    }

    public override void VisitPatternShorthand(PatternShorthand n)
    {
        VisitIdentifier(n.Name);
        var initializer = n.Initializer;
        if (initializer.IsNone) return;
        bool old = _binding;
        _binding = false;
        VisitExpression(initializer);
        _binding = old;
    }

    public override void VisitComputedProperty(ComputedProperty n)
    {
        bool old = _binding;
        _binding = false;
        VisitExpression(n.Expr);
        _binding = old;
    }

    public override void VisitIdentifier(Identifier n)
    {
        if (n.ScopeContext != ScopeContext.Unresolved) return;
        if (_binding)
        {
            Modify(n, DeclKind);
            return;
        }
        // A name no enclosing scope declares is a global: the program scope.
        int name = NameOf(n);
        if (Lookup(name, out _, out var ctx, out int number))
            Bind(n, ctx, number);
        else
            Bind(n, ScopeContext.TopLevel, Ast.GlobalBindingNumber(name));
    }

    // ---- identifiers that do not name a variable are skipped (Run cleared any stale context) --------

    public override void VisitMemberProperty(MemberProperty n)
    {
        if (n.TryGetComputedProperty(out var computed)) VisitComputedProperty(computed);
    }

    public override void VisitPrivateIdentifier(PrivateIdentifier n) { }

    public override void VisitLabelledStatement(LabelledStatement n) => VisitStatement(n.Statement);

    public override void VisitBreakStatement(BreakStatement n) { }

    public override void VisitContinueStatement(ContinueStatement n) { }

    /// <summary>Declares each identifier in binding position of a pattern (skipping defaults/computed keys).</summary>
    private sealed class Binder(Resolver r) : AstVisitor
    {
        public override void VisitExpression(Expression node) { }

        public override void VisitIdentifier(Identifier n) => r.Modify(n, r.DeclKind);
    }
}

/// <summary>
/// The pre-pass that declares, before references are resolved, every var and (conditionally) function declaration in
/// the enclosing function/program scope and every let, const and class declaration in the scope of its own statement
/// list, so a use that textually precedes its declaration still resolves to it.
/// </summary>
internal sealed class Hoister : AstVisitor
{
    private readonly Resolver _r;
    private DeclKind _kind;
    private bool _inBlock;
    private bool _inCatchBody;
    private HashSet<int>? _excludedFromCatch;
    private readonly HashSet<int> _catchParamDecls = new();
    private readonly HoistBinder _binder;

    internal Hoister(Resolver r)
    {
        _r = r;
        _binder = new HoistBinder(this);
    }

    internal void Reset()
    {
        _kind = DeclKind.Var;
        _inBlock = false;
        _inCatchBody = false;
        _excludedFromCatch = null;
        _catchParamDecls.Clear();
    }

    private void AddIdentifier(Identifier id)
    {
        int name = _r.NameOf(id);
        if (_inCatchBody)
        {
            if (_catchParamDecls.Contains(name))
            {
                if (_r.Lookup(name, out _, out var ctx, out int number))
                {
                    Resolver.Bind(id, ctx, number);
                    return;
                }
            }
            (_excludedFromCatch ??= new HashSet<int>()).Add(name);
        }
        else if (_catchParamDecls.Contains(name))
        {
            if (_excludedFromCatch is null || !_excludedFromCatch.Contains(name)) return;
        }
        _r.Modify(id, _kind);
    }

    public override void VisitBlockStatement(BlockStatement n)
    {
        bool old = _inBlock;
        _inBlock = true;
        n.VisitChildren(this);
        _inBlock = old;
    }

    public override void VisitCatchClause(CatchClause n)
    {
        var oldExclude = _excludedFromCatch;
        _excludedFromCatch = null;
        bool oldInCatchBody = _inCatchBody;

        int paramName = 0;
        var parameter = n.Parameter;
        if (!parameter.IsNone && parameter.TryGetIdentifier(out var ident)) paramName = _r.NameOf(ident);

        bool hadParam = false;
        if (paramName != 0) hadParam = !_catchParamDecls.Add(paramName);

        _inCatchBody = true;
        VisitBlockStatement(n.Body);

        if (paramName != 0 && !hadParam) _catchParamDecls.Remove(paramName);
        _inCatchBody = oldInCatchBody;
        _excludedFromCatch = oldExclude;
    }

    public override void VisitStatements(NodeList<Statement> list)
    {
        // Declare var, function and class bindings before visiting any other statement.
        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            var s = list[i];
            switch (s.Kind)
            {
                case NodeKind.VariableDeclaration:
                    VisitVariableDeclaration(s.AsVariableDeclaration());
                    break;
                case NodeKind.FunctionDeclaration:
                    VisitFunctionDeclaration(s.AsFunctionDeclaration());
                    break;
                case NodeKind.ClassDeclaration when !_inBlock:
                    {
                        // Like let: bound in the scope of this statement list (a nested block binds its own).
                        var name = s.AsClassDeclaration().Class.Name;
                        if (!name.IsNone) _r.Modify(name, DeclKind.Class);
                        break;
                    }
            }
        }
        for (int i = 0; i < count; i++)
        {
            var s = list[i];
            if (s.Kind is not (NodeKind.VariableDeclaration or NodeKind.FunctionDeclaration or NodeKind.ClassDeclaration or NodeKind.None))
                VisitStatement(s);
        }
    }

    public override void VisitVariableDeclaration(VariableDeclaration n)
    {
        if (_inBlock && n.VarKind != VarKind.Var) return;
        var old = _kind;
        _kind = DeclKind.Var;
        n.VisitChildren(this);
        _kind = old;
    }

    public override void VisitPattern(Pattern node) => _binder.VisitPattern(node);

    public override void VisitForStatement(ForStatement n)
    {
        var init = n.Initializer;
        if (init.TryGetVariableDeclaration(out var decl) && decl.VarKind == VarKind.Var)
            VisitVariableDeclaration(decl);
        HoistBlockBody(n.Body);
    }

    public override void VisitForInStatement(ForInStatement n) => HoistForInOf(n.Into, n.Body);

    public override void VisitForOfStatement(ForOfStatement n) => HoistForInOf(n.Into, n.Body);

    private void HoistForInOf(ForInto into, Statement body)
    {
        if (into.TryGetVariableDeclaration(out var decl) && decl.VarKind == VarKind.Var)
            VisitVariableDeclaration(decl);
        HoistBlockBody(body);
    }

    private void HoistBlockBody(Statement body)
    {
        bool old = _inBlock;
        _inBlock = true;
        VisitStatement(body);
        _inBlock = old;
    }

    public override void VisitFunctionDeclaration(FunctionDeclaration n)
    {
        var name = n.Function.Name;
        if (name.IsNone) return;
        int nameId = _r.NameOf(name);
        if (_catchParamDecls.Contains(nameId)) return;

        if (_inBlock && _r.Lookup(nameId, out var kind, out _, out _) && kind != DeclKind.Var && kind != DeclKind.Function)
            return;

        _r.Modify(name, DeclKind.Function);
    }

    public override void VisitSwitchStatement(SwitchStatement n)
    {
        VisitExpression(n.Discriminant);
        bool old = _inBlock;
        _inBlock = true;
        VisitSwitchCases(n.Body);
        _inBlock = old;
    }

    public override void VisitArrowFunctionLiteral(ArrowFunctionLiteral n) { }

    public override void VisitExpression(Expression node) { }

    public override void VisitFunctionLiteral(FunctionLiteral n) { }

    /// <summary>Registers each binding identifier of a pattern for hoisting, skipping defaults and computed keys.</summary>
    private sealed class HoistBinder(Hoister h) : AstVisitor
    {
        public override void VisitExpression(Expression node) { }

        public override void VisitIdentifier(Identifier n) => h.AddIdentifier(n);
    }
}
