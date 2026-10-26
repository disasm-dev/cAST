using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace cAST;

/// <summary>
/// Base class for AST visitors. Every method's default implementation visits the node's children,
/// mirroring go-fAST's <c>NoopVisitor</c>: override the methods for the node kinds you care about
/// and call the base implementation (or <c>node.VisitChildren(this)</c>) to keep descending.
/// Union methods (<see cref="VisitExpression"/>, <see cref="VisitStatement"/>, ...) are called for every
/// slot of that union and dispatch to the concrete method; list methods skip none (elision) elements.
/// </summary>
/// <remarks>
/// The walk only makes virtual calls for the methods a subclass actually overrides (detected once per
/// visitor type); everything else is traversed inline, straight off the node records.
/// </remarks>
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
public abstract partial class AstVisitor
{
    private static readonly ConcurrentDictionary<Type, bool[]> s_overrides = new();

    // Indexed by hook id (see Hooks): whether this visitor's type overrides that Visit* method.
    private readonly HookFlags _ov;

    // Bit k set: kind k has no children and its Visit* method is not overridden, so reaching it does nothing.
    private readonly KindMask _skip;

    [InlineArray(2)]
    private struct KindMask
    {
        private ulong _element0;
    }

    /// <summary>Initializes the visitor (the set of overridden methods is computed once per type).</summary>
    protected AstVisitor()
    {
        Type type = GetType();
        if (!s_overrides.TryGetValue(type, out var ov))
        {
            ov = ComputeOverrides(type);
            s_overrides.TryAdd(type, ov);
        }
        ov.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<HookFlags, bool>(ref _ov), HookCount));
        for (int kind = 1; kind < NodeKinds.Count; kind++)
        {
            if (!ov[kind] && IsLeafKind(kind))
                _skip[kind >> 6] |= 1UL << (kind & 63);
        }
    }

    private static bool IsLeafKind(int kind)
    {
        foreach (byte slot in NodeLayout.Slots[kind])
            if ((slot >> 4) is 1 or 2) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Ov(int hook) => Unsafe.Add(ref Unsafe.As<HookFlags, bool>(ref Unsafe.AsRef(in _ov)), hook);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Skip(NodeKind kind)
    {
        uint k = (uint)kind;
        return ((Unsafe.Add(ref Unsafe.As<KindMask, ulong>(ref Unsafe.AsRef(in _skip)), (nint)(k >> 6)) >> (int)(k & 63)) & 1) != 0;
    }

    private static bool[] ComputeOverrides([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
    {
        var ov = new bool[HookCount];
        foreach (var (hook, method, parameter) in Hooks)
            ov[hook] = IsOverridden(type, method, parameter);
        return ov;
    }

    private static bool IsOverridden([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type, string name, Type parameter)
    {
        try
        {
            // Anything other than AstVisitor's own declaration counts as overridden (a method hidden with `new`
            // included): a false positive only costs a virtual call that lands in the default implementation.
            MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [parameter]);
            return method is null || method.DeclaringType != typeof(AstVisitor);
        }
        catch (AmbiguousMatchException)
        {
            return true;
        }
    }

    // Deeply nested input (obfuscators produce thousands of nesting levels) would overflow the 1 MB default
    // stack. Every walk step compares the stack pointer with a window known to be safe; outside it (deeper, or
    // on another thread's stack) the runtime is asked how much stack remains, and when it runs low the rest of
    // the subtree is walked on a fresh thread with a large stack (go-fAST gets this from goroutine stack growth).
    private const nuint StackWindow = 256 * 1024;
    private const int StackSlack = 64 * 1024;
    private nint _stackLimit;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe bool StackIsLow()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return true;
        byte probe;
        _stackLimit = (nint)(&probe) - StackSlack;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WalkOnNewStack(JsAst ast, int id)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Walk(this, ast, id); }
            catch (Exception e) { error = e; }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
