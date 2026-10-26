// C# guarantees definite assignment, so the prologue zeroing of locals is pure overhead; it matters for the
// recursive walkers, whose stack probe would otherwise be zeroed on every call.
[module: System.Runtime.CompilerServices.SkipLocalsInit]
