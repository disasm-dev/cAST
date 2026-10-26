namespace cAST;

public readonly partial struct SwitchStatement
{
    /// <summary>The index of the <c>default:</c> case in <see cref="Body"/>, or -1. Computed from the cases, so it follows edits.</summary>
    public int Default
    {
        get
        {
            var cases = Body;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].Test.IsNone) return i;
            return -1;
        }
    }
}
