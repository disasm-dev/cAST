# cAST

A fast JavaScript parser for .NET. Parse a script into an AST, walk it, rewrite it, resolve its scopes and print it back
as JavaScript.

It parses TypeScript's 9 MB compiler (`typescript.js`) in 19 ms: 1.7× faster than OXC, 5× faster than SWC and 15×
faster than Babel on the same machine (see [Benchmarks](#benchmarks)).

```csharp
using cAST.Generation;
using cAST.Parsing;

using var ast = JsParser.ParseOrThrow("let total = 1 + 2;");
Console.WriteLine(JsGenerator.GenerateMinified(ast)); // let total=1+2;
```

## Features

- **Modern syntax**: classes with private members and static blocks, async functions and generators, destructuring,
  optional chaining, template literals, BigInt and every regular expression flag.
- **Error recovery**: parsing carries on past a syntax error, and each error comes with its source range.
- **Walking and rewriting**: generated visitors over every node type. A transformer replaces a node by assigning to it,
  and can remove nodes or insert new ones into lists while it walks.
- **Building**: a factory for every node type, and copying of subtrees within a tree or from another one.
- **Code generation**: pretty-printed or minified.
- **Scope resolution**: which identifiers refer to the same variable, as a dictionary key or a dense number for array
  lookups.
- **Low allocation**: the tree is one array of 32-byte records that node structs index into, and disposing it returns
  that array to a pool for the next parse. AOT-compatible.

## Limitations

- Scripts only. `import` and `export` declarations are syntax errors.
- Strict-mode restrictions are not checked.
- Not on NuGet yet. Reference `src/cAST/cAST.csproj`. Requires .NET 10.

## Examples

| Example | What it shows |
|---|---|
| [Minify](examples/Minify/Program.cs) | Parsing a file, reporting syntax errors with line and column, minified output |
| [StripConsole](examples/StripConsole/Program.cs) | A transformer that removes statements, or replaces them where they cannot be removed |
| [RenameLocals](examples/RenameLocals/Program.cs) | Resolving scopes, then renaming every local variable consistently |

```bash
dotnet run --project examples/RenameLocals
```

## Guide

### Parsing

`JsParser.Parse` (or `ParseFile`, or `ParseUtf8` for bytes) returns a `JsAst`. Check `HasErrors` and `Errors`, or use
`ParseOrThrow`, which throws a `JsSyntaxException` holding every error. `ParseError.ToString(source)` prefixes the message
with its line and column.

Dispose the `JsAst` when you are done with it: its arena goes back to a pool. Nothing read from it may be used afterwards.

### Walking

Subclass `AstVisitor` and override the `Visit` method of each node type you care about. Call the base method to carry on
into the node's children.

```csharp
sealed class CountCalls : AstVisitor
{
    public int Calls;

    public override void VisitCallExpression(CallExpression node)
    {
        Calls++;
        base.VisitCallExpression(node);
    }
}

var counter = new CountCalls();
counter.VisitProgram(ast.Program);
```

### Rewriting

Subclass `AstTransformer`. Its hooks take each node by `ref`, and assigning a new node replaces it in its parent. Build
new nodes with the factories on the tree (`Ast.NewNumberLiteral(3)`, `Ast.NewEmptyStatement()`, and so on).

```csharp
sealed class FoldAdditions : AstTransformer
{
    public override void VisitExpression(ref Expression node)
    {
        base.VisitExpression(ref node);
        if (node.TryGetBinaryExpression(out var sum) && sum.Operator == BinaryOperator.Addition
            && sum.Left.TryGetNumberLiteral(out var left) && sum.Right.TryGetNumberLiteral(out var right))
            node = Ast.NewNumberLiteral(left.Value + right.Value);
    }
}

new FoldAdditions().Transform(ast);
```

`Remove`, `InsertBefore` and `InsertAfter` edit the list the current node is in, once the walk of that list finishes.
`SlotOf` returns a handle to a node's place in its parent that can still be edited after the walk. `JsAst.Clone` copies a
subtree within a tree, and `Import` copies one from another tree.

### Generating

`JsGenerator.Generate` prints a tree or any node of it, pretty-printed. `GenerateMinified` prints it on one line.

### Scopes

`JsResolver.Resolve` marks every identifier that names a variable with its `ScopeContext`. Two identifiers with the same
name and context are the same variable.

- `Identifier.BindingId` combines the two into a dictionary key.
- `Identifier.BindingNumber` is a number for the same thing, below `JsAst.BindingNumbers`, so a plain array can be indexed
  by it.
- Top-level declarations and names nothing declares (globals such as `window`) get `ScopeContext.TopLevel`.
- Identifiers that are not variables, such as the `b` of `a.b` or a label, stay `ScopeContext.Unresolved`.

Resolve again after a rewrite adds or moves identifiers.

## Benchmarks

Milliseconds per operation, lower is better, best of two runs. Fastest per row in bold.

Parse:

| File | cAST | OXC | SWC | go-fAST | Babel |
|---|---:|---:|---:|---:|---:|
| typescript.js | **18.56** | 32.09 | 101.39 | 38.95 | 285.16 |
| prettier-flow.min.js | **3.29** | 6.10 | 19.37 | 8.30 | 60.37 |
| prettier-typescript.min.js | **2.09** | 3.61 | 12.51 | 4.84 | 30.09 |
| babel-parser.js | **1.32** | 2.11 | 6.48 | 2.72 | 12.50 |
| wabt.js | 0.50 | **0.48** | 0.80 | 1.14 | 2.45 |
| prettier-standalone.js | **0.49** | 0.74 | 1.97 | 1.08 | 4.06 |

Parse and resolve scopes:

| File | cAST | OXC | SWC | go-fAST |
|---|---:|---:|---:|---:|
| typescript.js | **36.16** | 58.23 | 136.46 | 57.81 |
| prettier-flow.min.js | **6.55** | 11.83 | 25.26 | 13.03 |
| prettier-typescript.min.js | **3.48** | 6.09 | 14.55 | 6.96 |
| babel-parser.js | **2.29** | 3.38 | 8.45 | 4.06 |
| wabt.js | **0.56** | 0.58 | 0.90 | 1.36 |
| prettier-standalone.js | **0.82** | 1.22 | 2.54 | 1.66 |

## Building

```bash
dotnet build -c Release
```

The node types, visitors and factories in `src/cAST/Generated` are generated from the schema in
`tools/AstGen/Program.cs`. Change the schema, not the generated files, and regenerate:

```bash
dotnet run --project tools/AstGen -- src/cAST/Generated
```

## License

MIT. See [LICENSE](LICENSE).
