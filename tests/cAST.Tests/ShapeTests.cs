using cAST.Matching;
using static cAST.Tests.H;

namespace cAST.Tests;

/// <summary>The Js shape tests: each recognises one form and hands out its parts.</summary>
public class ShapeTests
{
    private static Expression E(JsAst ast, int i) => ExprOf(Stmt(ast, i));

    [Fact]
    public void AssignStatementIsPlainAssignmentToAnExpression()
    {
        using var ast = MustParse("a.b = c; x += 1; [p] = q; f();");
        Assert.True(Stmt(ast, 0).IsAssignStatement(out var target, out var value));
        Assert.True(target.IsMember(out var owner, out var key));
        Assert.True(owner.IsIdentifierNamed("a"));
        Assert.Equal("b", key);
        Assert.True(value.IsIdentifierNamed("c"));
        Assert.False(Stmt(ast, 1).IsAssignStatement(out _, out _));
        Assert.False(Stmt(ast, 2).IsAssignStatement(out _, out _));
        Assert.False(Stmt(ast, 3).IsAssignStatement(out _, out _));
        Assert.True(E(ast, 0).IsAssign(out target, out _));
        Assert.Equal(NodeKind.MemberExpression, target.Kind);
        Assert.False(E(ast, 2).IsAssign(out _, out _));
    }

    [Fact]
    public void CallStatement()
    {
        using var ast = MustParse("f(1); new F(); a;");
        Assert.True(Stmt(ast, 0).IsCallStatement(out var call));
        Assert.Single(call.ArgumentList);
        Assert.False(Stmt(ast, 1).IsCallStatement(out _));
        Assert.False(Stmt(ast, 2).IsCallStatement(out _));
    }

    [Fact]
    public void PlainBreakHasNoLabel()
    {
        using var ast = MustParse("for (;;) { break; } l: for (;;) { break l; }");
        Assert.True(Stmt(ast, 0).AsForStatement().Body.AsBlockStatement().List[0].IsPlainBreak());
        Assert.False(Stmt(ast, 1).AsLabelledStatement().Statement.AsForStatement().Body.AsBlockStatement().List[0].IsPlainBreak());
        Assert.False(Stmt(ast, 0).IsPlainBreak());
    }

    [Fact]
    public void ReturnOfNeedsAnArgument()
    {
        using var ast = MustParse("function f() { return x; } function g() { return; }");
        Assert.True(BodyOf(Stmt(ast, 0)).List[0].IsReturnOf(out var returned));
        Assert.True(returned.IsIdentifierNamed("x"));
        Assert.False(BodyOf(Stmt(ast, 1)).List[0].IsReturnOf(out _));
    }

    [Fact]
    public void CallOfANamedFunction()
    {
        using var ast = MustParse("f(1, 2); g(); a.f();");
        Assert.True(E(ast, 0).IsCall("f", out var args));
        Assert.Equal(2, args.Count);
        Assert.False(E(ast, 1).IsCall("f", out _));
        Assert.False(E(ast, 2).IsCall("f", out _));
    }

    [Fact]
    public void MethodCallByDotOrStringKey()
    {
        using var ast = MustParse("a.m(1); a['m'](); a[m](); m(); a.n();");
        Assert.True(E(ast, 0).IsMethodCall("m", out var owner, out var args));
        Assert.True(owner.IsIdentifierNamed("a"));
        Assert.Single(args);
        Assert.True(E(ast, 1).IsMethodCall("m", out owner, out args));
        Assert.True(owner.IsIdentifierNamed("a"));
        Assert.Empty(args);
        Assert.False(E(ast, 2).IsMethodCall("m", out _, out _));
        Assert.False(E(ast, 3).IsMethodCall("m", out _, out _));
        Assert.False(E(ast, 4).IsMethodCall("m", out _, out _));
    }

    [Fact]
    public void MemberAndElement()
    {
        using var ast = MustParse("a.b; a['b']; a[0]; a[b];");
        Assert.True(E(ast, 0).IsMember(out var owner, out var key));
        Assert.True(owner.IsIdentifierNamed("a"));
        Assert.Equal("b", key);
        Assert.True(E(ast, 1).IsMember(out _, out key));
        Assert.Equal("b", key);
        Assert.False(E(ast, 2).IsMember(out _, out _));
        Assert.False(E(ast, 3).IsMember(out _, out _));

        Assert.False(E(ast, 0).IsElement(out _, out _));
        Assert.True(E(ast, 1).IsElement(out _, out var index));
        Assert.True(index.IsString(out _));
        Assert.True(E(ast, 2).IsElement(out owner, out index));
        Assert.True(owner.IsIdentifierNamed("a"));
        Assert.True(index.IsInt32(out int value));
        Assert.Equal(0, value);
        Assert.True(E(ast, 3).IsElement(out _, out index));
        Assert.True(index.IsIdentifierNamed("b"));
    }

    [Fact]
    public void BinaryAndUnaryByOperator()
    {
        using var ast = MustParse("a + b; a - b; !a; -a;");
        Assert.True(E(ast, 0).IsBinary(BinaryOperator.Addition, out var left, out var right));
        Assert.True(left.IsIdentifierNamed("a"));
        Assert.True(right.IsIdentifierNamed("b"));
        Assert.False(E(ast, 1).IsBinary(BinaryOperator.Addition, out _, out _));
        Assert.True(E(ast, 2).IsUnary(UnaryOperator.LogicalNot, out var operand));
        Assert.True(operand.IsIdentifierNamed("a"));
        Assert.False(E(ast, 3).IsUnary(UnaryOperator.LogicalNot, out _));
        Assert.False(E(ast, 0).IsUnary(UnaryOperator.LogicalNot, out _));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("-7", -7)]
    [InlineData("0x10", 16)]
    [InlineData("1e3", 1000)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("2147483647", int.MaxValue)]
    public void Int32Literals(string code, int expected)
    {
        using var ast = MustParse(code + ";");
        Assert.True(E(ast, 0).IsInt32(out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("'5'")]
    [InlineData("-x")]
    [InlineData("- -1")]
    [InlineData("+1")]
    public void NotInt32Literals(string code)
    {
        using var ast = MustParse(code + ";");
        Assert.False(E(ast, 0).IsInt32(out _));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("1e3", 1000)]
    [InlineData("2147483647", int.MaxValue)]
    public void WholeNumbers(string code, int expected)
    {
        using var ast = MustParse(code + ";");
        Assert.True(E(ast, 0).IsWholeNumber(out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("'5'")]
    [InlineData("x")]
    public void NotWholeNumbers(string code)
    {
        using var ast = MustParse(code + ";");
        Assert.False(E(ast, 0).IsWholeNumber(out _));
    }

    [Theory]
    [InlineData("x = 1;", true)]
    [InlineData("var x = 1;", true)]
    [InlineData("let x;", true)]
    [InlineData("o.x = 1;", false)]
    [InlineData("x += 1;", false)]
    [InlineData("var x = 1, y = 2;", false)]
    [InlineData("[x] = a;", false)]
    public void NameWrites(string code, bool written)
    {
        using var ast = MustParse(code);
        Assert.Equal(written, Stmt(ast, 0).IsNameWrite(out var name, out _));
        if (written)
            Assert.Equal("x", name.Name);
    }

    [Fact]
    public void NameAssignmentsAndDeclarations()
    {
        using var ast = MustParse("x = y; var z = 1; let w; x += 1;");
        Assert.True(Stmt(ast, 0).IsNameAssignment(out var name, out var value));
        Assert.Equal("x", name.Name);
        Assert.True(value.IsIdentifierNamed("y"));
        Assert.True(E(ast, 0).IsNameAssign(out name, out _));
        Assert.False(Stmt(ast, 1).IsNameAssignment(out _, out _));
        Assert.True(Stmt(ast, 1).IsNameDeclaration(out name, out value));
        Assert.Equal("z", name.Name);
        Assert.True(value.IsInt32(out _));
        Assert.True(Stmt(ast, 2).IsNameDeclaration(out _, out value));
        Assert.True(value.IsNone);
        Assert.False(Stmt(ast, 3).IsNameAssignment(out _, out _));
    }

    [Fact]
    public void LastOfASequence()
    {
        using var ast = MustParse("(a, b); (a, (b, c)); d;");
        Assert.True(E(ast, 0).Last().IsIdentifierNamed("b"));
        Assert.True(E(ast, 1).Last().IsIdentifierNamed("c"));
        Assert.True(E(ast, 2).Last().IsIdentifierNamed("d"));
    }

    [Fact]
    public void StringsAndVoidZero()
    {
        using var ast = MustParse("'a'; \"b\\u0063\"; 1; void 0; void 1; void x;");
        Assert.True(E(ast, 0).IsString(out var s));
        Assert.Equal("a", s);
        Assert.True(E(ast, 1).IsString(out s));
        Assert.Equal("bc", s);
        Assert.False(E(ast, 2).IsString(out _));
        Assert.True(E(ast, 3).IsVoidZero());
        Assert.False(E(ast, 4).IsVoidZero());
        Assert.False(E(ast, 5).IsVoidZero());
        Assert.False(E(ast, 0).IsVoidZero());
    }

    [Fact]
    public void SplitOfAStringLiteral()
    {
        using var ast = MustParse("'a|b|c'.split('|'); x.split('|'); 'a'.split(); 'a,b'['split'](','); 'a'.split('|', 2);");
        Assert.True(E(ast, 0).IsSplit(out var text, out var separator));
        Assert.Equal("a|b|c", text);
        Assert.Equal("|", separator);
        Assert.False(E(ast, 1).IsSplit(out _, out _));
        Assert.False(E(ast, 2).IsSplit(out _, out _));
        Assert.True(E(ast, 3).IsSplit(out text, out separator));
        Assert.Equal(("a,b", ","), (text, separator));
        Assert.False(E(ast, 4).IsSplit(out _, out _));
    }

    [Fact]
    public void IdentifierNamed()
    {
        using var ast = MustParse("abc; abd; 'abc';");
        Assert.True(E(ast, 0).IsIdentifierNamed("abc"));
        Assert.False(E(ast, 1).IsIdentifierNamed("abc"));
        Assert.False(E(ast, 2).IsIdentifierNamed("abc"));
    }

    [Fact]
    public void FunctionThatOnlyReturns()
    {
        using var ast = MustParse("(function (a) { return a + 1; }); (function () { a(); return 1; }); (function () { return; });");
        Assert.True(E(ast, 0).AsFunctionLiteral().ReturnsOnly(out var returned));
        Assert.True(returned.IsBinary(BinaryOperator.Addition, out _, out _));
        Assert.False(E(ast, 1).AsFunctionLiteral().ReturnsOnly(out _));
        Assert.False(E(ast, 2).AsFunctionLiteral().ReturnsOnly(out _));
    }

    [Fact]
    public void SameBindingNeedsResolution()
    {
        using var ast = MustParse("var x = 1; function f(x) { return x; } x;");
        var x = ast.Program.Descendants(NodeKind.Identifier).Select(n => n.AsIdentifier()).Where(i => i.Name == "x").ToList();
        Assert.Throws<InvalidOperationException>(() => x[0].SameBinding(x[3]));
        JsResolver.Resolve(ast);
        Assert.True(x[0].SameBinding(x[3]));
        Assert.True(x[1].SameBinding(x[2]));
        Assert.False(x[0].SameBinding(x[1]));
    }
}
