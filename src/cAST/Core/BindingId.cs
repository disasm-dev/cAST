namespace cAST;

/// <summary>
/// Identifies the binding a resolved <see cref="Identifier"/> refers to (go-fAST's <c>Identifier.ToId()</c>): two
/// identifiers of one arena refer to the same binding exactly when their binding ids are equal. Usable as a dictionary key.
/// </summary>
public readonly struct BindingId : IEquatable<BindingId>
{
    private readonly JsAst _ast;

    internal BindingId(JsAst ast, int nameId, ScopeContext scopeContext)
    {
        _ast = ast;
        NameId = nameId;
        ScopeContext = scopeContext;
    }

    /// <summary>The string-table id of the name.</summary>
    public int NameId { get; }

    /// <summary>The scope context the resolver gave the binding.</summary>
    public ScopeContext ScopeContext { get; }

    /// <summary>The name.</summary>
    public string Name => _ast.GetString(NameId);

    /// <inheritdoc/>
    public bool Equals(BindingId other) => NameId == other.NameId && ScopeContext == other.ScopeContext && ReferenceEquals(_ast, other._ast);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BindingId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(NameId, ScopeContext);

    /// <summary>Whether two ids refer to the same binding.</summary>
    public static bool operator ==(BindingId left, BindingId right) => left.Equals(right);

    /// <summary>Whether two ids refer to different bindings.</summary>
    public static bool operator !=(BindingId left, BindingId right) => !left.Equals(right);

    /// <summary>The name and scope context, for debugging.</summary>
    public override string ToString() => $"{Name}@{(int)ScopeContext}";
}

public readonly partial struct Identifier
{
    /// <summary>
    /// A number for the binding this identifier refers to, given by the resolver: identifiers it resolved refer to the
    /// same binding exactly when their numbers are equal, and every number is below <see cref="JsAst.BindingNumbers"/>, so
    /// a table indexed by it needs no hashing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The identifier is not resolved, or its scope context was set by hand (a factory or the setter) since the resolver
    /// gave it its number: run <see cref="Resolution.JsResolver.Resolve(JsAst)"/> again.
    /// </exception>
    public int BindingNumber
    {
        get
        {
            ref NodeRecord r = ref _ast._nodes[_id];
            if (r.B == (int)ScopeContext.Unresolved || r.E == 0 || r.F != r.B)
                throw new InvalidOperationException($"The identifier \"{Name}\" has no binding number: resolve the script again.");
            return r.E - 1;
        }
    }

    // Kept with the context it was given in, so a context set by hand since is caught.
    internal void SetBindingNumber(int number, ScopeContext context)
    {
        ref NodeRecord r = ref _ast._nodes[_id];
        r.E = number + 1;
        r.F = (int)context;
    }

    /// <summary>The binding this identifier refers to.</summary>
    /// <exception cref="InvalidOperationException">
    /// The identifier is not resolved: run <see cref="Resolution.JsResolver.Resolve(JsAst)"/> first (again after a transform
    /// adds identifiers). Member property names, labels and private names are never resolved.
    /// </exception>
    public BindingId BindingId
    {
        get
        {
            var scopeContext = ScopeContext;
            if (scopeContext == ScopeContext.Unresolved)
                throw new InvalidOperationException($"The identifier \"{Name}\" is not resolved, so it has no binding.");
            return new BindingId(_ast, _ast.IdentifierNameId(_id), scopeContext);
        }
    }
}
