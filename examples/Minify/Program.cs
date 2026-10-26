// Minifies a script, or lists its syntax errors with their line and column.
//
//   dotnet run --project examples/Minify -- path/to/script.js

using cAST.Generation;
using cAST.Parsing;

if (args is not [var path])
{
    Console.Error.WriteLine("usage: Minify <script.js>");
    return 2;
}

using var ast = JsParser.ParseFile(path);
if (ast.HasErrors)
{
    foreach (var error in ast.Errors)
        Console.Error.WriteLine($"{path}{error.ToString(ast.Source)}");
    return 1;
}

Console.WriteLine(JsGenerator.GenerateMinified(ast));
return 0;
