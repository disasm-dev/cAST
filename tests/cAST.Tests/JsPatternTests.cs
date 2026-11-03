using cAST.Matching;
using static cAST.Tests.H;

namespace cAST.Tests;

/// <summary>JsPattern: templates with $name, $name$ and $$ holes matched node for node.</summary>
public class JsPatternTests
{
    private static readonly JsPattern XorLoad = JsPattern.Parse("$t = $o[$k] ^ $n$;");

    [Fact]
    public void MatchesAStatementAndBindsItsHoles()
    {
        using var ast = MustParse("x = table[i + 1] ^ 0x5f;");
        Assert.True(XorLoad.Match(Stmt(ast, 0), out var b));
        Assert.True(b["t"].IsIdentifierNamed("x"));
        Assert.True(b["o"].IsIdentifierNamed("table"));
        Assert.True(b["k"].IsBinary(BinaryOperator.Addition, out _, out _));
        Assert.Equal(0x5f, b.Int("n"));
    }

    [Theory]
    [InlineData("x += t[i] ^ 1;")]
    [InlineData("x = t.i ^ 1;")]
    [InlineData("x = t[i] ^ y;")]
    [InlineData("x = t[i] | 1;")]
    [InlineData("x = t[i] ^ 1.5;")]
    [InlineData("x = t[i] ^ 1, y;")]
    [InlineData("[x] = t[i] ^ 1;")]
    public void FailsOnAnyDifference(string code)
    {
        using var ast = MustParse(code);
        Assert.False(XorLoad.Match(Stmt(ast, 0), out _));
    }

    [Fact]
    public void ComparesLiteralValuesNotTheirText()
    {
        var call = JsPattern.Parse("f('a', 16, 10n, /x/g, `t${$$}u`, true, null)");
        using var ast = MustParse("""
            f("a", 0x10, 0xan, /x/g, `t${1}u`, true, null);
            f('b', 16, 10n, /x/g, `t${1}u`, true, null);
            f('a', 17, 10n, /x/g, `t${1}u`, true, null);
            f('a', 16, 11n, /x/g, `t${1}u`, true, null);
            f('a', 16, 10n, /x/i, `t${1}u`, true, null);
            f('a', 16, 10n, /x/g, `t${1}v`, true, null);
            f('a', 16, 10n, /x/g, `t${1}u`, false, null);
            f('a', 16, 10n, /x/g, `t${1}u`, true, void 0);
            """);
        Assert.True(call.Match(E(ast, 0), out _));
        for (int i = 1; i < 8; i++)
            Assert.False(call.Match(E(ast, i), out _), $"statement {i}");
    }

    [Fact]
    public void ComparesOperatorsFlagsAndDeclarationKinds()
    {
        var pattern = JsPattern.Parse("var $a = $b++;");
        using var ast = MustParse("var p = q++; let p2 = q++; var p3 = ++q; var p4 = q--; var p5 = q++, r;");
        Assert.True(pattern.Match(Stmt(ast, 0), out var b));
        Assert.True(b["a"].IsIdentifierNamed("p"));
        for (int i = 1; i < 5; i++)
            Assert.False(pattern.Match(Stmt(ast, i), out _), $"statement {i}");
    }

    [Fact]
    public void ARepeatedHoleMatchesTheSameNameOrInteger()
    {
        var pattern = JsPattern.Parse("$a[$i] = $a[$i] ^ $k$ ^ $k$;");
        using var ast = MustParse("""
            s[j] = s[j] ^ 3 ^ 3;
            s[j] = t[j] ^ 3 ^ 3;
            s[j] = s[k] ^ 3 ^ 3;
            s[j] = s[j] ^ 3 ^ 4;
            s[0] = s[0] ^ 3 ^ 3;
            o.s[j] = o.s[j] ^ 3 ^ 3;
            """);
        Assert.True(pattern.Match(Stmt(ast, 0), out var b));
        Assert.True(b["a"].IsIdentifierNamed("s"));
        Assert.Equal(3, b.Int("k"));
        // A repeated $name matches identifiers of the same name only: not a literal or a member access, however equal.
        for (int i = 1; i < 6; i++)
            Assert.False(pattern.Match(Stmt(ast, i), out _), $"statement {i}");
    }

    [Fact]
    public void IntegerHolesMatchInt32LiteralsOnly()
    {
        var pattern = JsPattern.Parse("$x$ + $y$");
        using var ast = MustParse("1 + -2; 1 + 2.5; 1 + y; 1 + '2'; 1 + -0;");
        Assert.True(pattern.Match(E(ast, 0), out var b));
        Assert.Equal((1, -2), (b.Int("x"), b.Int("y")));
        for (int i = 1; i < 5; i++)
            Assert.False(pattern.Match(E(ast, i), out _), $"statement {i}");
    }

    [Fact]
    public void TheWildcardMatchesAnyExpressionAndBindsNothing()
    {
        var pattern = JsPattern.Parse("$$($$, $$);");
        using var ast = MustParse("f(1, g()); a.b(c, d); f(1); f(...a, b);");
        Assert.True(pattern.Match(Stmt(ast, 0), out var b));
        Assert.True(pattern.Match(Stmt(ast, 1), out _));
        Assert.False(pattern.Match(Stmt(ast, 2), out _));
        Assert.True(pattern.Match(Stmt(ast, 3), out _)); // a spread element is an expression
        Assert.Throws<KeyNotFoundException>(() => b["$"]);
    }

    [Fact]
    public void HolesMatchExpressionsOnly()
    {
        var member = JsPattern.Parse("$o.$p = $v");
        var assign = JsPattern.Parse("$t = $v");
        using var ast = MustParse("a.b = 1; a[b] = 1; ({a} = c);");
        Assert.True(member.Match(E(ast, 0), out var b));
        Assert.True(b["p"].IsIdentifierNamed("b"));
        Assert.False(member.Match(E(ast, 1), out _)); // a computed key is not an expression
        Assert.True(assign.Match(E(ast, 1), out _));
        Assert.False(assign.Match(E(ast, 2), out _)); // nor is a destructuring pattern
    }

    [Fact]
    public void MatchesARunOfStatements()
    {
        var pattern = JsPattern.Parse("var $a = $s.split('|'); var $i = 0;");
        using var ast = MustParse("f(); var parts = 'x|y'.split('|'); var n = 0; while (true) {}");
        var body = ast.Program.Body;
        Assert.False(pattern.Match(body, 0, out _));
        Assert.True(pattern.Match(body, 1, out var b));
        Assert.True(b["a"].IsIdentifierNamed("parts"));
        Assert.True(b["s"].IsString(out var text));
        Assert.Equal("x|y", text);
        Assert.True(b["i"].IsIdentifierNamed("n"));
        Assert.False(pattern.Match(body, 3, out _)); // one statement left, the template has two
        Assert.False(pattern.Match(body, 4, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => pattern.Match(body, 5, out _));
        Assert.Throws<InvalidOperationException>(() => pattern.Match(Stmt(ast, 1), out _));
    }

    [Fact]
    public void StatementTemplatesMayReturnBreakAndContinue()
    {
        var pattern = JsPattern.Parse("if ($c) { $r = $v; break; } else return $$;");
        using var ast = MustParse("""
            function f() {
                for (;;) {
                    if (a < b) { out = a; break; } else return 0;
                    if (a < b) { out = a; continue; } else return 0;
                    if (a < b) { out = a; break; f(); } else return 0;
                }
            }
            """);
        var loop = BodyOf(Stmt(ast, 0)).List[0].AsForStatement().Body.AsBlockStatement().List;
        Assert.True(pattern.Match(loop[0], out var b));
        Assert.True(b["c"].IsBinary(BinaryOperator.LessThan, out _, out _));
        Assert.False(pattern.Match(loop[1], out _));
        Assert.False(pattern.Match(loop[2], out _));
    }

    [Fact]
    public void BindingsReportMisuse()
    {
        using var ast = MustParse("x = t[i] ^ y;");
        Assert.False(XorLoad.Match(Stmt(ast, 0), out var b));
        Assert.True(b["k"].IsIdentifierNamed("i")); // bound before the match failed
        Assert.Throws<InvalidOperationException>(() => b.Int("n")); // the match failed before it
        Assert.Throws<InvalidOperationException>(() => b.Int("t")); // an expression hole
        Assert.Throws<InvalidOperationException>(() => b["n"]); // an integer hole
        Assert.Throws<KeyNotFoundException>(() => b["z"]);
        Assert.Throws<InvalidOperationException>(() => default(Bindings)["t"]);
        Assert.Throws<InvalidOperationException>(() => JsPattern.Parse("return $x;").Match(E(ast, 0), out _)); // not an expression
    }

    [Theory]
    [InlineData("")]
    [InlineData("$a$b$;")]
    [InlineData("$$$;")]
    [InlineData("$a + $a$;")]
    [InlineData("$a+$b+$c+$d+$e+$f+$g+$h+$i+$j+$k+$l+$m+$n+$o+$p+$q;")]
    public void RejectsBadTemplates(string template)
    {
        Assert.Throws<ArgumentException>(() => JsPattern.Parse(template));
    }

    [Fact]
    public void RejectsInvalidJavaScript()
    {
        Assert.Throws<JsSyntaxException>(() => JsPattern.Parse("$a = ;"));
    }

    [Fact]
    public void MatchingAllocatesNothing()
    {
        using var ast = MustParse("x = table[i] ^ 0x5f; x = table[i] ^ y;");
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            Assert.True(XorLoad.Match(Stmt(ast, 0), out var b) && b.Int("n") == 0x5f);
            Assert.False(XorLoad.Match(Stmt(ast, 1), out _));
            Assert.False(XorLoad.Match(ast.Program.Body, 1, out _));
        }
    }

    [Fact]
    public void OnePatternMatchesOnManyThreads()
    {
        var pattern = JsPattern.Parse("$a.push($b ^ $n$)");
        Parallel.For(0, 64, i =>
        {
            using var ast = MustParse($"out.push(v{i} ^ {i});");
            Assert.True(pattern.Match(E(ast, 0), out var b));
            Assert.Equal(i, b.Int("n"));
            Assert.Equal($"v{i}", b["b"].AsIdentifier().Name);
        });
    }

    private static Expression E(JsAst ast, int i) => ExprOf(Stmt(ast, i));
}
