namespace cAST;

/// <summary>
/// The per-scope identity attached to every resolved <see cref="Identifier"/> that names a variable. The resolver
/// gives each lexical scope a value never used before in the arena; two identifiers with the same name refer to the
/// same binding exactly when they have the same context.
/// </summary>
public enum ScopeContext
{
    /// <summary>
    /// Not resolved; also the context of identifiers that do not name a variable (member property names such as
    /// the <c>b</c> of <c>a.b</c>, labels, and private names).
    /// </summary>
    Unresolved = 0,

    /// <summary>
    /// The program scope: top-level declarations, and every reference to a name no enclosing scope declares (a global
    /// such as <c>undefined</c> or <c>window</c>).
    /// </summary>
    TopLevel = 1,
}

/// <summary>The keyword of a <see cref="VariableDeclaration"/>.</summary>
public enum VarKind : byte
{
    /// <summary><c>var</c></summary>
    Var,

    /// <summary><c>let</c></summary>
    Let,

    /// <summary><c>const</c></summary>
    Const,
}

/// <summary>The kind of a class method definition.</summary>
public enum MethodKind : byte
{
    /// <summary>Not a method (used internally while parsing).</summary>
    None = 0,

    /// <summary>An ordinary method.</summary>
    Method = 1,

    /// <summary>A <c>get</c> accessor.</summary>
    Get = 2,

    /// <summary>A <c>set</c> accessor.</summary>
    Set = 3,
}

/// <summary>Discriminates the meta-properties <c>new.target</c> and <c>import.meta</c>.</summary>
public enum MetaPropertyKind : byte
{
    /// <summary><c>new.target</c></summary>
    NewTarget,

    /// <summary><c>import.meta</c></summary>
    ImportMeta,
}

/// <summary>Text helpers for the small AST enums.</summary>
public static class EnumText
{
    extension(VarKind kind)
    {
        /// <summary>The keyword as it appears in source.</summary>
        public string Text => kind switch
        {
            VarKind.Var => "var",
            VarKind.Let => "let",
            VarKind.Const => "const",
            _ => "",
        };
    }

    extension(MetaPropertyKind kind)
    {
        /// <summary>The meta-property as it appears in source.</summary>
        public string Text => kind == MetaPropertyKind.NewTarget ? "new.target" : "import.meta";
    }
}
