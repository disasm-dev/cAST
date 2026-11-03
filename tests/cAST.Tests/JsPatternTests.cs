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
    public void IntegerHolesMatchWholeNumbersUpTo2To53()
    {
        var pattern = JsPattern.Parse("$x$ + $y$");
        using var ast = MustParse("""
            1 + -2;
            3089954259 + -0x20000000000000;
            9007199254740992 + 1e3;
            1 + 2.5; 1 + y; 1 + '2'; 1 + -0; 1 + 9007199254740994; 1 + -9007199254740994; 1 + 1e400; 1 + -x;
            """);
        Assert.True(pattern.Match(E(ast, 0), out var b));
        Assert.Equal((1, -2), (b.Int("x"), b.Int("y")));
        Assert.Equal((1L, -2L), (b.Long("x"), b.Long("y")));
        Assert.True(pattern.Match(E(ast, 1), out b));
        Assert.Equal((3089954259L, -9007199254740992L), (b.Long("x"), b.Long("y")));
        Assert.True(pattern.Match(E(ast, 2), out b));
        Assert.Equal((9007199254740992L, 1000), (b.Long("x"), b.Int("y")));
        for (int i = 3; i < 11; i++)
            Assert.False(pattern.Match(E(ast, i), out _), $"statement {i}");
    }

    [Fact]
    public void IntRefusesValuesOutsideInt32()
    {
        var pattern = JsPattern.Parse("$s$ ^ $s$");
        using var ast = MustParse("3089954259 ^ 3089954259; 3089954259 ^ -1205013037; -2147483649 ^ -2147483649; -2147483648 ^ -2147483648;");
        Assert.True(pattern.Match(E(ast, 0), out var b));
        Assert.Equal(3089954259L, b.Long("s"));
        Assert.Throws<OverflowException>(() => b.Int("s"));
        Assert.False(pattern.Match(E(ast, 1), out _)); // the same low 32 bits, but another number
        Assert.True(pattern.Match(E(ast, 2), out var below));
        Assert.Throws<OverflowException>(() => below.Int("s"));
        Assert.True(pattern.Match(E(ast, 3), out b));
        Assert.Equal(int.MinValue, b.Int("s"));
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
    public void ARunPassesOverBareDeclarations()
    {
        var pattern = JsPattern.Parse("let $a = $s.split('|'); let $i = 0; while ($$) {}");
        using var ast = MustParse("var a; var parts = 'x|y'.split('|'); var b, c; let d; i = 0; while (true) {} f();");
        var body = ast.Program.Body;
        Assert.False(pattern.Match(body, 0, out _, out _)); // the run starts at its first statement
        Assert.True(pattern.Match(body, 1, out var b, out int end));
        Assert.Equal(6, end);
        Assert.True(b["a"].IsIdentifierNamed("parts"));
        Assert.True(b["i"].IsIdentifierNamed("i"));
        Assert.Equal(WriteSpelling.Assign, b.Spelling("i"));
        Assert.False(pattern.Match(body, 1, out _)); // one for one, the declarations are in the way
        Assert.False(pattern.Match(body, 2, out _, out _));
        using var valued = MustParse("var parts = s.split('|'); var e = 1; i = 0; while (1) {}");
        Assert.False(pattern.Match(valued.Program.Body, 0, out _, out _)); // var e = 1 is not bare
    }

    [Fact]
    public void ARunTriesEachBareDeclarationOnTheTemplateFirst()
    {
        var declared = JsPattern.Parse("f(); var $d; $d = 2;");
        using var ast = MustParse("f(); var x; var y; x = 2;");
        Assert.True(declared.Match(ast.Program.Body, 0, out var b, out int end));
        Assert.True(b["d"].IsIdentifierNamed("x"));
        Assert.Equal(4, end);

        var partly = JsPattern.Parse("f(); var $b, $c = 1; g($b);");
        using var tried = MustParse("f(); var x, y; var z, w = 1; g(z);");
        Assert.True(partly.Match(tried.Program.Body, 0, out b, out end));
        Assert.True(b["b"].IsIdentifierNamed("z")); // not x, bound by the failed try on var x, y
        Assert.Equal(4, end);
    }

    [Fact]
    public void ARunMatchesStatementsGatheredIntoAList()
    {
        var pattern = JsPattern.Parse("let $a = 1; $a++;");
        using var ast = MustParse("var a = 1; debugger; var b; a++; f();");
        List<Statement> kept = [.. ast.Program.Body.Where(s => s.Kind != NodeKind.DebuggerStatement)];
        Assert.True(pattern.Match(kept, 0, out var b, out int end));
        Assert.Equal(3, end);
        Assert.True(b["a"].IsIdentifierNamed("a"));
        Assert.False(pattern.Match(kept, 4, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => pattern.Match(kept, 5, out _, out _));
        using var other = MustParse("a++;");
        kept[2] = Stmt(other, 0);
        Assert.Throws<ArgumentException>(() => pattern.Match(kept, 0, out _, out _));
    }

    [Fact]
    public void ALetTemplateMatchesEverySpellingOfAWrite()
    {
        var pattern = JsPattern.Parse("let $x = $v + 1;");
        using var ast = MustParse("a = b + 1; var a = b + 1; let a = b + 1; const a = b + 1;");
        WriteSpelling[] spellings = [WriteSpelling.Assign, WriteSpelling.Var, WriteSpelling.Let, WriteSpelling.Const];
        for (int i = 0; i < 4; i++)
        {
            Assert.True(pattern.Match(Stmt(ast, i), out var b), $"statement {i}");
            Assert.True(b["x"].IsIdentifierNamed("a"));
            Assert.True(b["v"].IsIdentifierNamed("b"));
            Assert.Equal(spellings[i], b.Spelling("x"));
        }
    }

    [Theory]
    [InlineData("a += b + 1;")]
    [InlineData("a = b + 2;")]
    [InlineData("o.a = b + 1;")]
    [InlineData("[a] = b + 1;")]
    [InlineData("a = b + 1, c;")]
    [InlineData("f(a = b + 1);")]
    [InlineData("var a;")]
    [InlineData("var a = b + 1, c;")]
    [InlineData("var [a] = b + 1;")]
    public void AWriteMatchesNothingElse(string code)
    {
        using var ast = MustParse(code);
        Assert.False(JsPattern.Parse("let $x = $v + 1;").Match(Stmt(ast, 0), out _));
    }

    [Fact]
    public void OtherDeclarationsMatchTheirOwnKind()
    {
        using var ast = MustParse("x = 1; var x = 1; let x = 1; for (x = 1;;) {} for (var x = 1;;) {} for (let x = 1;;) {}");
        var declared = JsPattern.Parse("var $x = 1;");
        Assert.False(declared.Match(Stmt(ast, 0), out _));
        Assert.True(declared.Match(Stmt(ast, 1), out _));
        Assert.False(declared.Match(Stmt(ast, 2), out _));
        var loop = JsPattern.Parse("for (let $x = 1;;) {}"); // a for head is not a statement
        Assert.False(loop.Match(Stmt(ast, 3), out _));
        Assert.False(loop.Match(Stmt(ast, 4), out _));
        Assert.True(loop.Match(Stmt(ast, 5), out _));
        using var pair = MustParse("let x = 1, y = 2; var x = 1, y = 2;");
        var two = JsPattern.Parse("let $x = 1, $y = 2;");
        Assert.True(two.Match(Stmt(pair, 0), out _));
        Assert.False(two.Match(Stmt(pair, 1), out _));
    }

    [Fact]
    public void WritesNameOneVariableAndTheWildcardNone()
    {
        var pattern = JsPattern.Parse("let $i = 0; let $i = $i + 1;");
        using var ast = MustParse("var i = 0; i = i + 1; var j = 0; k = j + 1;");
        Assert.True(pattern.Match(ast.Program.Body, 0, out var b));
        Assert.Equal(WriteSpelling.Var, b.Spelling("i")); // the first write's
        Assert.False(pattern.Match(ast.Program.Body, 2, out _));

        var any = JsPattern.Parse("let $$ = $v;");
        using var writes = MustParse("a = f(); const b = g;");
        Assert.True(any.Match(Stmt(writes, 0), out b));
        Assert.True(b["v"].IsCall("f", out _));
        Assert.True(any.Match(Stmt(writes, 1), out _));
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
        Assert.Throws<InvalidOperationException>(() => b.Long("t"));
        Assert.Throws<InvalidOperationException>(() => b.Spelling("k")); // no write statement names it
        Assert.Throws<InvalidOperationException>(() => b.Spelling("n")); // an integer hole
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
        var write = JsPattern.Parse("let $x = $v; $x++;");
        using var ast = MustParse("x = table[i] ^ 0x5f; x = table[i] ^ y; x = table[i] ^ 3089954259; var n = 1; var m; n++;");
        List<Statement> list = [.. ast.Program.Body];
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Run()
        {
            Assert.True(XorLoad.Match(Stmt(ast, 0), out var b) && b.Int("n") == 0x5f);
            Assert.False(XorLoad.Match(Stmt(ast, 1), out _));
            Assert.False(XorLoad.Match(ast.Program.Body, 1, out _));
            Assert.True(XorLoad.Match(Stmt(ast, 2), out var w) && w.Long("n") == 3089954259);
            Assert.True(write.Match(ast.Program.Body, 3, out var r, out int end) && end == 6 && r.Spelling("x") == WriteSpelling.Var);
            Assert.True(write.Match(list, 3, out _, out _));
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
