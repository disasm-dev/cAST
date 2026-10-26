// Renames every local variable and parameter to a short name, leaving top-level names and globals alone: the kind of
// renaming a minifier does. The resolver works out which identifiers are the same variable.
//
//   dotnet run --project examples/RenameLocals

using cAST;
using cAST.Generation;
using cAST.Parsing;
using cAST.Resolution;

const string source = """
    var total = 0;
    function add(value, times) {
        for (let index = 0; index < times; index++) {
            const scaled = value * index;
            total += scaled;
        }
        return total;
    }
    """;

using var ast = JsParser.ParseOrThrow(source);
JsResolver.Resolve(ast);
new RenameLocals(ast.BindingNumbers).VisitProgram(ast.Program);
Console.WriteLine(JsGenerator.Generate(ast));

// Every identifier of one binding has the same BindingNumber, so a plain array maps bindings to their new names.
sealed class RenameLocals(int bindings) : AstVisitor
{
    private readonly string?[] _names = new string?[bindings];
    private int _next;

    public override void VisitIdentifier(Identifier node)
    {
        // Unresolved: not a variable (a property name, a label). TopLevel: a top-level declaration or a global.
        if (node.ScopeContext is ScopeContext.Unresolved or ScopeContext.TopLevel)
            return;
        // "$" keeps the new names apart from any global the script reads.
        node.Name = _names[node.BindingNumber] ??= "$" + _next++;
    }
}
