// AstGen: generates the cAST node views, unions, factories, visitor and layout
// tables from the schema below. This plays the role of go-fAST's
// gen_union.go / gen_visit.go / gen_clone.go.
//
// Run from the repository root:
//     dotnet run --project tools/AstGen -- src/cAST/Generated
//
// Every node is stored as a 32-byte NodeRecord inside JsAst. The schema
// assigns each field a storage location in that record:
//   * handles, ints, strings (string ids), BigInts (side table ids): one payload slot (A..G)
//   * lists (start, count into JsAst's list pool) and doubles: two payload slots
//   * enums: the Op byte (at most one enum per node)
//   * bools: a bit of the Flags byte
using System.Text;

var outDir = args.Length > 0 ? args[0] : Path.Combine("src", "cAST", "Generated");
Directory.CreateDirectory(outDir);

var schema = Schema.Build();
schema.AssignSlots();

File.WriteAllText(Path.Combine(outDir, "NodeKind.g.cs"), Emit.NodeKinds(schema));
File.WriteAllText(Path.Combine(outDir, "Nodes.g.cs"), Emit.Nodes(schema));
File.WriteAllText(Path.Combine(outDir, "Unions.g.cs"), Emit.Unions(schema));
File.WriteAllText(Path.Combine(outDir, "Factories.g.cs"), Emit.Factories(schema));
File.WriteAllText(Path.Combine(outDir, "AstVisitor.g.cs"), Emit.Visitor(schema));
File.WriteAllText(Path.Combine(outDir, "AstTransformer.g.cs"), Emit.Transformer(schema));
File.WriteAllText(Path.Combine(outDir, "Layout.g.cs"), Emit.Layout(schema));
Console.WriteLine($"Generated {schema.Nodes.Count} nodes and {schema.Unions.Count} unions into {Path.GetFullPath(outDir)}");

// LazyStr / OptLazyStr: a string-id slot that may be 0, meaning "a slice of the source text"
// (the slice is described by other slots, see JsAst.Lazy.cs). Hidden / HiddenBool: internal storage
// used by those slices; not part of the public view API. SlicePos: a source position that also anchors
// those slices; its setter detaches the node's text from the source first (JsAst.DetachSource).
enum FK { Child, OptChild, List, HoleList, Str, OptStr, LazyStr, OptLazyStr, Hidden, HiddenBool, Pos, SlicePos, Scope, Double, BigInt, Bool, Enum }

sealed class F(string name, FK kind, string type, bool derived = false)
{
    public string Name { get; } = name;
    public FK Kind { get; } = kind;
    public string Type { get; } = type;
    /// <summary>A lazy string computed from the node's other text (JsAst.SetLazyStringId); not a factory parameter.</summary>
    public bool Derived { get; } = derived;
    public int Slot { get; set; } = -1;
    public int Bit { get; set; } = -1;
    public bool IsChild => Kind is FK.Child or FK.OptChild;
    public bool IsList => Kind is FK.List or FK.HoleList;
}

sealed class NodeDef(string name, string doc, F[] fields)
{
    public string Name { get; } = name;
    public string Doc { get; } = doc;
    public F[] Fields { get; } = fields;
    /// <summary>The fields in source order (the order of the public factory's parameters); <see cref="Fields"/> unless declared.</summary>
    public F[] SourceOrder { get; set; } = fields;
    public List<UnionDef> Unions { get; } = new();
}

sealed class UnionDef(string name, string doc, int categoryBit, string[] variants)
{
    public string Name { get; } = name;
    public string Doc { get; } = doc;
    /// <summary>Bit in NodeKinds category table, or -1 for composite unions.</summary>
    public int CategoryBit { get; } = categoryBit;
    /// <summary>Concrete node names, or other (category) union names for composites.</summary>
    public string[] Variants { get; } = variants;
    public bool IsComposite => CategoryBit < 0;
}

sealed class Schema
{
    public List<NodeDef> Nodes { get; } = new();
    public List<UnionDef> Unions { get; } = new();
    public Dictionary<string, NodeDef> NodeByName { get; } = new();
    public Dictionary<string, UnionDef> UnionByName { get; } = new();

    public static readonly Dictionary<string, (string Method, string Elem)> ListVisit = new()
    {
        ["Statement"] = ("VisitStatements", "Statement"),
        ["Expression"] = ("VisitExpressions", "Expression"),
        ["Property"] = ("VisitProperties", "Property"),
        ["ClassElement"] = ("VisitClassElements", "ClassElement"),
        ["VariableDeclarator"] = ("VisitVariableDeclarators", "VariableDeclarator"),
        ["Pattern"] = ("VisitPatterns", "Pattern"),
        ["PatternProperty"] = ("VisitPatternProperties", "PatternProperty"),
        ["SwitchCase"] = ("VisitSwitchCases", "SwitchCase"),
        ["TemplateElement"] = ("VisitTemplateElements", "TemplateElement"),
    };

    static F Child(string n, string t) => new(n, FK.Child, t);
    static F Opt(string n, string t) => new(n, FK.OptChild, t);
    static F List(string n, string t) => new(n, FK.List, t);
    static F HoleList(string n, string t) => new(n, FK.HoleList, t);
    static F Str(string n) => new(n, FK.Str, "string");
    static F OptStr(string n) => new(n, FK.OptStr, "string");
    static F Lazy(string n, bool derived = false) => new(n, FK.LazyStr, "string", derived: derived);
    static F OptLazy(string n) => new(n, FK.OptLazyStr, "string");
    static F Hidden(string n) => new(n, FK.Hidden, "int");
    static F HiddenBool(string n) => new(n, FK.HiddenBool, "bool");
    static F Pos(string n) => new(n, FK.Pos, "int");
    static F SlicePos(string n) => new(n, FK.SlicePos, "int");
    static F Scope(string n) => new(n, FK.Scope, "ScopeContext");
    static F Dbl(string n) => new(n, FK.Double, "double");
    static F Big(string n) => new(n, FK.BigInt, "BigInteger");
    static F Bool(string n) => new(n, FK.Bool, "bool");
    static F Enum(string n, string t) => new(n, FK.Enum, t);

    void N(string name, string doc, params F[] fields)
    {
        var n = new NodeDef(name, doc, fields);
        Nodes.Add(n);
        NodeByName[name] = n;
    }

    /// <summary>Declares the source order of a node's fields, where it differs from their storage and walk order.</summary>
    void SourceOrder(string name, params string[] fields)
    {
        var n = NodeByName[name];
        if (fields.Length != n.Fields.Length || fields.Distinct().Count() != fields.Length)
            throw new Exception($"{name}: the source order must list every field once");
        n.SourceOrder = fields.Select(f => n.Fields.Single(x => x.Name == f)).ToArray();
    }

    void U(string name, string doc, int bit, params string[] variants)
    {
        var u = new UnionDef(name, doc, bit, variants);
        Unions.Add(u);
        UnionByName[name] = u;
    }

    public static Schema Build()
    {
        var s = new Schema();
        s.N("Program", "The root node of a parsed script.", List("Body", "Statement"));

        // ---- expressions ----
        s.N("ArrayLiteral", "An array literal <c>[a, , ...b]</c>. Elisions are stored as none elements.",
            HoleList("Value", "Expression"), Pos("LeftBracket"), Pos("RightBracket"));
        s.N("ArrowFunctionLiteral", "An arrow function <c>(a, b) =&gt; body</c>.",
            Child("ParameterList", "ParameterList"), Child("Body", "ConciseBody"), Scope("ScopeContext"), Pos("Idx"), Bool("Async"));
        s.N("AssignExpression", "An assignment <c>left op right</c>, where the left side is a pattern.",
            Child("Left", "Pattern"), Child("Right", "Expression"), Enum("Operator", "AssignmentOperator"));
        s.N("AwaitExpression", "An <c>await argument</c> expression.",
            Child("Argument", "Expression"), Pos("Await"));
        s.N("BigIntLiteral", "A BigInt literal such as <c>42n</c> or <c>0xffn</c>.",
            Big("Value"), OptLazy("Raw"), SlicePos("Idx"), Hidden("RawLength"));
        s.N("BinaryExpression", "A binary expression. The short-circuiting operators live on <see cref=\"LogicalExpression\"/>.",
            Child("Left", "Expression"), Child("Right", "Expression"), Enum("Operator", "BinaryOperator"));
        s.N("BooleanLiteral", "A <c>true</c> or <c>false</c> literal.",
            Pos("Idx"), Bool("Value"));
        s.N("CallExpression", "A call <c>callee(args)</c>.",
            Child("Callee", "Expression"), List("ArgumentList", "Expression"), Pos("LeftParenthesis"), Pos("RightParenthesis"));
        s.N("ClassLiteral", "A class body shared by class declarations and class expressions.",
            Opt("Name", "Identifier"), Opt("SuperClass", "Expression"), List("Body", "ClassElement"), Pos("Class"), Pos("RightBrace"));
        s.N("ConditionalExpression", "A conditional <c>test ? consequent : alternate</c>.",
            Child("Test", "Expression"), Child("Consequent", "Expression"), Child("Alternate", "Expression"));
        s.N("FunctionLiteral", "A function (declaration body, expression, or method body).",
            Opt("Name", "Identifier"), Child("ParameterList", "ParameterList"), Child("Body", "BlockStatement"), Scope("ScopeContext"), Pos("Function"), Bool("Async"), Bool("Generator"));
        s.N("Identifier", "An identifier reference or binding.",
            Lazy("Name"), Scope("ScopeContext"), SlicePos("Idx"), Hidden("RawLength"));
        s.N("InvalidExpression", "Placeholder produced by error recovery.",
            Pos("From"), Pos("To"));
        s.N("LogicalExpression", "A short-circuiting <c>||</c>, <c>&amp;&amp;</c> or <c>??</c> expression.",
            Child("Left", "Expression"), Child("Right", "Expression"), Enum("Operator", "LogicalOperator"));
        s.N("MemberExpression", "A member access <c>object.property</c> or <c>object[property]</c>.",
            Child("Object", "Expression"), Child("Property", "MemberProperty"));
        s.N("MetaProperty", "A meta-property: <c>new.target</c> or <c>import.meta</c>.",
            Enum("Meta", "MetaPropertyKind"), Pos("Idx"));
        s.N("NewExpression", "A <c>new callee(args)</c> expression.",
            Child("Callee", "Expression"), List("ArgumentList", "Expression"), Pos("New"), Pos("LeftParenthesis"), Pos("RightParenthesis"));
        s.N("NullLiteral", "The <c>null</c> literal.", Pos("Idx"));
        s.N("NumberLiteral", "A numeric literal. NaN should not be stored here; use an identifier instead.",
            Dbl("Value"), OptLazy("Raw"), SlicePos("Idx"), Hidden("RawLength"));
        s.N("ObjectLiteral", "An object literal <c>{ ... }</c>.",
            List("Value", "Property"), Pos("LeftBrace"), Pos("RightBrace"));
        s.N("Optional", "The object/callee sitting just before an optional <c>?.</c> access.",
            Child("Expr", "Expression"));
        s.N("OptionalChain", "The outermost node of an optional chain such as <c>a?.b.c</c>.",
            Child("Base", "Expression"));
        s.N("PrivateDotExpression", "A private member access <c>left.#name</c>.",
            Child("Left", "Expression"), Child("Identifier", "PrivateIdentifier"));
        s.N("PrivateIdentifier", "A private name <c>#name</c>.",
            Child("Identifier", "Identifier"));
        s.N("RegExpLiteral", "A regular expression literal <c>/pattern/flags</c>.",
            Lazy("Literal", derived: true), Lazy("Pattern"), Lazy("Flags"), SlicePos("Idx"), Hidden("RawLength"), Hidden("PatternLength"));
        s.N("SequenceExpression", "A comma expression <c>a, b, c</c>.",
            List("Sequence", "Expression"));
        s.N("SpreadElement", "A spread <c>...expression</c> (array literal, call argument or object literal).",
            Child("Expression", "Expression"));
        s.N("StringLiteral", "A string literal.",
            Lazy("Value"), OptLazy("Raw"), SlicePos("Idx"), Hidden("RawLength"), HiddenBool("Unquoted"));
        s.N("SuperExpression", "The <c>super</c> keyword.", Pos("Idx"));
        s.N("TemplateLiteral", "A (possibly tagged) template literal.",
            Opt("Tag", "Expression"), List("Elements", "TemplateElement"), List("Expressions", "Expression"), Pos("OpenQuote"), Pos("CloseQuote"));
        s.N("ThisExpression", "The <c>this</c> keyword.", Pos("Idx"));
        s.N("UnaryExpression", "A prefix unary expression such as <c>!x</c> or <c>typeof x</c>.",
            Child("Operand", "Expression"), Pos("Idx"), Enum("Operator", "UnaryOperator"));
        s.N("UpdateExpression", "A <c>++</c>/<c>--</c> expression, prefix or postfix.",
            Child("Operand", "Expression"), Pos("Idx"), Enum("Operator", "UpdateOperator"), Bool("Postfix"));
        s.N("YieldExpression", "A <c>yield</c> or <c>yield*</c> expression.",
            Opt("Argument", "Expression"), Pos("Yield"), Bool("Delegate"));

        s.N("TemplateElement", "A raw/cooked chunk of a template literal.",
            Lazy("Literal", derived: true), Lazy("Parsed"), SlicePos("Idx"), Hidden("RawLength"));

        // ---- statements ----
        s.N("BadStatement", "Placeholder produced by error recovery.", Pos("From"), Pos("To"));
        s.N("BlockStatement", "A block <c>{ ... }</c>.",
            List("List", "Statement"), Scope("ScopeContext"), Pos("LeftBrace"), Pos("RightBrace"));
        s.N("BreakStatement", "A <c>break</c> statement.", Opt("Label", "Identifier"), Pos("Idx"));
        s.N("ClassDeclaration", "A class declaration.", Child("Class", "ClassLiteral"));
        s.N("ContinueStatement", "A <c>continue</c> statement.", Opt("Label", "Identifier"), Pos("Idx"));
        s.N("DebuggerStatement", "A <c>debugger</c> statement.", Pos("Debugger"));
        s.N("DoWhileStatement", "A <c>do ... while</c> loop.",
            Child("Test", "Expression"), Child("Body", "Statement"), Pos("Do"));
        s.N("EmptyStatement", "An empty statement <c>;</c>.", Pos("Semicolon"));
        s.N("ExpressionStatement", "An expression statement.",
            Child("Expression", "Expression"), OptStr("Comment"));
        s.N("ForStatement", "A C-style <c>for (init; test; update)</c> loop.",
            Opt("Initializer", "ForInit"), Opt("Update", "Expression"), Opt("Test", "Expression"), Child("Body", "Statement"), Pos("For"));
        s.SourceOrder("ForStatement", "Initializer", "Test", "Update", "Body", "For"); // storage and walk keep go-fAST's order
        s.N("ForInStatement", "A <c>for (into in source)</c> loop.",
            Child("Into", "ForInto"), Child("Source", "Expression"), Child("Body", "Statement"), Pos("For"));
        s.N("ForOfStatement", "A <c>for (into of source)</c> or <c>for await</c> loop.",
            Child("Into", "ForInto"), Child("Source", "Expression"), Child("Body", "Statement"), Pos("For"), Bool("Await"));
        s.N("FunctionDeclaration", "A function declaration.", Child("Function", "FunctionLiteral"));
        s.N("IfStatement", "An <c>if</c> statement.",
            Child("Test", "Expression"), Child("Consequent", "Statement"), Opt("Alternate", "Statement"), Pos("If"));
        s.N("LabelledStatement", "A labelled statement <c>label: statement</c>.",
            Child("Label", "Identifier"), Child("Statement", "Statement"), Pos("Colon"));
        s.N("ReturnStatement", "A <c>return</c> statement.", Opt("Argument", "Expression"), Pos("Return"));
        s.N("SwitchStatement", "A <c>switch</c> statement.",
            Child("Discriminant", "Expression"), List("Body", "SwitchCase"), Pos("Switch"));
        s.N("ThrowStatement", "A <c>throw</c> statement.", Child("Argument", "Expression"), Pos("Throw"));
        s.N("TryStatement", "A <c>try</c> statement.",
            Child("Body", "BlockStatement"), Opt("Catch", "CatchClause"), Opt("Finally", "BlockStatement"), Pos("Try"));
        s.N("VariableDeclaration", "A <c>var</c>, <c>let</c> or <c>const</c> declaration.",
            List("List", "VariableDeclarator"), OptStr("Comment"), Pos("Idx"), Enum("VarKind", "VarKind"));
        s.N("WhileStatement", "A <c>while</c> loop.",
            Child("Test", "Expression"), Child("Body", "Statement"), Pos("While"));
        s.N("WithStatement", "A <c>with</c> statement.",
            Child("Object", "Expression"), Child("Body", "Statement"), Pos("With"));

        // ---- helpers ----
        s.N("SwitchCase", "A <c>case test:</c> or <c>default:</c> clause.",
            Opt("Test", "Expression"), List("Consequent", "Statement"), Pos("Case"));
        s.N("CatchClause", "The <c>catch (parameter) { ... }</c> clause of a try statement.",
            Opt("Parameter", "Pattern"), Child("Body", "BlockStatement"), Pos("Catch"));
        s.N("VariableDeclarator", "A single <c>target = initializer</c> binding (also used for parameters).",
            Child("Target", "Pattern"), Opt("Initializer", "Expression"));
        s.N("ParameterList", "A formal parameter list.",
            List("List", "VariableDeclarator"), Opt("Rest", "Pattern"), Pos("Opening"), Pos("Closing"));

        // ---- class elements ----
        s.N("FieldDefinition", "A class field <c>[static] key = initializer;</c>.",
            Child("Key", "PropertyName"), Opt("Initializer", "Expression"), Pos("Idx"), Bool("Static"));
        s.N("MethodDefinition", "A class method, getter or setter.",
            Child("Key", "PropertyName"), Enum("MethodKind", "MethodKind"), Child("Body", "FunctionLiteral"), Pos("Idx"), Bool("Static"));
        s.N("ClassStaticBlock", "A class static initialization block <c>static { ... }</c>.",
            Child("Block", "BlockStatement"), Pos("Static"));

        s.N("ComputedProperty", "A computed key or member <c>[expr]</c>.",
            Child("Expr", "Expression"), Pos("LeftBracket"), Pos("RightBracket"));

        // ---- object literal properties ----
        s.N("PropertyGetter", "An object literal getter <c>get key() {}</c>.",
            Child("Key", "PropertyName"), Child("Body", "FunctionLiteral"));
        s.N("PropertyKeyValue", "An object literal property <c>key: value</c>.",
            Child("Key", "PropertyName"), Child("Value", "Expression"));
        s.N("PropertyMethod", "An object literal method shorthand <c>key() {}</c>; async/generator flags live on the body.",
            Child("Key", "PropertyName"), Child("Body", "FunctionLiteral"));
        s.N("PropertySetter", "An object literal setter <c>set key(v) {}</c>.",
            Child("Key", "PropertyName"), Child("Body", "FunctionLiteral"));
        s.N("PropertyShort", "A shorthand property <c>{ a }</c> or cover-grammar <c>{ a = d }</c>.",
            Child("Name", "Identifier"), Opt("Initializer", "Expression"));

        // ---- patterns ----
        s.N("ArrayPattern", "An array destructuring pattern. Elisions are stored as none elements.",
            HoleList("Elements", "Pattern"), Opt("Rest", "Pattern"), Pos("LeftBracket"), Pos("RightBracket"));
        s.N("AssignmentPattern", "A defaulted binding element <c>target = default</c>.",
            Child("Left", "Pattern"), Child("Right", "Expression"));
        s.N("ObjectPattern", "An object destructuring pattern.",
            List("Properties", "PatternProperty"), Opt("Rest", "Pattern"), Pos("LeftBrace"), Pos("RightBrace"));
        s.N("PatternKeyValue", "An object pattern property <c>key: value</c>.",
            Child("Key", "PropertyName"), Child("Value", "Pattern"));
        s.N("PatternShorthand", "An object pattern shorthand <c>a</c> or <c>a = default</c>.",
            Child("Name", "Identifier"), Opt("Initializer", "Expression"));

        // ---- unions ----
        s.U("Expression", "Any expression node.", 0,
            "ArrayLiteral", "ArrowFunctionLiteral", "AssignExpression", "AwaitExpression", "BigIntLiteral", "BinaryExpression",
            "BooleanLiteral", "CallExpression", "ClassLiteral", "ConditionalExpression", "FunctionLiteral", "Identifier",
            "InvalidExpression", "LogicalExpression", "MemberExpression", "MetaProperty", "NewExpression", "NullLiteral",
            "NumberLiteral", "ObjectLiteral", "Optional", "OptionalChain", "PrivateDotExpression", "PrivateIdentifier",
            "RegExpLiteral", "SequenceExpression", "SpreadElement", "StringLiteral", "SuperExpression", "TemplateLiteral",
            "ThisExpression", "UnaryExpression", "UpdateExpression", "YieldExpression");
        s.U("Statement", "Any statement node.", 1,
            "BadStatement", "BlockStatement", "BreakStatement", "ClassDeclaration", "ContinueStatement", "DebuggerStatement",
            "DoWhileStatement", "EmptyStatement", "ExpressionStatement", "ForInStatement", "ForOfStatement", "ForStatement",
            "FunctionDeclaration", "IfStatement", "LabelledStatement", "ReturnStatement", "SwitchStatement", "ThrowStatement",
            "TryStatement", "VariableDeclaration", "WhileStatement", "WithStatement");
        s.U("Pattern", "A binding or assignment target. Member and private-dot variants only occur in assignment position.", 2,
            "ArrayPattern", "AssignmentPattern", "Identifier", "InvalidExpression", "MemberExpression", "ObjectPattern", "PrivateDotExpression");
        s.U("Property", "An object literal member.", 3,
            "PropertyGetter", "PropertyKeyValue", "PropertyMethod", "PropertySetter", "PropertyShort", "SpreadElement");
        s.U("PropertyName", "An object/class member key. Computed keys are the ComputedProperty variant.", 4,
            "BigIntLiteral", "ComputedProperty", "NumberLiteral", "PrivateIdentifier", "StringLiteral");
        s.U("MemberProperty", "The property part of a member expression.", 5,
            "ComputedProperty", "Identifier");
        s.U("ClassElement", "A class body element.", 6,
            "ClassStaticBlock", "FieldDefinition", "MethodDefinition");
        s.U("PatternProperty", "A property inside an object pattern.", 7,
            "PatternKeyValue", "PatternShorthand");
        s.U("ConciseBody", "An arrow function body: a block or an expression.", -1,
            "BlockStatement", "Expression");
        s.U("ForInit", "A for-loop initializer: an expression or a variable declaration.", -1,
            "Expression", "VariableDeclaration");
        s.U("ForInto", "The left side of a for-in/for-of loop: a pattern or a variable declaration.", -1,
            "Pattern", "VariableDeclaration");

        foreach (var u in s.Unions)
            foreach (var v in s.ConcreteVariants(u))
                s.NodeByName[v].Unions.Add(u);
        return s;
    }

    /// <summary>Expands composite unions to their concrete node names.</summary>
    public IEnumerable<string> ConcreteVariants(UnionDef u)
    {
        foreach (var v in u.Variants)
        {
            if (NodeByName.ContainsKey(v)) yield return v;
            else foreach (var c in ConcreteVariants(UnionByName[v])) yield return c;
        }
    }

    public bool IsUnion(string type) => UnionByName.ContainsKey(type);

    public void AssignSlots()
    {
        foreach (var n in Nodes)
        {
            int slot = 0, bit = 0, enums = 0;
            foreach (var f in n.Fields)
            {
                switch (f.Kind)
                {
                    case FK.Bool or FK.HiddenBool: f.Bit = bit++; break;
                    case FK.Enum: enums++; break;
                    case FK.List or FK.HoleList or FK.Double: f.Slot = slot; slot += 2; break;
                    default: f.Slot = slot++; break;
                }
            }
            if (slot > 7) throw new Exception($"{n.Name} needs {slot} payload slots (max 7)");
            if (bit > 8) throw new Exception($"{n.Name} needs {bit} flag bits (max 8)");
            if (enums > 1) throw new Exception($"{n.Name} has {enums} enum fields (max 1)");
        }
    }
}

static class Emit
{
    const string Header = "// <auto-generated>\n// Generated by tools/AstGen. Do not edit by hand; edit the schema and regenerate.\n// </auto-generated>\n#nullable enable\n";
    static readonly string[] SlotNames = ["A", "B", "C", "D", "E", "F", "G"];

    static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    ];

    static string Camel(string name)
    {
        var c = char.ToLowerInvariant(name[0]) + name[1..];
        return Keywords.Contains(c) ? "@" + c : c;
    }

    static string S(F f) => SlotNames[f.Slot];
    static string S2(F f) => SlotNames[f.Slot + 1];

    public static string NodeKinds(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("namespace cAST;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>The concrete type of an AST node.</summary>");
        sb.AppendLine("public enum NodeKind : byte");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>No node (an absent optional child, an elision, or <c>default</c>).</summary>");
        sb.AppendLine("    None = 0,");
        foreach (var n in s.Nodes)
        {
            sb.AppendLine($"    /// <summary>{n.Doc}</summary>");
            sb.AppendLine($"    {n.Name},");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Classification helpers for <see cref=\"NodeKind\"/>.</summary>");
        sb.AppendLine("public static class NodeKinds");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>The number of <see cref=\"NodeKind\"/> values, including <see cref=\"NodeKind.None\"/>.</summary>");
        sb.AppendLine($"    public const int Count = {s.Nodes.Count + 1};");
        sb.AppendLine();
        sb.AppendLine("    // Bit i set => kind belongs to category union i (Expression, Statement, Pattern, Property,");
        sb.AppendLine("    // PropertyName, MemberProperty, ClassElement, PatternProperty).");
        sb.Append("    private static ReadOnlySpan<byte> Categories => [0");
        foreach (var n in s.Nodes)
        {
            int mask = 0;
            foreach (var u in n.Unions) if (u.CategoryBit >= 0) mask |= 1 << u.CategoryBit;
            sb.Append($", {mask}");
        }
        sb.AppendLine("];");
        sb.AppendLine();
        foreach (var u in s.Unions.Where(u => !u.IsComposite))
        {
            sb.AppendLine($"    /// <summary>Whether <paramref name=\"kind\"/> is a <see cref=\"{u.Name}\"/> variant.</summary>");
            sb.AppendLine($"    public static bool Is{u.Name}(NodeKind kind) => (uint)kind < (uint)Count && (Categories[(int)kind] & {1 << u.CategoryBit}) != 0;");
        }
        foreach (var u in s.Unions.Where(u => u.IsComposite))
        {
            var conds = u.Variants.Select(v => s.NodeByName.ContainsKey(v) ? $"kind == NodeKind.{v}" : $"Is{v}(kind)");
            sb.AppendLine($"    /// <summary>Whether <paramref name=\"kind\"/> is a <see cref=\"{u.Name}\"/> variant.</summary>");
            sb.AppendLine($"    public static bool Is{u.Name}(NodeKind kind) => {string.Join(" || ", conds)};");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    static void CommonMembers(StringBuilder sb, string name, bool concrete)
    {
        sb.AppendLine("    internal readonly JsAst _ast;");
        sb.AppendLine("    internal readonly int _id;");
        sb.AppendLine();
        sb.AppendLine($"    internal {name}(JsAst ast, int id) {{ _ast = ast; _id = id; }}");
        sb.AppendLine();
        sb.AppendLine($"    static {name} INode<{name}>.Create(JsAst ast, int id) => new(ast, id);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The arena that owns this node.</summary>");
        sb.AppendLine("    public JsAst Ast => _ast;");
        sb.AppendLine("    /// <summary>The id of this node within <see cref=\"Ast\"/>; 0 means none.</summary>");
        sb.AppendLine("    public int Id => _id;");
        sb.AppendLine("    /// <summary>The concrete kind of the node, or <see cref=\"NodeKind.None\"/>.</summary>");
        sb.AppendLine("    public NodeKind Kind => _id == 0 ? NodeKind.None : _ast._nodes[_id].Kind;");
        sb.AppendLine("    /// <summary>Whether this is an absent node.</summary>");
        sb.AppendLine("    public bool IsNone => _id == 0;");
        sb.AppendLine("    /// <summary>Offset of the first character belonging to the node.</summary>");
        sb.AppendLine("    public int Start => _ast.GetStart(_id);");
        sb.AppendLine("    /// <summary>Offset of the first character immediately after the node.</summary>");
        sb.AppendLine("    public int End => _ast.GetEnd(_id);");
        sb.AppendLine("    /// <summary>This node as an untyped <see cref=\"cAST.Node\"/>.</summary>");
        sb.AppendLine("    public Node AsNode() => new(_ast, _id);");
        sb.AppendLine($"    /// <summary>Converts to an untyped <see cref=\"cAST.Node\"/>.</summary>");
        sb.AppendLine($"    public static implicit operator Node({name} node) => new(node._ast, node._id);");
        sb.AppendLine();
        sb.AppendLine($"    /// <inheritdoc/>");
        sb.AppendLine($"    public bool Equals({name} other) => _id == other._id && ReferenceEquals(_ast, other._ast);");
        sb.AppendLine($"    /// <inheritdoc/>");
        sb.AppendLine($"    public override bool Equals(object? obj) => obj is {name} other && Equals(other);");
        sb.AppendLine($"    /// <inheritdoc/>");
        sb.AppendLine($"    public override int GetHashCode() => _id;");
        sb.AppendLine($"    /// <summary>Reference equality of the underlying node.</summary>");
        sb.AppendLine($"    public static bool operator ==({name} left, {name} right) => left.Equals(right);");
        sb.AppendLine($"    /// <summary>Reference inequality of the underlying node.</summary>");
        sb.AppendLine($"    public static bool operator !=({name} left, {name} right) => !left.Equals(right);");
        sb.AppendLine($"    /// <summary>Renders the node as minified JavaScript (for debugging).</summary>");
        sb.AppendLine($"    public override string ToString() => JsAst.DebugString(_ast, _id);");
    }

    public static string Nodes(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Numerics;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace cAST;");
        foreach (var n in s.Nodes)
        {
            sb.AppendLine();
            sb.AppendLine($"/// <summary>{n.Doc}</summary>");
            sb.AppendLine("[DebuggerDisplay(\"{Kind}: {ToString(),nq}\")]");
            sb.AppendLine($"public readonly partial struct {n.Name} : INode<{n.Name}>, IEquatable<{n.Name}>");
            sb.AppendLine("{");
            CommonMembers(sb, n.Name, concrete: true);
            sb.AppendLine();
            sb.AppendLine("    private ref NodeRecord R => ref _ast._nodes[_id];");
            foreach (var f in n.Fields)
            {
                sb.AppendLine();
                EmitField(sb, n, f);
            }
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Dispatches to <see cref=\"AstVisitor.Visit{n.Name}\"/>.</summary>");
            sb.AppendLine($"    public void Accept(AstVisitor visitor) => visitor.Visit{n.Name}(this);");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Visits the children of this node, in source order, with <paramref name=\"visitor\"/>.</summary>");
            sb.AppendLine($"    public void VisitChildren(AstVisitor visitor) => AstVisitor.WalkChildrenOf{n.Name}(visitor, _ast, _id);");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    // What setting a text or value field does beyond storing it (JsAst.SetLazyStringId / OnValueSet).
    static readonly Dictionary<(string Node, string Field), string> SetterNotes = new()
    {
        [("Identifier", "Name")] = "Setting it requires a valid IdentifierName.",
        [("StringLiteral", "Value")] = "Setting it drops <see cref=\"Raw\"/>, so the generator quotes the new value.",
        [("StringLiteral", "Raw")] = "The literal as written in the source; the generator prints it as is when present. Null means the value is quoted when printed.",
        [("NumberLiteral", "Value")] = "Setting it drops <see cref=\"Raw\"/>, so the generator prints the new value.",
        [("NumberLiteral", "Raw")] = "The literal as written in the source; the generator prints it as is when present. Null means the value is printed.",
        [("BigIntLiteral", "Value")] = "Setting it drops <see cref=\"Raw\"/>, so the generator prints the new value.",
        [("BigIntLiteral", "Raw")] = "The literal as written in the source (with its <c>n</c>); the generator prints it as is when present. Null means the value is printed.",
        [("RegExpLiteral", "Literal")] = "The whole literal <c>/pattern/flags</c>. Setting it updates <see cref=\"Pattern\"/> and <see cref=\"Flags\"/>; it must be one valid literal.",
        [("RegExpLiteral", "Pattern")] = "The pattern in source form (like <c>RegExp.prototype.source</c>). Setting it escapes a <c>/</c> outside character classes and line terminators, turns the empty pattern into <c>(?:)</c>, and updates <see cref=\"Literal\"/>. The pattern syntax itself is not checked.",
        [("RegExpLiteral", "Flags")] = "Setting it requires letters from <c>dgimsuvy</c>, none repeated and not both <c>u</c> and <c>v</c>, and updates <see cref=\"Literal\"/>.",
        [("TemplateElement", "Literal")] = "The raw text, as written between the delimiters (a tag function receives it as the raw string). Setting it updates <see cref=\"Parsed\"/>; it must be one template chunk with valid escapes.",
        [("TemplateElement", "Parsed")] = "The cooked value. Setting it updates <see cref=\"Literal\"/> with the value escaped as template text.",
    };

    static string Note(NodeDef n, F f) => SetterNotes.TryGetValue((n.Name, f.Name), out var note) ? " " + note : "";

    static void EmitField(StringBuilder sb, NodeDef n, F f)
    {
        switch (f.Kind)
        {
            case FK.Child:
            case FK.OptChild:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c> child{(f.Kind == FK.OptChild ? " (optional; check <c>IsNone</c>)" : "")}.</summary>");
                sb.AppendLine($"    public {f.Type} {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => new(_ast, R.{S(f)});");
                sb.AppendLine($"        set => R.{S(f)} = _ast.Own(value.Ast, value.Id);");
                sb.AppendLine("    }");
                break;
            case FK.List:
            case FK.HoleList:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c> list{(f.Kind == FK.HoleList ? " (elisions are none elements)" : "")}.</summary>");
                sb.AppendLine($"    public NodeList<{f.Type}> {f.Name} => new(_ast, _id, {f.Slot});");
                break;
            case FK.Str:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c> string.</summary>");
                sb.AppendLine($"    public string {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => _ast.GetString(R.{S(f)});");
                sb.AppendLine($"        set => R.{S(f)} = _ast.Intern(value);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>The string-table id of <see cref=\"{f.Name}\"/>. Interned ids are canonical: equal text, equal id.</summary>");
                sb.AppendLine($"    public int {f.Name}Id");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => R.{S(f)};");
                sb.AppendLine($"        set => R.{S(f)} = _ast.CheckStringId(value);");
                sb.AppendLine("    }");
                break;
            case FK.OptStr:
                sb.AppendLine($"    /// <summary>The optional <c>{f.Name}</c> string, or <c>null</c>.</summary>");
                sb.AppendLine($"    public string? {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => _ast.GetStringOrNull(R.{S(f)});");
                sb.AppendLine($"        set => R.{S(f)} = value is null ? 0 : _ast.Intern(value);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>The string-table id of <see cref=\"{f.Name}\"/>; 0 when absent.</summary>");
                sb.AppendLine($"    public int {f.Name}Id");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => R.{S(f)};");
                sb.AppendLine($"        set => R.{S(f)} = _ast.CheckStringId(value);");
                sb.AppendLine("    }");
                break;
            case FK.LazyStr:
            case FK.OptLazyStr:
                bool opt = f.Kind == FK.OptLazyStr;
                sb.AppendLine($"    /// <summary>The {(opt ? "optional " : "")}<c>{f.Name}</c> string{(opt ? ", or <c>null</c>" : "")}. Text taken from the source is materialized on first access.{Note(n, f)}</summary>");
                sb.AppendLine($"    public string{(opt ? "?" : "")} {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => _ast.GetLazyString{(opt ? "OrNull" : "")}(_id, {f.Slot});");
                sb.AppendLine($"        set => _ast.SetLazyString(_id, {f.Slot}, value);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>The text of <see cref=\"{f.Name}\"/> without allocating.</summary>");
                sb.AppendLine($"    public ReadOnlySpan<char> {f.Name}Span => _ast.GetLazySpan(_id, {f.Slot});");
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>The string-table id of <see cref=\"{f.Name}\"/> (interned on first access: equal text, equal id). Setting it works like setting <see cref=\"{f.Name}\"/>.</summary>");
                sb.AppendLine($"    public int {f.Name}Id");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => _ast.GetLazyStringId(_id, {f.Slot});");
                sb.AppendLine($"        set => _ast.SetLazyStringId(_id, {f.Slot}, value);");
                sb.AppendLine("    }");
                break;
            case FK.Hidden:
                sb.AppendLine($"    internal int {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => R.{S(f)};");
                sb.AppendLine($"        set => R.{S(f)} = value;");
                sb.AppendLine("    }");
                break;
            case FK.HiddenBool:
                sb.AppendLine($"    internal bool {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => (R.Flags & {1 << f.Bit}) != 0;");
                sb.AppendLine($"        set {{ ref NodeRecord r = ref R; r.Flags = value ? (byte)(r.Flags | {1 << f.Bit}) : (byte)(r.Flags & ~{1 << f.Bit}); }}");
                sb.AppendLine("    }");
                break;
            case FK.SlicePos:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c> source offset. Setting it first makes the node's text independent of the source, so moving the node never changes its text.</summary>");
                sb.AppendLine($"    public int {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => R.{S(f)};");
                sb.AppendLine("        set");
                sb.AppendLine("        {");
                sb.AppendLine("            _ast.DetachSource(_id);");
                sb.AppendLine($"            R.{S(f)} = value;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                break;
            case FK.Pos:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c> source offset.</summary>");
                sb.AppendLine($"    public int {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => R.{S(f)};");
                sb.AppendLine($"        set => R.{S(f)} = value;");
                sb.AppendLine("    }");
                break;
            case FK.Scope:
                sb.AppendLine($"    /// <summary>The scope context assigned by the resolver.</summary>");
                sb.AppendLine($"    public ScopeContext {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => (ScopeContext)R.{S(f)};");
                sb.AppendLine("        set");
                sb.AppendLine("        {");
                sb.AppendLine($"            R.{S(f)} = (int)value;");
                sb.AppendLine("            if (value != ScopeContext.Unresolved) _ast._scopeContextsSet = true;");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>Sets <see cref=\"{f.Name}\"/> without flagging the arena (the resolver flags it once per run).</summary>");
                sb.AppendLine($"    internal void Set{f.Name}Unflagged(ScopeContext value) => R.{S(f)} = (int)value;");
                break;
            case FK.Double:
                sb.AppendLine($"    /// <summary>The numeric value.{Note(n, f)}</summary>");
                sb.AppendLine($"    public double {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => Unsafe.ReadUnaligned<double>(ref Unsafe.As<int, byte>(ref R.{S(f)}));");
                sb.AppendLine("        set");
                sb.AppendLine("        {");
                sb.AppendLine($"            Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref R.{S(f)}), value);");
                sb.AppendLine("            _ast.OnValueSet(_id);");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                break;
            case FK.BigInt:
                sb.AppendLine($"    /// <summary>The arbitrary-precision value.{Note(n, f)}</summary>");
                sb.AppendLine($"    public BigInteger {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => _ast.GetBigInt(R.{S(f)});");
                sb.AppendLine("        set");
                sb.AppendLine("        {");
                sb.AppendLine($"            R.{S(f)} = _ast.AddBigInt(value);");
                sb.AppendLine("            _ast.OnValueSet(_id);");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                break;
            case FK.Bool:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c> flag.</summary>");
                sb.AppendLine($"    public bool {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => (R.Flags & {1 << f.Bit}) != 0;");
                sb.AppendLine($"        set {{ ref NodeRecord r = ref R; r.Flags = value ? (byte)(r.Flags | {1 << f.Bit}) : (byte)(r.Flags & ~{1 << f.Bit}); }}");
                sb.AppendLine("    }");
                break;
            case FK.Enum:
                sb.AppendLine($"    /// <summary>The <c>{f.Name}</c>.</summary>");
                sb.AppendLine($"    public {f.Type} {f.Name}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => ({f.Type})R.Op;");
                sb.AppendLine($"        set => R.Op = (byte)value;");
                sb.AppendLine("    }");
                break;
        }
    }

    public static string Unions(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine();
        sb.AppendLine("namespace cAST;");
        foreach (var u in s.Unions)
        {
            sb.AppendLine();
            sb.AppendLine($"/// <summary>{u.Doc}</summary>");
            sb.AppendLine("[DebuggerDisplay(\"{Kind}: {ToString(),nq}\")]");
            sb.AppendLine($"public readonly partial struct {u.Name} : INode<{u.Name}>, IEquatable<{u.Name}>");
            sb.AppendLine("{");
            CommonMembers(sb, u.Name, concrete: false);
            foreach (var v in u.Variants)
            {
                bool concrete = s.NodeByName.ContainsKey(v);
                string test = concrete ? $"Kind == NodeKind.{v}" : $"NodeKinds.Is{v}(Kind)";
                sb.AppendLine();
                sb.AppendLine($"    /// <summary>Whether this is a <see cref=\"{v}\"/>.</summary>");
                sb.AppendLine($"    public bool Is{v} => {test};");
                sb.AppendLine($"    /// <summary>Returns this node as a <see cref=\"{v}\"/>, or throws <see cref=\"InvalidCastException\"/>.</summary>");
                sb.AppendLine($"    public {v} As{v}() => {test} ? new(_ast, _id) : throw JsAst.BadCast(Kind, \"{v}\");");
                sb.AppendLine($"    /// <summary>Returns this node as a <see cref=\"{v}\"/> if it is one.</summary>");
                sb.AppendLine($"    public bool TryGet{v}(out {v} node)");
                sb.AppendLine("    {");
                sb.AppendLine($"        if ({test}) {{ node = new(_ast, _id); return true; }}");
                sb.AppendLine("        node = default;");
                sb.AppendLine("        return false;");
                sb.AppendLine("    }");
                sb.AppendLine($"    /// <summary>Converts a <see cref=\"{v}\"/> to a <see cref=\"{u.Name}\"/>.</summary>");
                sb.AppendLine($"    public static implicit operator {u.Name}({v} node) => new(node._ast, node._id);");
            }
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Dispatches to <see cref=\"AstVisitor.Visit{u.Name}\"/>.</summary>");
            sb.AppendLine($"    public void Accept(AstVisitor visitor) => visitor.Visit{u.Name}(this);");
            sb.AppendLine("}");
        }

        // Untyped Node view.
        sb.AppendLine();
        sb.AppendLine("/// <summary>An untyped reference to any AST node.</summary>");
        sb.AppendLine("[DebuggerDisplay(\"{Kind}: {ToString(),nq}\")]");
        sb.AppendLine("public readonly partial struct Node : INode<Node>, IEquatable<Node>");
        sb.AppendLine("{");
        sb.AppendLine("    internal readonly JsAst _ast;");
        sb.AppendLine("    internal readonly int _id;");
        sb.AppendLine();
        sb.AppendLine("    internal Node(JsAst ast, int id) { _ast = ast; _id = id; }");
        sb.AppendLine();
        sb.AppendLine("    static Node INode<Node>.Create(JsAst ast, int id) => new(ast, id);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The arena that owns this node.</summary>");
        sb.AppendLine("    public JsAst Ast => _ast;");
        sb.AppendLine("    /// <summary>The id of this node within <see cref=\"Ast\"/>; 0 means none.</summary>");
        sb.AppendLine("    public int Id => _id;");
        sb.AppendLine("    /// <summary>The concrete kind of the node, or <see cref=\"NodeKind.None\"/>.</summary>");
        sb.AppendLine("    public NodeKind Kind => _id == 0 ? NodeKind.None : _ast._nodes[_id].Kind;");
        sb.AppendLine("    /// <summary>Whether this is an absent node.</summary>");
        sb.AppendLine("    public bool IsNone => _id == 0;");
        sb.AppendLine("    /// <summary>Offset of the first character belonging to the node.</summary>");
        sb.AppendLine("    public int Start => _ast.GetStart(_id);");
        sb.AppendLine("    /// <summary>Offset of the first character immediately after the node.</summary>");
        sb.AppendLine("    public int End => _ast.GetEnd(_id);");
        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public bool Equals(Node other) => _id == other._id && ReferenceEquals(_ast, other._ast);");
        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public override bool Equals(object? obj) => obj is Node other && Equals(other);");
        sb.AppendLine("    /// <inheritdoc/>");
        sb.AppendLine("    public override int GetHashCode() => _id;");
        sb.AppendLine("    /// <summary>Reference equality of the underlying node.</summary>");
        sb.AppendLine("    public static bool operator ==(Node left, Node right) => left.Equals(right);");
        sb.AppendLine("    /// <summary>Reference inequality of the underlying node.</summary>");
        sb.AppendLine("    public static bool operator !=(Node left, Node right) => !left.Equals(right);");
        sb.AppendLine("    /// <summary>Renders the node as minified JavaScript (for debugging).</summary>");
        sb.AppendLine("    public override string ToString() => JsAst.DebugString(_ast, _id);");

        foreach (var u in s.Unions)
        {
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Whether this node can be viewed as a <see cref=\"{u.Name}\"/>.</summary>");
            sb.AppendLine($"    public bool Is{u.Name} => NodeKinds.Is{u.Name}(Kind);");
            sb.AppendLine($"    /// <summary>Views this node as a <see cref=\"{u.Name}\"/>, or throws <see cref=\"InvalidCastException\"/>.</summary>");
            sb.AppendLine($"    public {u.Name} As{u.Name}() => NodeKinds.Is{u.Name}(Kind) ? new(_ast, _id) : throw JsAst.BadCast(Kind, \"{u.Name}\");");
        }
        foreach (var n in s.Nodes)
        {
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Whether this is a <see cref=\"{n.Name}\"/>.</summary>");
            sb.AppendLine($"    public bool Is{n.Name} => Kind == NodeKind.{n.Name};");
            sb.AppendLine($"    /// <summary>Returns this node as a <see cref=\"{n.Name}\"/>, or throws <see cref=\"InvalidCastException\"/>.</summary>");
            sb.AppendLine($"    public {n.Name} As{n.Name}() => Kind == NodeKind.{n.Name} ? new(_ast, _id) : throw JsAst.BadCast(Kind, \"{n.Name}\");");
            sb.AppendLine($"    /// <summary>Returns this node as a <see cref=\"{n.Name}\"/> if it is one.</summary>");
            sb.AppendLine($"    public bool TryGet{n.Name}(out {n.Name} node)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (Kind == NodeKind.{n.Name}) {{ node = new(_ast, _id); return true; }}");
            sb.AppendLine("        node = default;");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
        }
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Dispatches to the <see cref=\"AstVisitor\"/> method for the concrete node kind.</summary>");
        sb.AppendLine("    public void Accept(AstVisitor visitor)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (Kind)");
        sb.AppendLine("        {");
        foreach (var n in s.Nodes)
            sb.AppendLine($"            case NodeKind.{n.Name}: visitor.Visit{n.Name}(new {n.Name}(_ast, _id)); break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string Factories(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Numerics;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine();
        sb.AppendLine("namespace cAST;");
        sb.AppendLine();
        sb.AppendLine("public sealed partial class JsAst");
        sb.AppendLine("{");
        foreach (var n in s.Nodes)
        {
            // ---- raw (id-based) factory, used by the parser ----
            var rawParams = new List<string>();
            foreach (var f in n.Fields)
            {
                string type = f.Kind switch
                {
                    FK.Child or FK.OptChild => "int",
                    FK.List or FK.HoleList => "ListRange",
                    FK.Str or FK.OptStr or FK.LazyStr or FK.OptLazyStr or FK.Hidden => "int",
                    FK.HiddenBool => "bool",
                    FK.BigInt => "int",
                    _ => f.Type,
                };
                rawParams.Add($"{type} {Camel(f.Name)}");
            }
            sb.AppendLine();
            sb.AppendLine($"    internal int Create{n.Name}({string.Join(", ", rawParams)})");
            sb.AppendLine("    {");
            sb.AppendLine($"        ref NodeRecord r = ref AllocRecord(NodeKind.{n.Name}, out int id);");
            int flagsBits = 0;
            foreach (var f in n.Fields)
            {
                string p = Camel(f.Name);
                switch (f.Kind)
                {
                    case FK.List or FK.HoleList:
                        sb.AppendLine($"        r.{S(f)} = {p}.Start;");
                        sb.AppendLine($"        r.{S2(f)} = {p}.Count;");
                        break;
                    case FK.Double:
                        sb.AppendLine($"        Unsafe.WriteUnaligned(ref Unsafe.As<int, byte>(ref r.{S(f)}), {p});");
                        break;
                    case FK.Scope:
                        sb.AppendLine($"        r.{S(f)} = (int){p};");
                        break;
                    case FK.Enum:
                        sb.AppendLine($"        r.Op = (byte){p};");
                        break;
                    case FK.Bool or FK.HiddenBool:
                        flagsBits++;
                        break;
                    default:
                        sb.AppendLine($"        r.{S(f)} = {p};");
                        break;
                }
            }
            if (flagsBits > 0)
            {
                var parts = n.Fields.Where(f => f.Kind is FK.Bool or FK.HiddenBool).Select(f => $"({Camel(f.Name)} ? {1 << f.Bit} : 0)");
                sb.AppendLine($"        r.Flags = (byte)({string.Join(" | ", parts)});");
            }
            sb.AppendLine("        return id;");
            sb.AppendLine("    }");

            // ---- public (view-based) factory ----
            var required = new List<F>();
            var optional = new List<F>();
            var positions = new List<F>();
            foreach (var f in n.SourceOrder)
            {
                if (f.Kind is FK.Hidden or FK.HiddenBool || f.Derived) continue;
                if (f.Kind is FK.Pos or FK.SlicePos) positions.Add(f);
                else if (f.Kind is FK.OptChild or FK.OptStr or FK.OptLazyStr or FK.Bool or FK.Scope) optional.Add(f);
                else required.Add(f);
            }
            var pubParams = new List<string>();
            // Source offsets go last, so a positional argument never lands in one.
            foreach (var f in required.Concat(optional).Concat(positions))
            {
                string p = Camel(f.Name);
                string decl = f.Kind switch
                {
                    FK.Child => $"{f.Type} {p}",
                    FK.OptChild => $"{f.Type} {p} = default",
                    FK.List or FK.HoleList => $"ReadOnlySpan<{f.Type}> {p}",
                    FK.Str or FK.LazyStr => $"string {p}",
                    FK.OptStr or FK.OptLazyStr => $"string? {p} = null",
                    FK.Double => $"double {p}",
                    FK.BigInt => $"BigInteger {p}",
                    FK.Bool => $"bool {p} = false",
                    FK.Enum => $"{f.Type} {p}",
                    FK.Scope => $"ScopeContext {p} = ScopeContext.Unresolved",
                    FK.Pos or FK.SlicePos => $"int {p} = 0",
                    _ => throw new Exception(),
                };
                pubParams.Add(decl);
            }
            var args = new List<string>();
            foreach (var f in n.Fields)
            {
                string p = Camel(f.Name);
                args.Add(f.Kind switch
                {
                    FK.Child or FK.OptChild => $"Own({p}.Ast, {p}.Id)",
                    FK.List or FK.HoleList => $"AddList({p})",
                    FK.Str => $"Intern({p})",
                    FK.OptStr => $"{p} is null ? 0 : Intern({p})",
                    FK.LazyStr or FK.OptLazyStr => "0", // set below, through the same path as the property setters
                    FK.Hidden => "0",
                    FK.HiddenBool => "false",
                    FK.BigInt => $"AddBigInt({p})",
                    _ => p,
                });
            }
            var after = new List<string>();
            foreach (var f in n.Fields)
            {
                string p = Camel(f.Name);
                if (f.Kind == FK.Scope)
                    after.Add($"        if ({p} != ScopeContext.Unresolved) _scopeContextsSet = true;");
                else if (f.Kind == FK.LazyStr && !f.Derived)
                    after.Add($"        SetLazyString(id, {f.Slot}, {p});");
                else if (f.Kind == FK.OptLazyStr && !f.Derived)
                    after.Add($"        if ({p} is not null) SetLazyString(id, {f.Slot}, {p});");
            }
            sb.AppendLine();
            sb.AppendLine($"    /// <summary>Creates a new <see cref=\"{n.Name}\"/> in this arena.</summary>");
            sb.AppendLine($"    public {n.Name} New{n.Name}({string.Join(", ", pubParams)})");
            if (after.Count == 0)
            {
                sb.AppendLine($"        => new(this, Create{n.Name}({string.Join(", ", args)}));");
            }
            else
            {
                sb.AppendLine("    {");
                sb.AppendLine($"        int id = Create{n.Name}({string.Join(", ", args)});");
                foreach (var line in after) sb.AppendLine(line);
                sb.AppendLine("        return new(this, id);");
                sb.AppendLine("    }");
            }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string Visitor(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace cAST;");
        sb.AppendLine();
        sb.AppendLine("public abstract unsafe partial class AstVisitor");
        sb.AppendLine("{");

        var unions = s.Unions;
        var lists = Schema.ListVisit.ToList();
        const string Inline = "    [MethodImpl(MethodImplOptions.AggressiveInlining)]";
        const string NoInline = "    [MethodImpl(MethodImplOptions.NoInlining)]";

        // ---- hook table: node kinds use (int)NodeKind, then unions, then lists ----
        for (int i = 0; i < unions.Count; i++)
            sb.AppendLine($"    private const int Hook{unions[i].Name} = NodeKinds.Count + {i};");
        for (int i = 0; i < lists.Count; i++)
            sb.AppendLine($"    private const int Hook{lists[i].Value.Method[5..]} = NodeKinds.Count + {unions.Count + i};");
        sb.AppendLine($"    internal const int HookCount = NodeKinds.Count + {unions.Count + lists.Count};");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Every overridable hook: its index in the override table, its name and its parameter type.</summary>");
        sb.AppendLine("    internal static readonly (int Hook, string Method, Type Parameter)[] Hooks =");
        sb.AppendLine("    [");
        foreach (var n in s.Nodes) sb.AppendLine($"        ((int)NodeKind.{n.Name}, \"Visit{n.Name}\", typeof({n.Name})),");
        foreach (var u in unions) sb.AppendLine($"        (Hook{u.Name}, \"Visit{u.Name}\", typeof({u.Name})),");
        foreach (var (elem, (method, _)) in lists) sb.AppendLine($"        (Hook{method[5..]}, \"{method}\", typeof(NodeList<{elem}>)),");
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>One flag per hook, stored inline in the visitor so a check is a single byte compare.</summary>");
        sb.AppendLine("    [InlineArray(HookCount)]");
        sb.AppendLine("    private struct HookFlags");
        sb.AppendLine("    {");
        sb.AppendLine("        private bool _element0;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Hook calls go through out-of-line thunks: a view passed by value needs a GC-tracked stack temp, and
        // keeping those out of the walkers keeps their frames small and free of zero-initialization.
        foreach (var n in s.Nodes)
        {
            sb.AppendLine(NoInline);
            sb.AppendLine($"    private void CallVisit{n.Name}(JsAst ast, int id) => Visit{n.Name}(new {n.Name}(ast, id));");
        }
        foreach (var u in unions)
        {
            sb.AppendLine(NoInline);
            sb.AppendLine($"    private void CallVisit{u.Name}(JsAst ast, int id) => Visit{u.Name}(new {u.Name}(ast, id));");
        }
        foreach (var (elem, (method, _)) in lists)
        {
            sb.AppendLine(NoInline);
            sb.AppendLine($"    private void Call{method}(JsAst ast, int owner, int slot) => {method}(new NodeList<{elem}>(ast, owner, slot));");
        }
        sb.AppendLine();

        // Statement visiting the child id held in `c`, whose static type is `type`. Union hooks are called even
        // for a none child of a required field (as they always were); optional fields skip none children.
        string VisitChild(string type, string c, bool optional)
        {
            string call;
            if (s.IsUnion(type))
            {
                string walk = s.UnionByName[type].IsComposite ? $"Walk{type}Variants(v, ast, {c})" : $"Walk(v, ast, {c})";
                call = optional
                    ? $"if (v.Ov(Hook{type})) v.CallVisit{type}(ast, {c}); else {walk};"
                    : $"if (v.Ov(Hook{type})) v.CallVisit{type}(ast, {c}); else if ({c} != 0) {walk};";
            }
            else
            {
                call = $"if (v.Ov((int)NodeKind.{type})) v.CallVisit{type}(ast, {c}); else WalkChildrenOf{type}(v, ast, {c});";
            }
            return optional ? $"if ({c} != 0) {{ {call} }}" : call;
        }

        // ---- the walk: a table of per-kind walkers, virtual calls only for overridden hooks ----
        sb.AppendLine("    // Per-kind walkers, indexed by NodeKind. An indirect call per node keeps leaf kinds nearly free and");
        sb.AppendLine("    // gives every call site its own branch-prediction history. Padded to 256 entries so any kind byte is in range.");
        sb.AppendLine("    private static readonly delegate*<AstVisitor, JsAst, int, void>* s_walkers = CreateWalkers();");
        sb.AppendLine();
        sb.AppendLine("    private static delegate*<AstVisitor, JsAst, int, void>* CreateWalkers()");
        sb.AppendLine("    {");
        sb.AppendLine("        var table = (delegate*<AstVisitor, JsAst, int, void>*)NativeMemory.Alloc(256, (nuint)sizeof(nint));");
        sb.AppendLine("        for (int i = 0; i < 256; i++) table[i] = &WalkNone;");
        foreach (var n in s.Nodes) sb.AppendLine($"        table[(int)NodeKind.{n.Name}] = &Walk{n.Name};");
        sb.AppendLine("        return table;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Visits node <paramref name=\"id\"/> (non-zero) through its kind's hook if overridden, else walks its children.</summary>");
        sb.AppendLine("    /// <remarks>");
        sb.AppendLine("    /// Identifiers (about half of all nodes) are handled without the indirect call, and so are leaf kinds this");
        sb.AppendLine("    /// visitor does not override (literals, this, ...), which have nothing to do.");
        sb.AppendLine("    /// </remarks>");
        sb.AppendLine(Inline);
        sb.AppendLine("    internal static void Walk(AstVisitor v, JsAst ast, int id)");
        sb.AppendLine("    {");
        sb.AppendLine("        NodeKind kind = ast._nodes[id].Kind;");
        sb.AppendLine("        if (kind == NodeKind.Identifier)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (v.Ov((int)NodeKind.Identifier)) v.CallVisitIdentifier(ast, id);");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine("        if (v.Skip(kind)) return;");
        sb.AppendLine("        s_walkers[(int)kind](v, ast, id);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void WalkNone(AstVisitor v, JsAst ast, int id) { }");
        sb.AppendLine();
        foreach (var n in s.Nodes)
        {
            bool hasChildren = n.Fields.Any(f => f.IsChild || f.IsList);
            sb.AppendLine($"    private static void Walk{n.Name}(AstVisitor v, JsAst ast, int id)");
            sb.AppendLine("    {");
            if (hasChildren)
            {
                // The stack check comes first: overridden hooks recurse through here too.
                sb.AppendLine("        byte probe;");
                sb.AppendLine("        if ((nuint)((nint)(&probe) - v._stackLimit) >= StackWindow && v.StackIsLow()) { v.WalkOnNewStack(ast, id); return; }");
                sb.AppendLine($"        if (v.Ov((int)NodeKind.{n.Name})) {{ v.CallVisit{n.Name}(ast, id); return; }}");
                sb.AppendLine($"        WalkChildrenOf{n.Name}(v, ast, id);");
            }
            else
            {
                sb.AppendLine($"        if (v.Ov((int)NodeKind.{n.Name})) v.CallVisit{n.Name}(ast, id);");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- children of each kind, in field order, each slot read just before it is visited ----
        foreach (var n in s.Nodes)
        {
            sb.AppendLine(Inline);
            sb.AppendLine($"    internal static void WalkChildrenOf{n.Name}(AstVisitor v, JsAst ast, int id)");
            sb.AppendLine("    {");
            foreach (var f in n.Fields)
            {
                if (f.IsList)
                    sb.AppendLine($"        Walk{Schema.ListVisit[f.Type].Method[5..]}(v, ast, id, {f.Slot});");
                else if (f.IsChild)
                    sb.AppendLine($"        {{ int c = ast._nodes[id].{SlotNames[f.Slot]}; {VisitChild(f.Type, "c", f.Kind == FK.OptChild)} }}");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- composite unions ----
        foreach (var u in unions.Where(u => u.IsComposite))
        {
            sb.AppendLine($"    private static void Walk{u.Name}Variants(AstVisitor v, JsAst ast, int id)");
            sb.AppendLine("    {");
            sb.AppendLine("        NodeKind kind = ast._nodes[id].Kind;");
            bool first = true;
            foreach (var variant in u.Variants)
            {
                string test = s.NodeByName.ContainsKey(variant) ? $"kind == NodeKind.{variant}" : $"NodeKinds.Is{variant}(kind)";
                sb.AppendLine($"        {(first ? "if" : "else if")} ({test}) {{ {VisitChild(variant, "id", optional: false)} }}");
                first = false;
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- lists ----
        foreach (var (elem, (method, _)) in lists)
        {
            string name = method[5..];
            sb.AppendLine(Inline);
            sb.AppendLine($"    private static void Walk{name}(AstVisitor v, JsAst ast, int owner, int slot)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (v.Ov(Hook{name})) v.Call{method}(ast, owner, slot);");
            sb.AppendLine($"        else Walk{name}Body(v, ast, owner, slot);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    private static void Walk{name}Body(AstVisitor v, JsAst ast, int owner, int slot)");
            sb.AppendLine("    {");
            sb.AppendLine("        // The range is re-read every step: hooks may edit the list (or grow the arena) during the walk.");
            sb.AppendLine("        for (int i = 0; ; i++)");
            sb.AppendLine("        {");
            sb.AppendLine("            ref int range = ref Unsafe.Add(ref ast._nodes[owner].A, slot);");
            sb.AppendLine("            if (i >= Unsafe.Add(ref range, 1)) return;");
            sb.AppendLine("            int e = ast._lists[range + i];");
            sb.AppendLine($"            {VisitChild(elem, "e", optional: true)}");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- public hooks (defaults) ----
        foreach (var u in unions)
        {
            sb.AppendLine($"    /// <summary>Called for every <see cref=\"{u.Name}\"/> slot; the default calls the method for the concrete kind.</summary>");
            sb.AppendLine($"    public virtual void Visit{u.Name}({u.Name} node)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (node._id != 0) {(u.IsComposite ? $"Walk{u.Name}Variants" : "Walk")}(this, node._ast, node._id);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        foreach (var (elem, (method, _)) in lists)
        {
            sb.AppendLine($"    /// <summary>Visits each non-none element of a <see cref=\"{elem}\"/> list.</summary>");
            sb.AppendLine($"    public virtual void {method}(NodeList<{elem}> list)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (list._owner != 0) Walk{method[5..]}Body(this, list._ast, list._owner, list._slot);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        foreach (var n in s.Nodes)
        {
            sb.AppendLine($"    /// <summary>Visits a <see cref=\"{n.Name}\"/>; the default visits its children.</summary>");
            sb.AppendLine($"    public virtual void Visit{n.Name}({n.Name} node) => WalkChildrenOf{n.Name}(this, node._ast, node._id);");
            sb.AppendLine();
        }
        sb.Length -= Environment.NewLine.Length;
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---- transformer ----------------------------------------------------------------------------------
    //
    // The walk mirrors the visitor's (same hooks, order and skipping), but every step returns the id now in the slot:
    // hooks take their node by ref, and a changed id is stored back into the parent's field or list element. The
    // slot of a node (owner id + slot code) is passed only to the thunk that calls a hook, where it becomes the
    // "current slot" behind Remove/InsertBefore/InsertAfter/SlotOf, tied to the local the thunk passes by ref. Slot
    // code: a field's slot index, or (index << 4) | 8 | listSlot for a list element.
    public static string Transformer(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace cAST;");
        sb.AppendLine();
        sb.AppendLine("public abstract unsafe partial class AstTransformer");
        sb.AppendLine("{");

        var unions = s.Unions;
        var lists = Schema.ListVisit.ToList();
        const string Inline = "    [MethodImpl(MethodImplOptions.AggressiveInlining)]";
        const string NoInline = "    [MethodImpl(MethodImplOptions.NoInlining)]";

        for (int i = 0; i < unions.Count; i++)
            sb.AppendLine($"    private const int Hook{unions[i].Name} = NodeKinds.Count + {i};");
        for (int i = 0; i < lists.Count; i++)
            sb.AppendLine($"    private const int Hook{lists[i].Value.Method[5..]} = NodeKinds.Count + {unions.Count + i};");
        sb.AppendLine($"    internal const int HookCount = NodeKinds.Count + {unions.Count + lists.Count};");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Every overridable hook: its index in the override table, its name and its parameter type.</summary>");
        sb.AppendLine("    internal static readonly (int Hook, string Method, Type Parameter)[] Hooks =");
        sb.AppendLine("    [");
        foreach (var n in s.Nodes) sb.AppendLine($"        ((int)NodeKind.{n.Name}, \"Visit{n.Name}\", typeof({n.Name}).MakeByRefType()),");
        foreach (var u in unions) sb.AppendLine($"        (Hook{u.Name}, \"Visit{u.Name}\", typeof({u.Name}).MakeByRefType()),");
        foreach (var (elem, (method, _)) in lists) sb.AppendLine($"        (Hook{method[5..]}, \"{method}\", typeof(NodeList<{elem}>)),");
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>One flag per hook, stored inline in the transformer so a check is a single byte compare.</summary>");
        sb.AppendLine("    [InlineArray(HookCount)]");
        sb.AppendLine("    private struct HookFlags");
        sb.AppendLine("    {");
        sb.AppendLine("        private bool _element0;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Thunks: enter the node's slot, call the hook with a local view, store nothing (the walker stores the result).
        foreach (var n in s.Nodes)
        {
            sb.AppendLine(NoInline);
            sb.AppendLine($"    private int CallVisit{n.Name}(JsAst ast, int id, int owner, int code)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var node = new {n.Name}(ast, id);");
            sb.AppendLine("        var saved = Enter(ast, owner, code, ref node);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine($"            Visit{n.Name}(ref node);");
            sb.AppendLine("            return Result(ast, id, node._ast, node._id);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            _slot = saved;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
        foreach (var u in unions)
        {
            sb.AppendLine(NoInline);
            sb.AppendLine($"    private int CallVisit{u.Name}(JsAst ast, int id, int owner, int code)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var node = new {u.Name}(ast, id);");
            sb.AppendLine("        var saved = Enter(ast, owner, code, ref node);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine($"            Visit{u.Name}(ref node);");
            sb.AppendLine("            return Result(ast, id, node._ast, node._id);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            _slot = saved;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
        foreach (var (elem, (method, _)) in lists)
        {
            sb.AppendLine(NoInline);
            sb.AppendLine($"    private void Call{method}(JsAst ast, int owner, int slot) => {method}(new NodeList<{elem}>(ast, owner, slot));");
        }
        sb.AppendLine();

        // Statements that visit the child id held in `c` (static type `type`, in slot `code` of `owner`) and leave
        // the resulting id in `n`. Union hooks are called even for a none child of a required field (as in the
        // visitor); optional fields skip none children.
        string VisitChild(string type, string c, string owner, string code, bool optional)
        {
            string call;
            if (s.IsUnion(type))
            {
                string walk = s.UnionByName[type].IsComposite ? $"Walk{type}Variants(v, ast, {c}, {owner}, {code})" : $"Walk(v, ast, {c}, {owner}, {code})";
                call = optional
                    ? $"n = v.Ov(Hook{type}) ? v.CallVisit{type}(ast, {c}, {owner}, {code}) : {walk};"
                    : $"n = v.Ov(Hook{type}) ? v.CallVisit{type}(ast, {c}, {owner}, {code}) : {c} != 0 ? {walk} : 0;";
            }
            else
            {
                call = $"if (v.Ov((int)NodeKind.{type})) n = v.CallVisit{type}(ast, {c}, {owner}, {code}); else {{ WalkChildrenOf{type}(v, ast, {c}); n = {c}; }}";
            }
            return optional ? $"int n = {c}; if ({c} != 0) {{ {call} }}" : $"int n; {call}";
        }

        // ---- the walk ----
        sb.AppendLine("    // Per-kind walkers, indexed by NodeKind; each returns the id left in the slot. Padded to 256 entries.");
        sb.AppendLine("    private static readonly delegate*<AstTransformer, JsAst, int, int, int, int>* s_walkers = CreateWalkers();");
        sb.AppendLine();
        sb.AppendLine("    private static delegate*<AstTransformer, JsAst, int, int, int, int>* CreateWalkers()");
        sb.AppendLine("    {");
        sb.AppendLine("        var table = (delegate*<AstTransformer, JsAst, int, int, int, int>*)NativeMemory.Alloc(256, (nuint)sizeof(nint));");
        sb.AppendLine("        for (int i = 0; i < 256; i++) table[i] = &WalkNone;");
        foreach (var n in s.Nodes) sb.AppendLine($"        table[(int)NodeKind.{n.Name}] = &Walk{n.Name};");
        sb.AppendLine("        return table;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Transforms node <paramref name=\"id\"/> (non-zero) in slot <paramref name=\"code\"/> of <paramref name=\"owner\"/>; returns the id now in that slot.</summary>");
        sb.AppendLine(Inline);
        sb.AppendLine("    internal static int Walk(AstTransformer v, JsAst ast, int id, int owner, int code)");
        sb.AppendLine("    {");
        sb.AppendLine("        NodeKind kind = ast._nodes[id].Kind;");
        sb.AppendLine("        if (kind == NodeKind.Identifier)");
        sb.AppendLine("            return v.Ov((int)NodeKind.Identifier) ? v.CallVisitIdentifier(ast, id, owner, code) : id;");
        sb.AppendLine("        if (v.Skip(kind)) return id;");
        sb.AppendLine("        return s_walkers[(int)kind](v, ast, id, owner, code);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static int WalkNone(AstTransformer v, JsAst ast, int id, int owner, int code) => id;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Transforms node <paramref name=\"id\"/> (non-zero) as a child of static type <typeparamref name=\"T\"/> that has no slot; returns the id left in its place.</summary>");
        sb.AppendLine("    private int WalkAs<T>(JsAst ast, int id) where T : struct, INode<T>");
        sb.AppendLine("    {");
        foreach (var u in unions)
        {
            string walk = u.IsComposite ? $"Walk{u.Name}Variants" : "Walk";
            sb.AppendLine($"        if (typeof(T) == typeof({u.Name})) return Ov(Hook{u.Name}) ? CallVisit{u.Name}(ast, id, 0, 0) : {walk}(this, ast, id, 0, 0);");
        }
        sb.AppendLine("        return Walk(this, ast, id, 0, 0);");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var n in s.Nodes)
        {
            bool hasChildren = n.Fields.Any(f => f.IsChild || f.IsList);
            sb.AppendLine($"    private static int Walk{n.Name}(AstTransformer v, JsAst ast, int id, int owner, int code)");
            sb.AppendLine("    {");
            if (hasChildren)
            {
                sb.AppendLine("        byte probe;");
                sb.AppendLine("        if ((nuint)((nint)(&probe) - v._stackLimit) >= StackWindow && v.StackIsLow()) return v.WalkOnNewStack(ast, id, owner, code);");
                sb.AppendLine($"        if (v.Ov((int)NodeKind.{n.Name})) return v.CallVisit{n.Name}(ast, id, owner, code);");
                sb.AppendLine($"        WalkChildrenOf{n.Name}(v, ast, id);");
                sb.AppendLine("        return id;");
            }
            else
            {
                sb.AppendLine($"        return v.Ov((int)NodeKind.{n.Name}) ? v.CallVisit{n.Name}(ast, id, owner, code) : id;");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- children of each kind: each slot read just before it is visited, a changed id stored back ----
        foreach (var n in s.Nodes)
        {
            sb.AppendLine(Inline);
            sb.AppendLine($"    internal static void WalkChildrenOf{n.Name}(AstTransformer v, JsAst ast, int id)");
            sb.AppendLine("    {");
            foreach (var f in n.Fields)
            {
                if (f.IsList)
                {
                    sb.AppendLine($"        Walk{Schema.ListVisit[f.Type].Method[5..]}(v, ast, id, {f.Slot});");
                }
                else if (f.IsChild)
                {
                    bool optional = f.Kind == FK.OptChild;
                    sb.AppendLine($"        {{ int c = ast._nodes[id].{SlotNames[f.Slot]}; {VisitChild(f.Type, "c", "id", f.Slot.ToString(), optional)} if (n != c) StoreField(ast, id, {f.Slot}, c, n, noneAllowed: {(optional ? "true" : "false")}); }}");
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- composite unions ----
        foreach (var u in unions.Where(u => u.IsComposite))
        {
            sb.AppendLine($"    private static int Walk{u.Name}Variants(AstTransformer v, JsAst ast, int id, int owner, int code)");
            sb.AppendLine("    {");
            sb.AppendLine("        NodeKind kind = ast._nodes[id].Kind;");
            foreach (var variant in u.Variants)
            {
                string test = s.NodeByName.ContainsKey(variant) ? $"kind == NodeKind.{variant}" : $"NodeKinds.Is{variant}(kind)";
                sb.AppendLine($"        if ({test}) {{ {VisitChild(variant, "id", "owner", "code", optional: false)} return n; }}");
            }
            sb.AppendLine("        return id;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- lists ----
        foreach (var (elem, (method, _)) in lists)
        {
            string name = method[5..];
            sb.AppendLine(Inline);
            sb.AppendLine($"    private static void Walk{name}(AstTransformer v, JsAst ast, int owner, int slot)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (v.Ov(Hook{name})) v.Call{method}(ast, owner, slot);");
            sb.AppendLine($"        else Walk{name}Body(v, ast, owner, slot);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    private static void Walk{name}Body(AstTransformer v, JsAst ast, int owner, int slot)");
            sb.AppendLine("    {");
            sb.AppendLine("        v.EnterList();");
            sb.AppendLine("        // The range is re-read every step: hooks may edit the list (or grow the arena) during the walk.");
            sb.AppendLine("        for (int i = 0; ; i++)");
            sb.AppendLine("        {");
            sb.AppendLine("            ref int range = ref Unsafe.Add(ref ast._nodes[owner].A, slot);");
            sb.AppendLine("            if (i >= Unsafe.Add(ref range, 1)) break;");
            sb.AppendLine("            int e = ast._lists[range + i];");
            sb.AppendLine("            if (e == 0) continue;");
            sb.AppendLine("            int code = (i << 4) | 8 | slot;");
            sb.AppendLine($"            {VisitChild(elem, "e", "owner", "code", optional: true)}");
            sb.AppendLine("            StoreElement(ast, owner, slot, i, e, n);");
            sb.AppendLine("        }");
            sb.AppendLine("        v.LeaveList(ast);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ---- public hooks (defaults) ----
        foreach (var u in unions)
        {
            string walk = u.IsComposite ? $"Walk{u.Name}Variants" : "Walk";
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Called for every <see cref=\"{u.Name}\"/> slot; the default calls the method for the concrete kind. Assigning");
            sb.AppendLine($"    /// <paramref name=\"node\"/> replaces the node in its slot when the hook returns.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public virtual void Visit{u.Name}(ref {u.Name} node)");
            sb.AppendLine("    {");
            sb.AppendLine("        int id = node._id;");
            sb.AppendLine("        if (id == 0) return;");
            sb.AppendLine("        var ast = CheckArena(node._ast);");
            sb.AppendLine($"        node = new {u.Name}(ast, {walk}(this, ast, id, OwnerFor(ref node), _slot.Code));");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        foreach (var (elem, (method, _)) in lists)
        {
            sb.AppendLine($"    /// <summary>Transforms each non-none element of a <see cref=\"{elem}\"/> list.</summary>");
            sb.AppendLine($"    public virtual void {method}(NodeList<{elem}> list)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (list._owner != 0) Walk{method[5..]}Body(this, CheckArena(list._ast), list._owner, list._slot);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        foreach (var n in s.Nodes)
        {
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Transforms a <see cref=\"{n.Name}\"/>; the default transforms its children. Assigning <paramref name=\"node\"/>");
            sb.AppendLine($"    /// replaces the node in its slot when the hook returns.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public virtual void Visit{n.Name}(ref {n.Name} node)");
            sb.AppendLine("    {");
            sb.AppendLine($"        if (node._id != 0) WalkChildrenOf{n.Name}(this, CheckArena(node._ast), node._id);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        sb.Length -= Environment.NewLine.Length;
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string Layout(Schema s)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("namespace cAST;");
        sb.AppendLine();
        sb.AppendLine("internal static class NodeLayout");
        sb.AppendLine("{");
        sb.AppendLine("    // Per kind: the payload slots holding references, encoded as (SlotKind << 4) | slotIndex.");
        sb.AppendLine("    // SlotKind: 1 = child node, 2 = list (slot = start, slot + 1 = count), 3 = string id, 4 = BigInt id,");
        sb.AppendLine("    // 5 = lazy string id (0 = a slice of the source), 6 = scope context (assigned by the resolver).");
        sb.AppendLine("    internal static readonly byte[][] Slots =");
        sb.AppendLine("    [");
        sb.AppendLine("        [], // None");
        foreach (var n in s.Nodes)
        {
            var entries = new List<string>();
            foreach (var f in n.Fields)
            {
                int kind = f.Kind switch
                {
                    FK.Child or FK.OptChild => 1,
                    FK.List or FK.HoleList => 2,
                    FK.Str or FK.OptStr => 3,
                    FK.BigInt => 4,
                    FK.LazyStr or FK.OptLazyStr => 5,
                    FK.Scope => 6,
                    _ => 0,
                };
                if (kind != 0) entries.Add($"0x{(kind << 4) | f.Slot:X2}");
            }
            sb.AppendLine($"        [{string.Join(", ", entries)}], // {n.Name}");
        }
        sb.AppendLine("    ];");
        sb.AppendLine();

        // The declared type of every child and list slot, for code that stores nodes into slots by index.
        sb.AppendLine("    /// <summary>The first type code of a union: codes below it are concrete <see cref=\"NodeKind\"/> values.</summary>");
        sb.AppendLine("    internal const int UnionTypeBase = 256;");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Type code flag: the slot may hold none (an optional child, or an element of a list with elisions).</summary>");
        sb.AppendLine("    internal const int NoneAllowed = 0x8000;");
        sb.AppendLine();
        for (int i = 0; i < s.Unions.Count; i++)
            sb.AppendLine($"    internal const int {s.Unions[i].Name}Type = UnionTypeBase + {i};");
        sb.AppendLine();
        sb.AppendLine("    // Per kind, parallel to Slots: the type code of each child or list slot (a NodeKind value or a union code,");
        sb.AppendLine("    // plus NoneAllowed), 0 for the other slots.");
        sb.AppendLine("    internal static readonly ushort[][] Types =");
        sb.AppendLine("    [");
        sb.AppendLine("        [], // None");
        foreach (var n in s.Nodes)
        {
            var entries = new List<string>();
            foreach (var f in n.Fields)
            {
                bool counted = f.Kind is FK.Child or FK.OptChild or FK.List or FK.HoleList or FK.Str or FK.OptStr or FK.BigInt or FK.LazyStr or FK.OptLazyStr or FK.Scope;
                if (!counted) continue;
                if (!f.IsChild && !f.IsList)
                {
                    entries.Add("0");
                    continue;
                }
                string type = s.IsUnion(f.Type) ? $"{f.Type}Type" : $"(int)NodeKind.{f.Type}";
                bool none = f.Kind is FK.OptChild or FK.HoleList;
                entries.Add(none ? $"{type} | NoneAllowed" : type);
            }
            sb.AppendLine(entries.Count == 0 ? $"        [], // {n.Name}" : $"        [{string.Join(", ", entries)}], // {n.Name}");
        }
        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Per kind: the payload slot holding the node's scope context, or -1.</summary>");
        sb.Append("    internal static ReadOnlySpan<sbyte> ScopeSlot => [-1");
        foreach (var n in s.Nodes)
        {
            var scope = n.Fields.FirstOrDefault(f => f.Kind == FK.Scope);
            sb.Append(", ").Append(scope is null ? -1 : scope.Slot);
        }
        sb.AppendLine("];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Whether a node of <paramref name=\"kind\"/> may be stored in a slot of type code <paramref name=\"type\"/> (the NoneAllowed flag is ignored).</summary>");
        sb.AppendLine("    internal static bool Accepts(int type, NodeKind kind)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (type & ~NoneAllowed)");
        sb.AppendLine("        {");
        foreach (var u in s.Unions)
            sb.AppendLine($"            case {u.Name}Type: return NodeKinds.Is{u.Name}(kind);");
        sb.AppendLine("            default: return (type & ~NoneAllowed) == (int)kind;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The name of type code <paramref name=\"type\"/>, for error messages.</summary>");
        sb.AppendLine("    internal static string TypeName(int type)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (type & ~NoneAllowed)");
        sb.AppendLine("        {");
        foreach (var u in s.Unions)
            sb.AppendLine($"            case {u.Name}Type: return \"{u.Name}\";");
        sb.AppendLine("            default: return ((NodeKind)(type & ~NoneAllowed)).ToString();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
