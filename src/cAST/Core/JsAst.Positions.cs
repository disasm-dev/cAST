using System.Globalization;

namespace cAST;

public sealed partial class JsAst
{
    /// <summary>Offset of the first character belonging to node <paramref name="id"/> (go-fAST's <c>Idx0</c>).</summary>
    public int GetStart(int id)
    {
        // Loop down left spines instead of recursing, so deeply nested a+b+c+... stays O(1) stack.
        while (id != 0)
        {
            ref NodeRecord r = ref _nodes[id];
            switch (r.Kind)
            {
                case NodeKind.Program:
                    if (r.B == 0) return 0;
                    id = _lists[r.A];
                    continue;

                // Nodes that start at their first child.
                case NodeKind.AssignExpression:
                case NodeKind.BinaryExpression:
                case NodeKind.LogicalExpression:
                case NodeKind.CallExpression:
                case NodeKind.ConditionalExpression:
                case NodeKind.MemberExpression:
                case NodeKind.Optional:
                case NodeKind.OptionalChain:
                case NodeKind.PrivateDotExpression:
                case NodeKind.PrivateIdentifier:
                case NodeKind.SpreadElement:
                case NodeKind.ClassDeclaration:
                case NodeKind.ExpressionStatement:
                case NodeKind.FunctionDeclaration:
                case NodeKind.LabelledStatement:
                case NodeKind.VariableDeclarator:
                case NodeKind.PropertyGetter:
                case NodeKind.PropertyKeyValue:
                case NodeKind.PropertyMethod:
                case NodeKind.PropertySetter:
                case NodeKind.PropertyShort:
                case NodeKind.AssignmentPattern:
                case NodeKind.PatternKeyValue:
                case NodeKind.PatternShorthand:
                    id = r.A;
                    continue;

                case NodeKind.SequenceExpression:
                    if (r.B == 0) return 0;
                    id = _lists[r.A];
                    continue;

                case NodeKind.UpdateExpression:
                    if ((r.Flags & 1) != 0) { id = r.A; continue; } // postfix: starts at the operand
                    return r.B;

                case NodeKind.ComputedProperty:
                    if (r.B != 0) return r.B;
                    id = r.A;
                    continue;

                case NodeKind.ArrayLiteral: return r.C;
                case NodeKind.ArrowFunctionLiteral: return r.D;
                case NodeKind.AwaitExpression: return r.B;
                case NodeKind.BigIntLiteral: return r.C;
                case NodeKind.BooleanLiteral: return r.A;
                case NodeKind.ClassLiteral: return r.E;
                case NodeKind.FunctionLiteral: return r.E;
                case NodeKind.Identifier: return r.C;
                case NodeKind.InvalidExpression: return r.A;
                case NodeKind.MetaProperty: return r.A;
                case NodeKind.NewExpression: return r.D;
                case NodeKind.NullLiteral: return r.A;
                case NodeKind.NumberLiteral: return r.D;
                case NodeKind.ObjectLiteral: return r.C;
                case NodeKind.RegExpLiteral: return r.D;
                case NodeKind.StringLiteral: return r.C;
                case NodeKind.SuperExpression: return r.A;
                case NodeKind.TemplateLiteral: return r.F;
                case NodeKind.ThisExpression: return r.A;
                case NodeKind.UnaryExpression: return r.B;
                case NodeKind.YieldExpression: return r.B;
                case NodeKind.TemplateElement: return r.C;
                case NodeKind.BadStatement: return r.A;
                case NodeKind.BlockStatement: return r.D;
                case NodeKind.BreakStatement: return r.B;
                case NodeKind.ContinueStatement: return r.B;
                case NodeKind.DebuggerStatement: return r.A;
                case NodeKind.DoWhileStatement: return r.C;
                case NodeKind.EmptyStatement: return r.A;
                case NodeKind.ForStatement: return r.E;
                case NodeKind.ForInStatement: return r.D;
                case NodeKind.ForOfStatement: return r.D;
                case NodeKind.IfStatement: return r.D;
                case NodeKind.ReturnStatement: return r.B;
                case NodeKind.SwitchStatement: return r.D;
                case NodeKind.ThrowStatement: return r.B;
                case NodeKind.TryStatement: return r.D;
                case NodeKind.VariableDeclaration: return r.D;
                case NodeKind.WhileStatement: return r.C;
                case NodeKind.WithStatement: return r.C;
                case NodeKind.SwitchCase: return r.D;
                case NodeKind.CatchClause: return r.C;
                case NodeKind.ParameterList: return r.D;
                case NodeKind.FieldDefinition: return r.C;
                case NodeKind.MethodDefinition: return r.C;
                case NodeKind.ClassStaticBlock: return r.B;
                case NodeKind.ArrayPattern: return r.D;
                case NodeKind.ObjectPattern: return r.D;
                default: return 0;
            }
        }
        return 0;
    }

    /// <summary>Offset of the first character immediately after node <paramref name="id"/> (go-fAST's <c>Idx1</c>).</summary>
    public int GetEnd(int id)
    {
        // Loop down right spines instead of recursing.
        while (id != 0)
        {
            ref NodeRecord r = ref _nodes[id];
            switch (r.Kind)
            {
                case NodeKind.Program:
                    if (r.B == 0) return 0;
                    id = _lists[r.A + r.B - 1];
                    continue;

                case NodeKind.ArrayLiteral: return r.D + 1;
                case NodeKind.ArrowFunctionLiteral: id = r.B; continue;
                case NodeKind.AssignExpression: id = r.B; continue;
                case NodeKind.AwaitExpression: id = r.A; continue;
                case NodeKind.BigIntLiteral:
                    return r.C + (r.D > 0 ? r.D : r.B != 0 ? GetStringLength(r.B) : GetBigInt(r.A).ToString(CultureInfo.InvariantCulture).Length + 1);
                case NodeKind.BinaryExpression: id = r.B; continue;
                case NodeKind.BooleanLiteral: return r.A + ((r.Flags & 1) != 0 ? 4 : 5);
                case NodeKind.CallExpression: return r.E + 1;
                case NodeKind.ClassLiteral: return r.F + 1;
                case NodeKind.ConditionalExpression: id = r.C; continue;
                case NodeKind.FunctionLiteral: id = r.C; continue;
                case NodeKind.Identifier: return r.C + (r.D > 0 ? r.D : GetStringLength(r.A));
                case NodeKind.InvalidExpression: return r.B;
                case NodeKind.LogicalExpression: id = r.B; continue;
                case NodeKind.MemberExpression: id = r.B; continue;
                case NodeKind.MetaProperty: return r.A + ((MetaPropertyKind)r.Op).Text.Length;
                case NodeKind.NewExpression:
                    if (r.F != 0) return r.F + 1;
                    id = r.A;
                    continue;
                case NodeKind.NullLiteral: return r.A + 4;
                case NodeKind.NumberLiteral:
                    if (r.E > 0) return r.D + r.E;
                    if (r.C != 0) return r.D + GetStringLength(r.C);
                    return r.D + new NumberLiteral(this, id).Value.ToString("R", CultureInfo.InvariantCulture).Length;
                case NodeKind.ObjectLiteral: return r.D + 1;
                case NodeKind.Optional: id = r.A; continue;
                case NodeKind.OptionalChain: id = r.A; continue;
                case NodeKind.PrivateDotExpression: id = r.B; continue;
                case NodeKind.PrivateIdentifier: id = r.A; continue;
                case NodeKind.RegExpLiteral: return r.D + (r.E > 0 ? r.E : GetStringLength(r.A));
                case NodeKind.SequenceExpression:
                    if (r.B == 0) return 0;
                    id = _lists[r.A + r.B - 1];
                    continue;
                case NodeKind.SpreadElement: id = r.A; continue;
                case NodeKind.StringLiteral:
                    return r.C + (r.D > 0 ? r.D : r.B != 0 ? GetStringLength(r.B) : GetStringLength(r.A) + 2);
                case NodeKind.SuperExpression: return r.A + 5;
                case NodeKind.TemplateLiteral: return r.G + 1;
                case NodeKind.ThisExpression: return r.A + 4;
                case NodeKind.UnaryExpression: id = r.A; continue;
                case NodeKind.UpdateExpression:
                    if ((r.Flags & 1) != 0) return r.B + 2; // postfix: ends after the operator
                    id = r.A;
                    continue;
                case NodeKind.YieldExpression:
                    if (r.A != 0) { id = r.A; continue; }
                    return r.B + 5;
                case NodeKind.TemplateElement: return r.C + (r.D > 0 ? r.D : GetStringLength(r.A));

                case NodeKind.BadStatement: return r.B;
                case NodeKind.BlockStatement: return r.E + 1;
                case NodeKind.BreakStatement:
                    if (r.A != 0) { id = r.A; continue; }
                    return r.B + 5;
                case NodeKind.ClassDeclaration: id = r.A; continue;
                case NodeKind.ContinueStatement:
                    if (r.A != 0) { id = r.A; continue; }
                    return r.B + 8;
                case NodeKind.DebuggerStatement: return r.A + 8;
                case NodeKind.DoWhileStatement: id = r.A; continue;
                case NodeKind.EmptyStatement: return r.A + 1;
                case NodeKind.ExpressionStatement: id = r.A; continue;
                case NodeKind.ForStatement: id = r.D; continue;
                case NodeKind.ForInStatement: id = r.C; continue;
                case NodeKind.ForOfStatement: id = r.C; continue;
                case NodeKind.FunctionDeclaration: id = r.A; continue;
                case NodeKind.IfStatement: id = r.C != 0 ? r.C : r.B; continue;
                case NodeKind.LabelledStatement: id = r.B; continue;
                case NodeKind.ReturnStatement:
                    if (r.A != 0) { id = r.A; continue; }
                    return r.B + 6;
                case NodeKind.SwitchStatement:
                    if (r.C > 0) { id = _lists[r.B + r.C - 1]; continue; }
                    id = r.A;
                    continue;
                case NodeKind.ThrowStatement: id = r.A; continue;
                case NodeKind.TryStatement: id = r.C != 0 ? r.C : r.B != 0 ? r.B : r.A; continue;
                case NodeKind.VariableDeclaration:
                    if (r.B == 0) return r.D + ((VarKind)r.Op).Text.Length;
                    id = _lists[r.A + r.B - 1];
                    continue;
                case NodeKind.WhileStatement: id = r.B; continue;
                case NodeKind.WithStatement: id = r.B; continue;

                case NodeKind.SwitchCase:
                    if (r.C > 0) { id = _lists[r.B + r.C - 1]; continue; }
                    if (r.A != 0) { id = r.A; continue; }
                    return r.D + 7;
                case NodeKind.CatchClause: id = r.B; continue;
                case NodeKind.VariableDeclarator: id = r.B != 0 ? r.B : r.A; continue;
                case NodeKind.ParameterList: return r.E + 1;
                case NodeKind.FieldDefinition: id = r.B != 0 ? r.B : r.A; continue;
                case NodeKind.MethodDefinition: id = r.B; continue;
                case NodeKind.ClassStaticBlock: id = r.A; continue;
                case NodeKind.ComputedProperty:
                    if (r.C != 0) return r.C + 1;
                    id = r.A;
                    continue;
                case NodeKind.PropertyGetter:
                case NodeKind.PropertyKeyValue:
                case NodeKind.PropertyMethod:
                case NodeKind.PropertySetter:
                case NodeKind.AssignmentPattern:
                case NodeKind.PatternKeyValue:
                    id = r.B;
                    continue;
                case NodeKind.PropertyShort:
                case NodeKind.PatternShorthand:
                    id = r.B != 0 ? r.B : r.A;
                    continue;
                case NodeKind.ArrayPattern: return r.E + 1;
                case NodeKind.ObjectPattern: return r.E + 1;
                default: return 0;
            }
        }
        return 0;
    }
}
