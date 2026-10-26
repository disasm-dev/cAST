namespace cAST;

/// <summary>The operator in an assignment expression.</summary>
public enum AssignmentOperator : byte
{
    /// <summary><c>=</c></summary>
    Assign,
    /// <summary><c>+=</c></summary>
    Addition,
    /// <summary><c>-=</c></summary>
    Subtraction,
    /// <summary><c>*=</c></summary>
    Multiplication,
    /// <summary><c>/=</c></summary>
    Division,
    /// <summary><c>%=</c></summary>
    Remainder,
    /// <summary><c>**=</c></summary>
    Exponential,
    /// <summary><c>&lt;&lt;=</c></summary>
    ShiftLeft,
    /// <summary><c>&gt;&gt;=</c></summary>
    ShiftRight,
    /// <summary><c>&gt;&gt;&gt;=</c></summary>
    UnsignedShiftRight,
    /// <summary><c>|=</c></summary>
    BitwiseOr,
    /// <summary><c>^=</c></summary>
    BitwiseXor,
    /// <summary><c>&amp;=</c></summary>
    BitwiseAnd,
    /// <summary><c>||=</c></summary>
    LogicalOr,
    /// <summary><c>&amp;&amp;=</c></summary>
    LogicalAnd,
    /// <summary><c>??=</c></summary>
    LogicalNullish,
}

/// <summary>The operator in a binary expression. The short-circuiting operators live on <see cref="LogicalOperator"/>.</summary>
public enum BinaryOperator : byte
{
    /// <summary><c>==</c></summary>
    Equality,
    /// <summary><c>!=</c></summary>
    Inequality,
    /// <summary><c>===</c></summary>
    StrictEquality,
    /// <summary><c>!==</c></summary>
    StrictInequality,
    /// <summary><c>&lt;</c></summary>
    LessThan,
    /// <summary><c>&lt;=</c></summary>
    LessEqualThan,
    /// <summary><c>&gt;</c></summary>
    GreaterThan,
    /// <summary><c>&gt;=</c></summary>
    GreaterEqualThan,
    /// <summary><c>+</c></summary>
    Addition,
    /// <summary><c>-</c></summary>
    Subtraction,
    /// <summary><c>*</c></summary>
    Multiplication,
    /// <summary><c>/</c></summary>
    Division,
    /// <summary><c>%</c></summary>
    Remainder,
    /// <summary><c>**</c></summary>
    Exponential,
    /// <summary><c>&lt;&lt;</c></summary>
    ShiftLeft,
    /// <summary><c>&gt;&gt;</c></summary>
    ShiftRight,
    /// <summary><c>&gt;&gt;&gt;</c></summary>
    UnsignedShiftRight,
    /// <summary><c>|</c></summary>
    BitwiseOr,
    /// <summary><c>^</c></summary>
    BitwiseXor,
    /// <summary><c>&amp;</c></summary>
    BitwiseAnd,
    /// <summary><c>in</c></summary>
    In,
    /// <summary><c>instanceof</c></summary>
    Instanceof,
}

/// <summary>One of the short-circuiting binary operators.</summary>
public enum LogicalOperator : byte
{
    /// <summary><c>||</c></summary>
    Or,
    /// <summary><c>&amp;&amp;</c></summary>
    And,
    /// <summary><c>??</c></summary>
    Coalesce,
}

/// <summary>The <c>++</c> or <c>--</c> operator.</summary>
public enum UpdateOperator : byte
{
    /// <summary><c>++</c></summary>
    Increment,
    /// <summary><c>--</c></summary>
    Decrement,
}

/// <summary>The operator in a unary expression.</summary>
public enum UnaryOperator : byte
{
    /// <summary><c>+</c></summary>
    Plus,
    /// <summary><c>-</c></summary>
    Negation,
    /// <summary><c>!</c></summary>
    LogicalNot,
    /// <summary><c>~</c></summary>
    BitwiseNot,
    /// <summary><c>typeof</c></summary>
    Typeof,
    /// <summary><c>void</c></summary>
    Void,
    /// <summary><c>delete</c></summary>
    Delete,
}

/// <summary>
/// The precedence of an ECMAScript operator. Only the relative ordering is meaningful; the values mirror
/// esbuild's <c>js_ast.OpPrec</c>, derived bottom-up from the grammar starting at the comma operator.
/// </summary>
public enum Precedence : byte
{
    Lowest,
    Comma,
    Spread,
    Yield,
    Assign,
    Conditional,
    NullishCoalescing,
    LogicalOr,
    LogicalAnd,
    BitwiseOr,
    BitwiseXor,
    BitwiseAnd,
    Equals,
    Compare,
    Shift,
    Add,
    Multiply,
    Exponentiation,
    Prefix,
    Postfix,
    New,
    Call,
    Member,
}

/// <summary>Operator helpers (the Go methods on the operator types).</summary>
public static class OperatorExtensions
{
    private static readonly string[] AssignmentText =
        ["=", "+=", "-=", "*=", "/=", "%=", "**=", "<<=", ">>=", ">>>=", "|=", "^=", "&=", "||=", "&&=", "??="];

    private static readonly string[] BinaryText =
        ["==", "!=", "===", "!==", "<", "<=", ">", ">=", "+", "-", "*", "/", "%", "**", "<<", ">>", ">>>", "|", "^", "&", "in", "instanceof"];

    private static readonly string[] LogicalText = ["||", "&&", "??"];
    private static readonly string[] UpdateText = ["++", "--"];
    private static readonly string[] UnaryText = ["+", "-", "!", "~", "typeof", "void", "delete"];

    extension(AssignmentOperator op)
    {
        /// <summary>The operator as it appears in source.</summary>
        public string Text => (uint)op < (uint)AssignmentText.Length ? AssignmentText[(int)op] : "";

        /// <summary>Whether the operator is plain <c>=</c>.</summary>
        public bool IsAssign => op == AssignmentOperator.Assign;

        /// <summary>Whether the operator is one of <c>+= -= *= /= %= **=</c>.</summary>
        public bool IsArithmetic => op is AssignmentOperator.Addition or AssignmentOperator.Subtraction
            or AssignmentOperator.Multiplication or AssignmentOperator.Division
            or AssignmentOperator.Remainder or AssignmentOperator.Exponential;

        /// <summary>Whether the operator is one of <c>|= ^= &amp;= &lt;&lt;= &gt;&gt;= &gt;&gt;&gt;=</c>.</summary>
        public bool IsBitwise => op is AssignmentOperator.ShiftLeft or AssignmentOperator.ShiftRight
            or AssignmentOperator.UnsignedShiftRight or AssignmentOperator.BitwiseOr
            or AssignmentOperator.BitwiseXor or AssignmentOperator.BitwiseAnd;

        /// <summary>Whether the operator is one of <c>||= &amp;&amp;= ??=</c>.</summary>
        public bool IsLogical => op is AssignmentOperator.LogicalOr or AssignmentOperator.LogicalAnd or AssignmentOperator.LogicalNullish;

        /// <summary>The underlying binary operator of a compound arithmetic/bitwise assignment.</summary>
        public bool TryToBinaryOperator(out BinaryOperator result)
        {
            result = op switch
            {
                AssignmentOperator.Addition => BinaryOperator.Addition,
                AssignmentOperator.Subtraction => BinaryOperator.Subtraction,
                AssignmentOperator.Multiplication => BinaryOperator.Multiplication,
                AssignmentOperator.Division => BinaryOperator.Division,
                AssignmentOperator.Remainder => BinaryOperator.Remainder,
                AssignmentOperator.Exponential => BinaryOperator.Exponential,
                AssignmentOperator.ShiftLeft => BinaryOperator.ShiftLeft,
                AssignmentOperator.ShiftRight => BinaryOperator.ShiftRight,
                AssignmentOperator.UnsignedShiftRight => BinaryOperator.UnsignedShiftRight,
                AssignmentOperator.BitwiseOr => BinaryOperator.BitwiseOr,
                AssignmentOperator.BitwiseXor => BinaryOperator.BitwiseXor,
                AssignmentOperator.BitwiseAnd => BinaryOperator.BitwiseAnd,
                _ => (BinaryOperator)255,
            };
            return result != (BinaryOperator)255;
        }

        /// <summary>The underlying logical operator of <c>||= &amp;&amp;= ??=</c>.</summary>
        public bool TryToLogicalOperator(out LogicalOperator result)
        {
            switch (op)
            {
                case AssignmentOperator.LogicalOr: result = LogicalOperator.Or; return true;
                case AssignmentOperator.LogicalAnd: result = LogicalOperator.And; return true;
                case AssignmentOperator.LogicalNullish: result = LogicalOperator.Coalesce; return true;
                default: result = default; return false;
            }
        }
    }

    extension(BinaryOperator op)
    {
        /// <summary>The operator as it appears in source.</summary>
        public string Text => (uint)op < (uint)BinaryText.Length ? BinaryText[(int)op] : "";

        /// <summary>Whether the operator is one of <c>== != === !==</c>.</summary>
        public bool IsEquality => op is BinaryOperator.Equality or BinaryOperator.Inequality
            or BinaryOperator.StrictEquality or BinaryOperator.StrictInequality;

        /// <summary>Whether the operator is one of <c>&lt; &lt;= &gt; &gt;=</c>.</summary>
        public bool IsCompare => op is BinaryOperator.LessThan or BinaryOperator.LessEqualThan
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterEqualThan;

        /// <summary>Whether the operator is one of <c>+ - * / % **</c>.</summary>
        public bool IsArithmetic => op is BinaryOperator.Addition or BinaryOperator.Subtraction
            or BinaryOperator.Multiplication or BinaryOperator.Division
            or BinaryOperator.Remainder or BinaryOperator.Exponential;

        /// <summary>Whether the operator is one of <c>* / %</c>.</summary>
        public bool IsMultiplicative => op is BinaryOperator.Multiplication or BinaryOperator.Division or BinaryOperator.Remainder;

        /// <summary>Whether the operator is a bitwise operator, including the shifts.</summary>
        public bool IsBitwise => op is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight or BinaryOperator.UnsignedShiftRight
            or BinaryOperator.BitwiseOr or BinaryOperator.BitwiseXor or BinaryOperator.BitwiseAnd;

        /// <summary>Whether the operator is one of <c>&lt;&lt; &gt;&gt; &gt;&gt;&gt;</c>.</summary>
        public bool IsBitshift => op is BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight or BinaryOperator.UnsignedShiftRight;

        /// <summary>Whether the operator produces a number or string (arithmetic or bitwise).</summary>
        public bool IsNumericOrStringBinaryOperator => op.IsArithmetic || op.IsBitwise;

        /// <summary>Whether the operator is <c>in</c> or <c>instanceof</c>.</summary>
        public bool IsRelational => op is BinaryOperator.In or BinaryOperator.Instanceof;

        /// <summary>Whether the operator is spelled as a keyword (<c>in</c>, <c>instanceof</c>).</summary>
        public bool IsKeyword => op is BinaryOperator.In or BinaryOperator.Instanceof;

        /// <summary>The operator's precedence.</summary>
        public Precedence Precedence => op switch
        {
            BinaryOperator.BitwiseOr => Precedence.BitwiseOr,
            BinaryOperator.BitwiseXor => Precedence.BitwiseXor,
            BinaryOperator.BitwiseAnd => Precedence.BitwiseAnd,
            BinaryOperator.Equality or BinaryOperator.Inequality or BinaryOperator.StrictEquality or BinaryOperator.StrictInequality => Precedence.Equals,
            BinaryOperator.LessThan or BinaryOperator.LessEqualThan or BinaryOperator.GreaterThan or BinaryOperator.GreaterEqualThan
                or BinaryOperator.Instanceof or BinaryOperator.In => Precedence.Compare,
            BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight or BinaryOperator.UnsignedShiftRight => Precedence.Shift,
            BinaryOperator.Addition or BinaryOperator.Subtraction => Precedence.Add,
            BinaryOperator.Multiplication or BinaryOperator.Division or BinaryOperator.Remainder => Precedence.Multiply,
            BinaryOperator.Exponential => Precedence.Exponentiation,
            _ => Precedence.Lowest,
        };

        /// <summary>The precedence one level below the operator's own.</summary>
        public Precedence LowerPrecedence => op.Precedence == Precedence.Lowest ? Precedence.Lowest : (Precedence)((int)op.Precedence - 1);

        /// <summary>The operator that flips a comparison (<c>&lt;</c> ↔ <c>&gt;</c>, <c>&lt;=</c> ↔ <c>&gt;=</c>).</summary>
        public bool TryGetCompareInverse(out BinaryOperator result)
        {
            switch (op)
            {
                case BinaryOperator.LessThan: result = BinaryOperator.GreaterThan; return true;
                case BinaryOperator.LessEqualThan: result = BinaryOperator.GreaterEqualThan; return true;
                case BinaryOperator.GreaterThan: result = BinaryOperator.LessThan; return true;
                case BinaryOperator.GreaterEqualThan: result = BinaryOperator.LessEqualThan; return true;
                default: result = default; return false;
            }
        }

        /// <summary>The operator that negates an equality test (<c>==</c> ↔ <c>!=</c>, <c>===</c> ↔ <c>!==</c>).</summary>
        public bool TryGetEqualityInverse(out BinaryOperator result)
        {
            switch (op)
            {
                case BinaryOperator.Equality: result = BinaryOperator.Inequality; return true;
                case BinaryOperator.Inequality: result = BinaryOperator.Equality; return true;
                case BinaryOperator.StrictEquality: result = BinaryOperator.StrictInequality; return true;
                case BinaryOperator.StrictInequality: result = BinaryOperator.StrictEquality; return true;
                default: result = default; return false;
            }
        }

        /// <summary>The compound-assignment form of the operator (e.g. <c>+</c> → <c>+=</c>).</summary>
        public bool TryToAssignmentOperator(out AssignmentOperator result)
        {
            result = op switch
            {
                BinaryOperator.Addition => AssignmentOperator.Addition,
                BinaryOperator.Subtraction => AssignmentOperator.Subtraction,
                BinaryOperator.Multiplication => AssignmentOperator.Multiplication,
                BinaryOperator.Division => AssignmentOperator.Division,
                BinaryOperator.Remainder => AssignmentOperator.Remainder,
                BinaryOperator.Exponential => AssignmentOperator.Exponential,
                BinaryOperator.ShiftLeft => AssignmentOperator.ShiftLeft,
                BinaryOperator.ShiftRight => AssignmentOperator.ShiftRight,
                BinaryOperator.UnsignedShiftRight => AssignmentOperator.UnsignedShiftRight,
                BinaryOperator.BitwiseOr => AssignmentOperator.BitwiseOr,
                BinaryOperator.BitwiseXor => AssignmentOperator.BitwiseXor,
                BinaryOperator.BitwiseAnd => AssignmentOperator.BitwiseAnd,
                _ => (AssignmentOperator)255,
            };
            return result != (AssignmentOperator)255;
        }
    }

    extension(LogicalOperator op)
    {
        /// <summary>The operator as it appears in source.</summary>
        public string Text => (uint)op < (uint)LogicalText.Length ? LogicalText[(int)op] : "";

        /// <summary>The matching <c>||=</c>, <c>&amp;&amp;=</c> or <c>??=</c>.</summary>
        public AssignmentOperator AssignmentOperator => op switch
        {
            LogicalOperator.Or => AssignmentOperator.LogicalOr,
            LogicalOperator.And => AssignmentOperator.LogicalAnd,
            LogicalOperator.Coalesce => AssignmentOperator.LogicalNullish,
            _ => AssignmentOperator.Assign,
        };

        /// <summary>The operator's precedence.</summary>
        public Precedence Precedence => op switch
        {
            LogicalOperator.Or => Precedence.LogicalOr,
            LogicalOperator.And => Precedence.LogicalAnd,
            LogicalOperator.Coalesce => Precedence.NullishCoalescing,
            _ => Precedence.Lowest,
        };
    }

    extension(UpdateOperator op)
    {
        /// <summary>The operator as it appears in source.</summary>
        public string Text => (uint)op < (uint)UpdateText.Length ? UpdateText[(int)op] : "";
    }

    extension(UnaryOperator op)
    {
        /// <summary>The operator as it appears in source.</summary>
        public string Text => (uint)op < (uint)UnaryText.Length ? UnaryText[(int)op] : "";

        /// <summary>Whether the operator is unary <c>+</c> or <c>-</c>.</summary>
        public bool IsArithmetic => op is UnaryOperator.Plus or UnaryOperator.Negation;

        /// <summary>Whether the operator is spelled as a keyword (<c>typeof</c>, <c>void</c>, <c>delete</c>).</summary>
        public bool IsKeyword => op is UnaryOperator.Typeof or UnaryOperator.Void or UnaryOperator.Delete;
    }

    extension(Precedence p)
    {
        /// <summary>Whether the precedence binds right-to-left.</summary>
        public bool IsRightAssociative => p is Precedence.Exponentiation or Precedence.Conditional or Precedence.Assign;

        /// <summary>Whether the precedence binds left-to-right.</summary>
        public bool IsLeftAssociative => !p.IsRightAssociative;
    }
}
