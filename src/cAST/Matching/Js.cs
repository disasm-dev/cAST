using System.Diagnostics.CodeAnalysis;

namespace cAST.Matching;

/// <summary>
/// Shape tests for the common JavaScript forms: each says whether a node has the shape and hands out its parts, so a
/// chain of <c>TryGet</c> calls and operator checks becomes one condition. Names are compared without allocating.
/// </summary>
public static partial class Js
{
    extension(Statement statement)
    {
        /// <summary>Whether the statement is <c>target = value;</c> with plain <c>=</c> and a target that is an expression.</summary>
        public bool IsAssignStatement(out Expression target, out Expression value)
        {
            if (statement.TryGetExpressionStatement(out var s)) return s.Expression.IsAssign(out target, out value);
            target = value = default;
            return false;
        }

        /// <summary>Whether the statement is a call on its own, <c>f(...);</c>.</summary>
        public bool IsCallStatement(out CallExpression call)
        {
            if (statement.TryGetExpressionStatement(out var s) && s.Expression.TryGetCallExpression(out call)) return true;
            call = default;
            return false;
        }

        /// <summary>Whether the statement is <c>break;</c> with no label.</summary>
        public bool IsPlainBreak() => statement.TryGetBreakStatement(out var b) && b.Label.IsNone;

        /// <summary>Whether the statement is <c>return returned;</c> (a bare <c>return;</c> is not).</summary>
        public bool IsReturnOf(out Expression returned)
        {
            if (statement.TryGetReturnStatement(out var r) && !r.Argument.IsNone)
            {
                returned = r.Argument;
                return true;
            }
            returned = default;
            return false;
        }

        /// <summary>Whether the statement is <c>name = value;</c> with plain <c>=</c>.</summary>
        public bool IsNameAssignment(out Identifier name, out Expression value)
        {
            if (statement.TryGetExpressionStatement(out var s)) return s.Expression.IsNameAssign(out name, out value);
            name = default;
            value = default;
            return false;
        }

        /// <summary>
        /// Whether the statement declares one name and nothing else, <c>var name = value;</c> (<c>let</c> and <c>const</c>
        /// as well); the value is none for <c>var name;</c>.
        /// </summary>
        public bool IsNameDeclaration(out Identifier name, out Expression value)
        {
            if (statement.TryGetVariableDeclaration(out var d) && d.List.Count == 1 && d.List[0].Target.TryGetIdentifier(out name))
            {
                value = d.List[0].Initializer;
                return true;
            }
            name = default;
            value = default;
            return false;
        }

        /// <summary>
        /// Whether the statement writes a name, as <see cref="IsNameAssignment"/> or <see cref="IsNameDeclaration"/> reads it:
        /// <c>name = value;</c> or <c>var name = value;</c>, the value none for <c>var name;</c>.
        /// </summary>
        public bool IsNameWrite(out Identifier name, out Expression value) =>
            statement.IsNameAssignment(out name, out value) || statement.IsNameDeclaration(out name, out value);
    }

    extension(Expression expression)
    {
        /// <summary>
        /// Whether the expression is <c>target = value</c> with plain <c>=</c> and a target that is an expression (an
        /// identifier or member access, not a destructuring pattern).
        /// </summary>
        public bool IsAssign(out Expression target, out Expression value)
        {
            if (expression.TryGetAssignExpression(out var a) && a.Operator == AssignmentOperator.Assign && NodeKinds.IsExpression(a.Left.Kind))
            {
                target = new Expression(a.Ast, a.Left.Id);
                value = a.Right;
                return true;
            }
            target = value = default;
            return false;
        }

        /// <summary>Whether the expression is <c>name = value</c> with plain <c>=</c>.</summary>
        public bool IsNameAssign(out Identifier name, out Expression value)
        {
            if (expression.IsAssign(out var target, out value) && target.TryGetIdentifier(out name)) return true;
            name = default;
            return false;
        }

        /// <summary>
        /// The expression whose value the expression gives: a sequence's last, followed into sequences, so <c>(a, b)</c> gives
        /// <c>b</c>; any other expression is its own.
        /// </summary>
        public Expression Last()
        {
            var last = expression;
            while (last.TryGetSequenceExpression(out var sequence) && sequence.Sequence.Count > 0)
                last = sequence.Sequence[sequence.Sequence.Count - 1];
            return last;
        }

        /// <summary>Whether the expression is a number literal holding a whole number from 0 to <see cref="int.MaxValue"/>.</summary>
        public bool IsWholeNumber(out int value)
        {
            if (expression.TryGetNumberLiteral(out var number) && number.Value is >= 0 and <= int.MaxValue && number.Value == Math.Floor(number.Value))
            {
                value = (int)number.Value;
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>Whether the expression is <c>calleeName(args)</c>, a call of the identifier <paramref name="calleeName"/>.</summary>
        public bool IsCall(string calleeName, out NodeList<Expression> args)
        {
            if (expression.TryGetCallExpression(out var call) && call.Callee.IsIdentifierNamed(calleeName))
            {
                args = call.ArgumentList;
                return true;
            }
            args = default;
            return false;
        }

        /// <summary>Whether the expression is <c>owner.method(args)</c> or <c>owner["method"](args)</c>.</summary>
        public bool IsMethodCall(string method, out Expression owner, out NodeList<Expression> args)
        {
            if (expression.TryGetCallExpression(out var call) && MemberKey(call.Callee, out owner, out int key)
                && expression.Ast.GetLazySpan(key, 0).SequenceEqual(method))
            {
                args = call.ArgumentList;
                return true;
            }
            owner = default;
            args = default;
            return false;
        }

        /// <summary>Whether the expression is <c>owner.key</c> or <c>owner["key"]</c> (a string literal key).</summary>
        public bool IsMember(out Expression owner, [MaybeNullWhen(false)] out string key)
        {
            if (MemberKey(expression, out owner, out int id))
            {
                key = expression.Ast.GetLazyString(id, 0);
                return true;
            }
            key = null;
            return false;
        }

        /// <summary>Whether the expression is <c>owner[index]</c>, any computed access.</summary>
        public bool IsElement(out Expression owner, out Expression index)
        {
            if (expression.TryGetMemberExpression(out var m) && m.Property.TryGetComputedProperty(out var computed))
            {
                owner = m.Object;
                index = computed.Expr;
                return true;
            }
            owner = index = default;
            return false;
        }

        /// <summary>Whether the expression is <c>left op right</c>.</summary>
        public bool IsBinary(BinaryOperator op, out Expression left, out Expression right)
        {
            if (expression.TryGetBinaryExpression(out var b) && b.Operator == op)
            {
                left = b.Left;
                right = b.Right;
                return true;
            }
            left = right = default;
            return false;
        }

        /// <summary>Whether the expression is <c>op operand</c>.</summary>
        public bool IsUnary(UnaryOperator op, out Expression operand)
        {
            if (expression.TryGetUnaryExpression(out var u) && u.Operator == op)
            {
                operand = u.Operand;
                return true;
            }
            operand = default;
            return false;
        }

        /// <summary>
        /// Whether the expression is a number literal holding a whole number in <see cref="int"/> range, or <c>-</c> on one.
        /// <c>-0</c> is not: no <see cref="int"/> holds it.
        /// </summary>
        public bool IsInt32(out int value)
        {
            bool negated = expression.TryGetUnaryExpression(out var u) && u.Operator == UnaryOperator.Negation;
            if ((negated ? u.Operand : expression).TryGetNumberLiteral(out var number))
            {
                double v = negated ? -number.Value : number.Value;
                if (v >= int.MinValue && v <= int.MaxValue && v == Math.Truncate(v) && (v != 0 || !double.IsNegative(v)))
                {
                    value = (int)v;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        /// <summary>Whether the expression is a string literal.</summary>
        public bool IsString([MaybeNullWhen(false)] out string value)
        {
            if (expression.TryGetStringLiteral(out var s))
            {
                value = s.Value;
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>Whether the expression is <c>void 0</c>.</summary>
        public bool IsVoidZero() =>
            expression.IsUnary(UnaryOperator.Void, out var operand) && operand.TryGetNumberLiteral(out var n) && n.Value == 0;

        /// <summary>Whether the expression is <c>"text".split("separator")</c>.</summary>
        public bool IsSplit([MaybeNullWhen(false)] out string text, [MaybeNullWhen(false)] out string separator)
        {
            if (expression.IsMethodCall("split", out var owner, out var args) && args.Count == 1
                && owner.IsString(out text) && args[0].IsString(out separator))
                return true;
            text = separator = null;
            return false;
        }

        /// <summary>Whether the expression is the identifier <paramref name="name"/>.</summary>
        public bool IsIdentifierNamed(string name) => expression.TryGetIdentifier(out var id) && id.NameSpan.SequenceEqual(name);
    }

    extension(FunctionLiteral function)
    {
        /// <summary>Whether the function's body is one statement, <c>return returned;</c>.</summary>
        public bool ReturnsOnly(out Expression returned)
        {
            var body = function.Body.List;
            if (body.Count == 1) return body[0].IsReturnOf(out returned);
            returned = default;
            return false;
        }
    }

    extension(Identifier identifier)
    {
        /// <summary>Whether the two identifiers refer to the same binding: their <see cref="Identifier.BindingId"/>s are equal.</summary>
        /// <exception cref="InvalidOperationException">
        /// Either identifier is not resolved: run <see cref="Resolution.JsResolver.Resolve(JsAst)"/> first.
        /// </exception>
        public bool SameBinding(Identifier other) => identifier.BindingId == other.BindingId;
    }

    // The owner of a member access and the node holding its key: an Identifier or a StringLiteral, its text in lazy slot 0.
    private static bool MemberKey(Expression expression, out Expression owner, out int key)
    {
        if (expression.TryGetMemberExpression(out var m))
        {
            var property = m.Property;
            if (property.TryGetIdentifier(out var name))
            {
                owner = m.Object;
                key = name.Id;
                return true;
            }
            if (property.TryGetComputedProperty(out var computed) && computed.Expr.IsStringLiteral)
            {
                owner = m.Object;
                key = computed.Expr.Id;
                return true;
            }
        }
        owner = default;
        key = 0;
        return false;
    }
}
