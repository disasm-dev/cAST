using static cAST.Tests.H;

namespace cAST.Tests;

/// <summary>Identifier.BindingNumber: equal exactly when identifiers the resolver gave numbers refer to the same binding.</summary>
public class BindingNumberTests
{
    private sealed class Identifiers : AstVisitor
    {
        public readonly List<Identifier> Items = [];
        public override void VisitIdentifier(Identifier node) => Items.Add(node);

        public static List<Identifier> Of(JsAst ast)
        {
            var c = new Identifiers();
            c.VisitProgram(ast.Program);
            return c.Items;
        }

        public static List<Identifier> Named(JsAst ast, string name) => Of(ast).Where(i => i.Name == name).ToList();
    }

    [Fact]
    public void EqualExactlyForTheSameBinding()
    {
        using var ast = MustParse("var x = 1; function f(x) { return x; } x;");
        JsResolver.Resolve(ast);
        var x = Identifiers.Named(ast, "x"); // var x, parameter x, return x, x
        Assert.Equal(x[0].BindingNumber, x[3].BindingNumber);
        Assert.Equal(x[1].BindingNumber, x[2].BindingNumber);
        Assert.NotEqual(x[0].BindingNumber, x[1].BindingNumber);
    }

    [Theory]
    [InlineData("var a; { let a; a; } a; function g(b) { var a = b; try { a(); } catch (a) { a; } return a; }")]
    [InlineData("class C { m() { return C; } } const k = class K { n() { return K; } }; for (let i = 0; i < 2; i++) { i; } for (var j in o) j;")]
    [InlineData("function f() { arguments; var arguments; } switch (s) { case 1: let t = 1; t; break; default: t; } u; u = 2;")]
    public void AgreesWithBindingIds(string code)
    {
        using var ast = MustParse(code);
        JsResolver.Resolve(ast);
        var resolved = Identifiers.Of(ast).Where(i => i.ScopeContext != ScopeContext.Unresolved).ToList();
        foreach (var a in resolved)
        {
            Assert.InRange(a.BindingNumber, 0, ast.BindingNumbers - 1);
            foreach (var b in resolved)
                Assert.Equal(a.BindingId == b.BindingId, a.BindingNumber == b.BindingNumber);
        }
    }

    [Fact]
    public void ResolvingTheWholeProgramNumbersFromZeroAgain()
    {
        using var ast = MustParse("foo(); function g(p) { foo(p); }");
        JsResolver.Resolve(ast);
        int numbers = ast.BindingNumbers;
        int resolutions = ast.Resolutions;
        var first = Identifiers.Of(ast).Where(i => i.ScopeContext != ScopeContext.Unresolved).Select(i => i.BindingNumber).ToList();
        JsResolver.Resolve(ast);
        Assert.Equal(resolutions + 1, ast.Resolutions);
        Assert.Equal(numbers, ast.BindingNumbers);
        Assert.Equal(first, Identifiers.Of(ast).Where(i => i.ScopeContext != ScopeContext.Unresolved).Select(i => i.BindingNumber));
    }

    [Fact]
    public void ResolvingASubtreeKeepsTheNumbersOutsideItAndGivesNewOnesInside()
    {
        using var ast = MustParse("var a; x = function (p) { return a + p; }; a;");
        JsResolver.Resolve(ast);
        int numbers = ast.BindingNumbers;
        int a = Identifiers.Named(ast, "a")[0].BindingNumber;
        JsResolver.Resolve(ExprOf(Stmt(ast, 1)).AsAssignExpression().Right);
        Assert.Equal(a, Identifiers.Named(ast, "a")[1].BindingNumber);
        Assert.InRange(Identifiers.Named(ast, "p")[0].BindingNumber, numbers, ast.BindingNumbers - 1);
        var resolved = Identifiers.Of(ast).Where(i => i.ScopeContext != ScopeContext.Unresolved).ToList();
        foreach (var i in resolved)
        {
            foreach (var j in resolved)
                Assert.Equal(i.BindingId == j.BindingId, i.BindingNumber == j.BindingNumber);
        }
    }

    [Fact]
    public void OnlyTheResolverGivesNumbers()
    {
        using var ast = MustParse("function f() { var o; o; }");
        var o = Identifiers.Named(ast, "o");
        Assert.Throws<InvalidOperationException>(() => o[0].BindingNumber); // not resolved yet
        JsResolver.Resolve(ast);
        Assert.Equal(o[0].BindingNumber, o[1].BindingNumber);
        var read = o[1];
        read.ScopeContext = ScopeContext.TopLevel; // set by hand since
        Assert.Throws<InvalidOperationException>(() => read.BindingNumber);
        Assert.Throws<InvalidOperationException>(() => ast.NewIdentifier("o", ScopeContext.TopLevel).BindingNumber);
    }
}
