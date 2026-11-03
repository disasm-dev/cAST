using cAST.Matching;
using static cAST.Tests.H;

namespace cAST.Tests;

/// <summary>Descendants, OwnDescendants, Any and First: pre-order walks read straight off the node records.</summary>
public class TraversalTests
{
    private const string Sample = """
        function f({a: b = init()}, ...rest) {
            let [x,,y] = rest;
            for (let item of rest) { if (item) use(item.key, item[x]); }
            return () => ({[x]: y, b, ...rest});
        }
        class C extends Parent { [key] = value; method(p) { return p + outer; } }
        try { f(input); } catch ({message}) { report(message); }
        tag`a${b}c${d}`;
        """;

    private sealed class Trace : AstVisitor
    {
        public readonly List<string> Items = [];
        public override void VisitIdentifier(Identifier node) => Items.Add(node.Name);
        public override void VisitCallExpression(CallExpression node)
        {
            Items.Add("call");
            base.VisitCallExpression(node);
        }
    }

    [Fact]
    public void DescendantsAreInPreOrder()
    {
        using var ast = MustParse("a + b * c; f(x);");
        var expected = new[]
        {
            NodeKind.ExpressionStatement, NodeKind.BinaryExpression, NodeKind.Identifier, NodeKind.BinaryExpression,
            NodeKind.Identifier, NodeKind.Identifier, NodeKind.ExpressionStatement, NodeKind.CallExpression,
            NodeKind.Identifier, NodeKind.Identifier,
        };
        Assert.Equal(expected, ast.Program.Descendants().Select(n => n.Kind));
    }

    [Fact]
    public void DescendantsFollowTheVisitorsOrder()
    {
        using var ast = MustParse(Sample);
        var trace = new Trace();
        trace.VisitProgram(ast.Program);
        var walked = new List<string>();
        foreach (var node in ast.Program.Descendants(NodeKind.Identifier, NodeKind.CallExpression))
            walked.Add(node.TryGetIdentifier(out var id) ? id.Name : "call");
        Assert.Equal(trace.Items, walked);
    }

    [Fact]
    public void DescendantsLeaveOutTheStartAndEnterFunctions()
    {
        using var ast = MustParse("function f() { return function () { return g(); }; }");
        var function = Stmt(ast, 0).AsFunctionDeclaration().Function;
        Assert.DoesNotContain(function.AsNode(), function.Descendants());
        Assert.Equal(new[] { NodeKind.FunctionLiteral, NodeKind.CallExpression }, function.Descendants(NodeKind.FunctionLiteral, NodeKind.CallExpression).Select(n => n.Kind));
        Assert.Empty(default(Expression).Descendants());
    }

    [Fact]
    public void OwnDescendantsStopAtNestedFunctions()
    {
        using var ast = MustParse("""
            function outer(p) {
                own(p);
                var g = function () { inner1(); };
                var h = () => inner2();
                var o = { m() { inner3(); }, get k() { return inner4(); }, [own2()]: 1 };
                class K { method() { inner5(); } }
                return own3();
            }
            """);
        var outer = Stmt(ast, 0).AsFunctionDeclaration().Function;
        Assert.Equal(new[] { "own", "own2", "own3" }, outer.OwnDescendants(NodeKind.CallExpression).Select(Callee));
        Assert.Equal(
            new[] { "own", "inner1", "inner2", "inner3", "inner4", "own2", "inner5", "own3" },
            outer.Descendants(NodeKind.CallExpression).Select(Callee));
        Assert.Equal(5, outer.OwnDescendants(NodeKind.FunctionLiteral, NodeKind.ArrowFunctionLiteral).Count());
        // From the program, the declaration's function is yielded but not entered.
        Assert.Equal(new[] { NodeKind.FunctionDeclaration, NodeKind.FunctionLiteral }, ast.Program.OwnDescendants().Select(n => n.Kind));
    }

    private static string Callee(Node call) => call.AsCallExpression().Callee.AsIdentifier().Name;

    [Fact]
    public void AnyAndFirstFindTheFirstNodeOfTheKinds()
    {
        using var ast = MustParse("function f() { return g(); } h(); x = this;");
        var program = ast.Program;
        Assert.True(program.Any(NodeKind.ReturnStatement, enterFunctions: true));
        Assert.False(program.Any(NodeKind.ReturnStatement, enterFunctions: false));
        Assert.Equal("g", Callee(program.First(NodeKind.CallExpression, enterFunctions: true)));
        Assert.Equal("h", Callee(program.First(NodeKind.CallExpression, enterFunctions: false)));
        Assert.True(program.First(NodeKind.NewExpression, enterFunctions: true).IsNone);
        Assert.False(program.Any([NodeKind.NewExpression, NodeKind.SuperExpression], enterFunctions: true));
        Assert.Equal(NodeKind.AssignExpression, program.First([NodeKind.ThisExpression, NodeKind.AssignExpression], enterFunctions: false).Kind);
    }

    [Fact]
    public void WalkSeesChildrenReplacedBeforeItReachesThem()
    {
        using var ast = MustParse("f(a);");
        var names = new List<string>();
        foreach (var node in ast.Program.Descendants())
        {
            if (node.TryGetCallExpression(out var call))
            {
                var args = call.ArgumentList;
                args[0] = ast.NewIdentifier("b");
            }
            if (node.TryGetIdentifier(out var id)) names.Add(id.Name);
        }
        Assert.Equal(new[] { "f", "b" }, names);
    }

    [Fact]
    public void WalksDeepTreesWithoutRecursion()
    {
        const int depth = 50_000;
        using var ast = MustParse(string.Concat(Enumerable.Repeat("typeof ", depth)) + "leaf;");
        int count = 0;
        foreach (var _ in ast.Program.Descendants()) count++;
        Assert.Equal(depth + 2, count);
        Assert.True(ast.Program.Any(NodeKind.Identifier, enterFunctions: false));
    }

    [Fact]
    public void WalkingAllocatesNothing()
    {
        using var ast = MustParse(Sample);
        Walk(ast);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = Walk(ast);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(count > 50);

        static int Walk(JsAst ast)
        {
            int n = 0;
            foreach (var _ in ast.Program.Descendants()) n++;
            foreach (var _ in ast.Program.OwnDescendants(NodeKind.Identifier)) n++;
            if (ast.Program.Any(NodeKind.TemplateLiteral, enterFunctions: true)) n++;
            return n;
        }
    }
}
