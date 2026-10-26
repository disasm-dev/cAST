// Removes every console.*(...) statement from a script.
//
//   dotnet run --project examples/StripConsole

using cAST;
using cAST.Generation;
using cAST.Parsing;

const string source = """
    function save(user, debug) {
        console.log("saving", user.id);
        store.put(user);
        if (debug) console.debug("saved");
        return user;
    }
    """;

using var ast = JsParser.ParseOrThrow(source);
new StripConsole().Transform(ast);
Console.WriteLine(JsGenerator.Generate(ast));

sealed class StripConsole : AstTransformer
{
    public override void VisitStatement(ref Statement node)
    {
        if (!IsConsoleCall(node))
        {
            base.VisitStatement(ref node);
            return;
        }
        // A statement in a list (a block's body) can go. One that is a required field, such as the body of an if, is
        // replaced with an empty statement instead.
        if (SlotOf(ref node).IsListElement)
            Remove(ref node);
        else
            node = Ast.NewEmptyStatement();
    }

    private static bool IsConsoleCall(Statement statement) =>
        statement.TryGetExpressionStatement(out var expression)
        && expression.Expression.TryGetCallExpression(out var call)
        && call.Callee.TryGetMemberExpression(out var member)
        && member.Object.TryGetIdentifier(out var owner)
        && owner.Name == "console";
}
