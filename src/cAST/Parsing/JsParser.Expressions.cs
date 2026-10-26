using System.Runtime.CompilerServices;

namespace cAST.Parsing;

// Expression parsing. The structure follows go-fAST's parser/expression.go (recursive descent with a Pratt
// loop for binary operators), with these changes:
//  * nodes are arena records created in one call once their children exist (no wrapper allocations);
//  * arrow functions reinterpret the already-parsed cover grammar for (), (a, b), ({a}) , (a = 1), (...r)
//    and async (a) instead of rewinding and re-parsing (go-fAST backtracks for all but a plain sequence);
//  * operator classification uses lookup tables instead of switch chains.
public sealed unsafe partial class JsParser
{
    // ---- operator tables ---------------------------------------------------------------------------

    // Left binding power of binary/logical operators (go-fAST parser/operator.go values), 0 = not binary.
    private const int PrecLowest = 0;
    private const int PrecNullishCoalescing = 12;
    private const int PrecCompare = 26;

    private static readonly byte[] s_binaryPrecedence = BuildBinaryPrecedence();
    private static readonly byte[] s_binaryOperator = BuildBinaryOperator();
    private static readonly byte[] s_unaryOperator = BuildUnaryOperator();
    private static readonly byte[] s_assignOperator = BuildAssignOperator();

    private const byte NotAnOperator = 0xFF;
    private const byte LogicalFlag = 0x80; // s_binaryOperator: high bit marks a LogicalOperator

    private static byte[] BuildBinaryPrecedence()
    {
        var t = new byte[256];
        t[(int)TokenKind.Coalesce] = 12;
        t[(int)TokenKind.LogicalOr] = 14;
        t[(int)TokenKind.LogicalAnd] = 16;
        t[(int)TokenKind.Or] = 18;
        t[(int)TokenKind.ExclusiveOr] = 20;
        t[(int)TokenKind.And] = 22;
        t[(int)TokenKind.Equal] = 24;
        t[(int)TokenKind.StrictEqual] = 24;
        t[(int)TokenKind.NotEqual] = 24;
        t[(int)TokenKind.StrictNotEqual] = 24;
        t[(int)TokenKind.Less] = 26;
        t[(int)TokenKind.Greater] = 26;
        t[(int)TokenKind.LessOrEqual] = 26;
        t[(int)TokenKind.GreaterOrEqual] = 26;
        t[(int)TokenKind.InstanceOf] = 26;
        t[(int)TokenKind.In] = 26;
        t[(int)TokenKind.ShiftLeft] = 28;
        t[(int)TokenKind.ShiftRight] = 28;
        t[(int)TokenKind.UnsignedShiftRight] = 28;
        t[(int)TokenKind.Plus] = 30;
        t[(int)TokenKind.Minus] = 30;
        t[(int)TokenKind.Multiply] = 32;
        t[(int)TokenKind.Slash] = 32;
        t[(int)TokenKind.Remainder] = 32;
        t[(int)TokenKind.Exponent] = 35; // odd: right-associative
        return t;
    }

    private static byte[] BuildBinaryOperator()
    {
        var t = new byte[256];
        Array.Fill(t, NotAnOperator);
        t[(int)TokenKind.Equal] = (byte)BinaryOperator.Equality;
        t[(int)TokenKind.NotEqual] = (byte)BinaryOperator.Inequality;
        t[(int)TokenKind.StrictEqual] = (byte)BinaryOperator.StrictEquality;
        t[(int)TokenKind.StrictNotEqual] = (byte)BinaryOperator.StrictInequality;
        t[(int)TokenKind.Less] = (byte)BinaryOperator.LessThan;
        t[(int)TokenKind.LessOrEqual] = (byte)BinaryOperator.LessEqualThan;
        t[(int)TokenKind.Greater] = (byte)BinaryOperator.GreaterThan;
        t[(int)TokenKind.GreaterOrEqual] = (byte)BinaryOperator.GreaterEqualThan;
        t[(int)TokenKind.Plus] = (byte)BinaryOperator.Addition;
        t[(int)TokenKind.Minus] = (byte)BinaryOperator.Subtraction;
        t[(int)TokenKind.Multiply] = (byte)BinaryOperator.Multiplication;
        t[(int)TokenKind.Slash] = (byte)BinaryOperator.Division;
        t[(int)TokenKind.Remainder] = (byte)BinaryOperator.Remainder;
        t[(int)TokenKind.Exponent] = (byte)BinaryOperator.Exponential;
        t[(int)TokenKind.ShiftLeft] = (byte)BinaryOperator.ShiftLeft;
        t[(int)TokenKind.ShiftRight] = (byte)BinaryOperator.ShiftRight;
        t[(int)TokenKind.UnsignedShiftRight] = (byte)BinaryOperator.UnsignedShiftRight;
        t[(int)TokenKind.Or] = (byte)BinaryOperator.BitwiseOr;
        t[(int)TokenKind.ExclusiveOr] = (byte)BinaryOperator.BitwiseXor;
        t[(int)TokenKind.And] = (byte)BinaryOperator.BitwiseAnd;
        t[(int)TokenKind.In] = (byte)BinaryOperator.In;
        t[(int)TokenKind.InstanceOf] = (byte)BinaryOperator.Instanceof;
        t[(int)TokenKind.LogicalOr] = LogicalFlag | (byte)LogicalOperator.Or;
        t[(int)TokenKind.LogicalAnd] = LogicalFlag | (byte)LogicalOperator.And;
        t[(int)TokenKind.Coalesce] = LogicalFlag | (byte)LogicalOperator.Coalesce;
        return t;
    }

    private static byte[] BuildUnaryOperator()
    {
        var t = new byte[256];
        Array.Fill(t, NotAnOperator);
        t[(int)TokenKind.Plus] = (byte)UnaryOperator.Plus;
        t[(int)TokenKind.Minus] = (byte)UnaryOperator.Negation;
        t[(int)TokenKind.Not] = (byte)UnaryOperator.LogicalNot;
        t[(int)TokenKind.BitwiseNot] = (byte)UnaryOperator.BitwiseNot;
        t[(int)TokenKind.Delete] = (byte)UnaryOperator.Delete;
        t[(int)TokenKind.Void] = (byte)UnaryOperator.Void;
        t[(int)TokenKind.Typeof] = (byte)UnaryOperator.Typeof;
        return t;
    }

    private static byte[] BuildAssignOperator()
    {
        var t = new byte[256];
        Array.Fill(t, NotAnOperator);
        t[(int)TokenKind.Assign] = (byte)AssignmentOperator.Assign;
        t[(int)TokenKind.AddAssign] = (byte)AssignmentOperator.Addition;
        t[(int)TokenKind.SubtractAssign] = (byte)AssignmentOperator.Subtraction;
        t[(int)TokenKind.MultiplyAssign] = (byte)AssignmentOperator.Multiplication;
        t[(int)TokenKind.ExponentAssign] = (byte)AssignmentOperator.Exponential;
        t[(int)TokenKind.QuotientAssign] = (byte)AssignmentOperator.Division;
        t[(int)TokenKind.RemainderAssign] = (byte)AssignmentOperator.Remainder;
        t[(int)TokenKind.AndAssign] = (byte)AssignmentOperator.BitwiseAnd;
        t[(int)TokenKind.OrAssign] = (byte)AssignmentOperator.BitwiseOr;
        t[(int)TokenKind.ExclusiveOrAssign] = (byte)AssignmentOperator.BitwiseXor;
        t[(int)TokenKind.ShiftLeftAssign] = (byte)AssignmentOperator.ShiftLeft;
        t[(int)TokenKind.ShiftRightAssign] = (byte)AssignmentOperator.ShiftRight;
        t[(int)TokenKind.UnsignedShiftRightAssign] = (byte)AssignmentOperator.UnsignedShiftRight;
        t[(int)TokenKind.LogicalAndAssign] = (byte)AssignmentOperator.LogicalAnd;
        t[(int)TokenKind.LogicalOrAssign] = (byte)AssignmentOperator.LogicalOr;
        t[(int)TokenKind.CoalesceAssign] = (byte)AssignmentOperator.LogicalNullish;
        return t;
    }

    // Positions of the parentheses of the most recent parenthesized expression, for arrow parameter lists.
    private int _parenOpen;
    private int _parenClose;

    // Token information about the most recent property key (see ParseObjectPropertyKey).
    private TokenKind _keyKind;
    private int _keyKnown;
    private string? _keyEscaped;
    private int _keyStart;
    private int _keyEnd;

    // ---- identifiers and binding names -------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ParseIdentifier()
    {
        int id = CurrentIdentifier();
        Next();
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsBindingId(TokenKind kind)
    {
        if (kind == TokenKind.Identifier) return true;
        if (kind == TokenKind.Await) return !_scope.AllowAwait;
        if (kind == TokenKind.Yield) return !_scope.AllowYield;
        return kind > TokenKind.EscapedReservedWord;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TokenToBindingId()
    {
        if (IsBindingId(_kind)) _kind = TokenKind.Identifier;
    }

    // ---- primary expressions -----------------------------------------------------------------------

    private int ParsePrimaryExpression()
    {
        int idx = _tokStart;
        switch (_kind)
        {
            case TokenKind.Identifier:
                return ParseIdentifier();
            case TokenKind.Null:
                Next();
                return _ast.CreateNullLiteral(idx);
            case TokenKind.Boolean:
                {
                    bool value = _tokKnown == KnownStrings.True;
                    Next();
                    return _ast.CreateBooleanLiteral(idx, value);
                }
            case TokenKind.String:
                {
                    int lit = CurrentStringLiteral(unquoted: false);
                    Next();
                    return lit;
                }
            case TokenKind.Number:
                {
                    int lit = CurrentNumberLiteral();
                    Next();
                    return lit;
                }
            case TokenKind.Slash:
            case TokenKind.QuotientAssign:
                {
                    var (literalLength, patternLength) = ScanRegExp();
                    Next();
                    return _ast.CreateRegExpLiteral(0, 0, 0, idx, literalLength, patternLength);
                }
            case TokenKind.LeftBrace:
                return ParseObjectLiteral();
            case TokenKind.LeftBracket:
                return ParseArrayLiteral();
            case TokenKind.LeftParenthesis:
                return ParseParenthesisedExpression();
            case TokenKind.NoSubstitutionTemplate:
            case TokenKind.TemplateHead:
                return ParseTemplateLiteral(0);
            case TokenKind.This:
                Next();
                return _ast.CreateThisExpression(idx);
            case TokenKind.Super:
                return ParseSuperProperty();
            case TokenKind.Async:
                {
                    int f = ParseMaybeAsyncFunction(false);
                    if (f != 0) return f;
                    break;
                }
            case TokenKind.Function:
                return ParseFunction(false, false, idx);
            case TokenKind.Class:
                return ParseClass(false);
        }

        if (IsBindingId(_kind))
        {
            // Contextual keywords used as identifiers (go-fAST gives these an empty name).
            return ParseIdentifier();
        }

        ErrorUnexpectedToken(_kind);
        NextStatement();
        return Invalid(idx, _tokStart);
    }

    private int CurrentStringLiteral(bool unquoted)
    {
        int value = _hasEscape ? _ast.Intern(_escaped!) : unquoted ? _tokKnown : 0;
        return _ast.CreateStringLiteral(value, 0, _tokStart, _tokEnd - _tokStart, unquoted);
    }

    private int CurrentNumberLiteral()
    {
        int start = _tokStart, length = _tokEnd - start;
        if (_numExact)
            return _ast.CreateNumberLiteral(_numValue, 0, start, length);

        var raw = new ReadOnlySpan<char>(_src + start, length);
        if (raw[^1] == 'n')
        {
            if (!NumberParsing.TryParseBigInt(raw, out var big))
            {
                Error("illegal BigInt literal");
                big = default;
            }
            return _ast.CreateBigIntLiteral(_ast.AddBigInt(big), 0, start, length);
        }

        if (!NumberParsing.TryParseNumber(raw, out double value))
        {
            Error("illegal numeric literal");
            value = 0;
        }
        return _ast.CreateNumberLiteral(value, 0, start, length);
    }

    private int ParseSuperProperty()
    {
        int idx = _tokStart;
        Next();
        switch (_kind)
        {
            case TokenKind.Period:
                {
                    Next();
                    if (!TokenKinds.IsIdentifierName(_kind))
                    {
                        Expect(TokenKind.Identifier);
                        NextStatement();
                        return Invalid(idx, _tokStart);
                    }
                    int prop = ParseIdentifier();
                    return _ast.CreateMemberExpression(_ast.CreateSuperExpression(idx), prop);
                }
            case TokenKind.LeftBracket:
                return ParseBracketMember(_ast.CreateSuperExpression(idx));
            case TokenKind.LeftParenthesis:
                return ParseCallExpression(_ast.CreateSuperExpression(idx));
            default:
                Error("'super' keyword unexpected here");
                NextStatement();
                return Invalid(idx, _tokStart);
        }
    }

    private int ParseParenthesisedExpression()
    {
        int errorCount = _errors.Count;
        int opening = Expect(TokenKind.LeftParenthesis);
        int mark = _scratchTop;
        int spreadAt = -1, spreadPos = 0, trailingCommaPos = -1;

        if (_kind != TokenKind.RightParenthesis)
        {
            while (true)
            {
                if (_kind == TokenKind.Ellipsis)
                {
                    // Only valid as an arrow rest parameter; the error is decided after ')'.
                    spreadPos = _tokStart;
                    Next();
                    int arg = ParseAssignmentExpression();
                    spreadAt = _scratchTop;
                    Push(_ast.CreateSpreadElement(arg));
                }
                else
                {
                    Push(ParseAssignmentExpression());
                }
                if (_kind != TokenKind.Comma) break;
                Next();
                if (_kind == TokenKind.RightParenthesis)
                {
                    trailingCommaPos = _tokStart; // valid only before '=>'
                    break;
                }
            }
        }

        int closing = Expect(TokenKind.RightParenthesis);
        _parenOpen = opening;
        _parenClose = closing;
        int n = _scratchTop - mark;

        if (_kind == TokenKind.Arrow)
        {
            // Arrow parameters: hand the raw cover grammar to ParseArrowFromCover.
            if (n == 1 && spreadAt < 0)
            {
                int single = _scratch[mark];
                _scratchTop = mark;
                return single;
            }
            return _ast.CreateSequenceExpression(FinishList(mark));
        }

        // Not an arrow function: report exactly what go-fAST reports.
        if (spreadAt >= 0)
        {
            ErrorAt(spreadPos, spreadPos, "Unexpected token ...");
            int spread = _scratch[spreadAt];
            _scratch[spreadAt] = Invalid(spreadPos, _ast.GetEnd(N(spread).A));
        }
        if (trailingCommaPos >= 0) ErrorAt(trailingCommaPos, trailingCommaPos, "Unexpected token )");

        if (n == 1 && _errors.Count == errorCount)
        {
            int single = _scratch[mark];
            _scratchTop = mark;
            return single;
        }
        if (n == 0)
        {
            ErrorUnexpectedToken(TokenKind.RightParenthesis);
            return Invalid(opening, _tokStart);
        }
        return _ast.CreateSequenceExpression(FinishList(mark));
    }

    // ---- object and array literals -----------------------------------------------------------------

    /// <summary>
    /// Parses a property key, returning the key node (0 on error). Token details of the key are left in
    /// <see cref="_keyKind"/>, <see cref="_keyKnown"/>, <see cref="_keyEscaped"/>, <see cref="_keyStart"/> and
    /// <see cref="_keyEnd"/> (kind <see cref="TokenKind.Illegal"/> for computed keys).
    /// </summary>
    private int ParseObjectPropertyKey()
    {
        if (_kind == TokenKind.LeftBracket)
        {
            int lb = _tokStart;
            Next();
            int expr = ParseAssignmentExpression();
            int rb = Expect(TokenKind.RightBracket);
            _keyKind = TokenKind.Illegal;
            _keyKnown = 0;
            _keyEscaped = null;
            _keyStart = lb;
            _keyEnd = rb + 1;
            return _ast.CreateComputedProperty(expr, lb, rb);
        }

        TokenKind kind = _kind;
        _keyKind = kind;
        _keyKnown = _tokKnown;
        _keyEscaped = _hasEscape ? _escaped : null;
        _keyStart = _tokStart;
        _keyEnd = _tokEnd;

        int key;
        switch (kind)
        {
            case TokenKind.String:
                key = CurrentStringLiteral(unquoted: false);
                break;
            case TokenKind.Number:
                key = CurrentNumberLiteral();
                break;
            case TokenKind.PrivateIdentifier:
                key = _ast.CreatePrivateIdentifier(_ast.CreateIdentifier(_tokKnown, ScopeContext.Unresolved, _tokStart, _tokEnd - _tokStart));
                break;
            default:
                if (TokenKinds.IsIdentifierName(kind))
                {
                    key = CurrentStringLiteral(unquoted: true);
                }
                else
                {
                    ErrorUnexpectedToken(kind);
                    key = 0;
                }
                break;
        }
        Next();
        return key;
    }

    /// <summary>Creates an Identifier node from the last property key's token.</summary>
    private int KeyIdentifier()
    {
        int name = _keyEscaped is not null ? _ast.Intern(_keyEscaped) : _keyKnown;
        return _ast.CreateIdentifier(name, ScopeContext.Unresolved, _keyStart, _keyEnd - _keyStart);
    }

    private bool KeyIsPlain(int known) => _keyKind is TokenKind.Identifier && _keyEscaped is null && _keyKnown == known;

    private int ParseObjectProperty()
    {
        if (_kind == TokenKind.Ellipsis)
        {
            Next();
            return _ast.CreateSpreadElement(ParseAssignmentExpression());
        }

        int keyStart = _tokStart;
        bool generator = false;
        if (_kind == TokenKind.Multiply)
        {
            generator = true;
            Next();
        }

        int key = ParseObjectPropertyKey();
        if (key == 0) return 0;
        TokenKind tkn = _keyKind;

        if (TokenKinds.IsIdentifierName(tkn) || tkn is TokenKind.String or TokenKind.Number or TokenKind.Illegal)
        {
            if (generator)
                return _ast.CreatePropertyMethod(key, ParseMethodDefinition(keyStart, MethodKind.Method, true, false));

            switch (_kind)
            {
                case TokenKind.LeftParenthesis:
                    return _ast.CreatePropertyMethod(key, ParseMethodDefinition(keyStart, MethodKind.Method, false, false));

                case TokenKind.Comma:
                case TokenKind.RightBrace:
                case TokenKind.Assign:
                    if (IsBindingId(tkn))
                    {
                        int name = KeyIdentifier();
                        int initializer = 0;
                        if (_kind == TokenKind.Assign)
                        {
                            Next();
                            initializer = ParseAssignmentExpression();
                        }
                        return _ast.CreatePropertyShort(name, initializer);
                    }
                    ErrorUnexpectedToken(_kind);
                    break;

                default:
                    bool isGet = KeyIsPlain(KnownStrings.Get), isSet = KeyIsPlain(KnownStrings.Set);
                    if ((isGet || isSet || tkn == TokenKind.Async) && _kind != TokenKind.Colon)
                    {
                        bool async = tkn == TokenKind.Async;
                        bool methodGenerator = false;
                        if (async && _kind == TokenKind.Multiply)
                        {
                            methodGenerator = true;
                            Next();
                        }
                        int innerKey = ParseObjectPropertyKey();
                        if (innerKey == 0) return 0;
                        if (async)
                            return _ast.CreatePropertyMethod(innerKey, ParseMethodDefinition(keyStart, MethodKind.Method, methodGenerator, true));
                        if (isGet)
                            return _ast.CreatePropertyGetter(innerKey, ParseMethodDefinition(keyStart, MethodKind.Get, false, false));
                        return _ast.CreatePropertySetter(innerKey, ParseMethodDefinition(keyStart, MethodKind.Set, false, false));
                    }
                    break;
            }
        }

        Expect(TokenKind.Colon);
        return _ast.CreatePropertyKeyValue(key, ParseAssignmentExpression());
    }

    private int ParseMethodDefinition(int keyStart, MethodKind kind, bool generator, bool async)
    {
        bool savedYield = _scope.AllowYield, savedAwait = _scope.AllowAwait;
        _scope.AllowYield = generator;
        _scope.AllowAwait = async;

        int parameters = ParseFunctionParameterList();
        ref NodeRecord pl = ref N(parameters);
        switch (kind)
        {
            case MethodKind.Get:
                if (pl.B > 0 || pl.C != 0) Error("Getter must not have any formal parameters.");
                break;
            case MethodKind.Set:
                if (pl.B != 1 || pl.C != 0) Error("Setter must have exactly one formal parameter.");
                break;
        }

        int body = ParseFunctionBlock(async, async, generator);
        _scope.AllowYield = savedYield;
        _scope.AllowAwait = savedAwait;
        return _ast.CreateFunctionLiteral(0, parameters, body, ScopeContext.Unresolved, keyStart, async, generator);
    }

    private int ParseObjectLiteral()
    {
        int lb = Expect(TokenKind.LeftBrace);
        int mark = _scratchTop;
        while (_kind != TokenKind.RightBrace && _kind != TokenKind.Eof)
        {
            int property = ParseObjectProperty();
            if (property != 0) Push(property);
            if (_kind != TokenKind.RightBrace)
                Expect(TokenKind.Comma);
            else
                break;
        }
        int rb = Expect(TokenKind.RightBrace);
        return _ast.CreateObjectLiteral(FinishList(mark), lb, rb);
    }

    private int ParseArrayLiteral()
    {
        int lb = Expect(TokenKind.LeftBracket);
        int mark = _scratchTop;
        while (_kind != TokenKind.RightBracket && _kind != TokenKind.Eof)
        {
            if (_kind == TokenKind.Comma)
            {
                Next();
                Push(0); // elision
                continue;
            }
            if (_kind == TokenKind.Ellipsis)
            {
                Next();
                Push(_ast.CreateSpreadElement(ParseAssignmentExpression()));
            }
            else
            {
                Push(ParseAssignmentExpression());
            }
            if (_kind != TokenKind.RightBracket) Expect(TokenKind.Comma);
        }
        int rb = Expect(TokenKind.RightBracket);
        return _ast.CreateArrayLiteral(FinishList(mark), lb, rb);
    }

    // ---- templates ---------------------------------------------------------------------------------

    private int ParseTemplateLiteral(int tag)
    {
        int open = _tokStart;
        int close = 0;
        // Elements and expressions are pushed interleaved (e0, x0, e1, x1, ..., en) and split at the end.
        int mark = _scratchTop;

        while (true)
        {
            int start = _tokStart;
            TokenKind kind = _kind;
            bool tail = kind is TokenKind.NoSubstitutionTemplate or TokenKind.TemplateTail;
            int literalLength = Math.Max(0, _tokEnd - start - (tail ? 2 : 3));
            int parsed = _hasEscape ? _ast.AddString(_escaped!) : 0;
            Push(_ast.CreateTemplateElement(0, parsed, start, literalLength));

            if (tail)
            {
                close = _tokEnd - 1;
                Next();
                break;
            }

            Next();
            Push(ParseExpression());

            if (_kind != TokenKind.RightBrace)
            {
                ErrorUnexpectedToken(_kind);
                break;
            }
            NextTemplatePart();
        }

        int total = _scratchTop - mark;
        int elementCount = (total + 1) / 2, expressionCount = total / 2;
        int elements = _ast.AllocList(elementCount);
        int expressions = expressionCount == 0 ? 0 : _ast.AllocList(expressionCount);
        int[] lists = _ast._lists;
        for (int i = 0; i < total; i++)
        {
            int id = _scratch[mark + i];
            if ((i & 1) == 0) lists[elements + (i >> 1)] = id;
            else lists[expressions + (i >> 1)] = id;
        }
        _scratchTop = mark;

        return _ast.CreateTemplateLiteral(tag, new ListRange(elements, elementCount),
            expressionCount == 0 ? default : new ListRange(expressions, expressionCount), open, close);
    }

    // ---- calls and members -------------------------------------------------------------------------

    private ListRange ParseArgumentList(out int lp, out int rp)
    {
        lp = Expect(TokenKind.LeftParenthesis);
        int mark = _scratchTop;
        while (_kind != TokenKind.RightParenthesis)
        {
            if (_kind == TokenKind.Ellipsis)
            {
                Next();
                Push(_ast.CreateSpreadElement(ParseAssignmentExpression()));
            }
            else
            {
                Push(ParseAssignmentExpression());
            }
            if (_kind != TokenKind.Comma) break;
            Next();
        }
        rp = Expect(TokenKind.RightParenthesis);
        return FinishList(mark);
    }

    private int ParseCallExpression(int callee)
    {
        var args = ParseArgumentList(out int lp, out int rp);
        return _ast.CreateCallExpression(callee, args, lp, rp);
    }

    private int ParseDotMember(int left)
    {
        int period = _tokStart;
        Next();
        int idx = _tokStart;

        if (_kind == TokenKind.PrivateIdentifier)
        {
            int ident = _ast.CreateIdentifier(_tokKnown, ScopeContext.Unresolved, idx, _tokEnd - idx);
            Next();
            return _ast.CreatePrivateDotExpression(left, _ast.CreatePrivateIdentifier(ident));
        }

        if (!TokenKinds.IsIdentifierName(_kind))
        {
            Expect(TokenKind.Identifier);
            NextStatement();
            return Invalid(period, _tokStart);
        }

        int prop = ParseIdentifier();
        return _ast.CreateMemberExpression(left, prop);
    }

    private int ParseBracketMember(int left)
    {
        int lb = Expect(TokenKind.LeftBracket);
        int member = ParseExpression();
        int rb = Expect(TokenKind.RightBracket);
        return _ast.CreateMemberExpression(left, _ast.CreateComputedProperty(member, lb, rb));
    }

    private int ParseNewExpression()
    {
        if (StackIsLow())
        {
            return OnNewStack(ParseNewExpressionCore);
        }
        return ParseNewExpressionCore();
    }

    private int ParseNewExpressionCore()
    {
        int idx = Expect(TokenKind.New);
        if (_kind == TokenKind.Period)
        {
            Next();
            if (_tokKnown == KnownStrings.Target && !_hasEscape && TokenKinds.IsIdentifierName(_kind))
            {
                Next();
                return _ast.CreateMetaProperty(MetaPropertyKind.NewTarget, idx);
            }
            ErrorUnexpectedToken(TokenKind.Identifier);
        }

        int callee = ParseLeftHandSideExpression();
        if (K(callee) == NodeKind.InvalidExpression)
        {
            N(callee).A = idx; // From
            return callee;
        }

        // `new a?.b()`: an optional chain cannot continue a `new` callee (`new a()?.b()` is fine: the arguments
        // close the NewExpression first).
        if (_kind == TokenKind.QuestionDot) Error("Optional chaining cannot appear in the callee of new expressions");

        int lp = 0, rp = 0;
        ListRange args = default;
        if (_kind == TokenKind.LeftParenthesis) args = ParseArgumentList(out lp, out rp);
        return _ast.CreateNewExpression(callee, args, idx, lp, rp);
    }

    private int ParseLeftHandSideExpression()
    {
        int left = _kind == TokenKind.New ? ParseNewExpression() : ParsePrimaryExpression();
        while (true)
        {
            switch (_kind)
            {
                case TokenKind.Period:
                    left = ParseDotMember(left);
                    continue;
                case TokenKind.LeftBracket:
                    left = ParseBracketMember(left);
                    continue;
                case TokenKind.NoSubstitutionTemplate:
                case TokenKind.TemplateHead:
                    left = ParseTemplateLiteral(left);
                    continue;
            }
            return left;
        }
    }

    private int ParseLeftHandSideExpressionAllowCall()
    {
        bool allowIn = _scope.AllowIn;
        _scope.AllowIn = true;

        int start = _tokStart;
        int left = _kind == TokenKind.New ? ParseNewExpression() : ParsePrimaryExpression();
        if (IsCallTailStart(_kind)) left = ParseCallTail(left, start);

        _scope.AllowIn = allowIn;
        return left;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCallTailStart(TokenKind kind) =>
        kind is TokenKind.Period or TokenKind.LeftBracket or TokenKind.LeftParenthesis
            or TokenKind.NoSubstitutionTemplate or TokenKind.TemplateHead or TokenKind.QuestionDot;

    /// <summary>
    /// The member/call/template/optional-chain loop that follows a primary expression. Runs with
    /// <c>in</c> allowed (brackets and argument lists are not for-init contexts).
    /// </summary>
    private int ParseCallTail(int left, int start)
    {
        bool allowIn = _scope.AllowIn;
        _scope.AllowIn = true;
        bool optionalChain = false;

        while (true)
        {
            switch (_kind)
            {
                case TokenKind.Period:
                    left = ParseDotMember(left);
                    continue;
                case TokenKind.LeftBracket:
                    left = ParseBracketMember(left);
                    continue;
                case TokenKind.LeftParenthesis:
                    left = ParseCallExpression(left);
                    continue;
                case TokenKind.NoSubstitutionTemplate:
                case TokenKind.TemplateHead:
                    if (optionalChain)
                    {
                        Error("Invalid template literal on optional chain");
                        NextStatement();
                        _scope.AllowIn = allowIn;
                        return Invalid(start, _tokStart);
                    }
                    left = ParseTemplateLiteral(left);
                    continue;
                case TokenKind.QuestionDot:
                    optionalChain = true;
                    left = _ast.CreateOptional(left);
                    switch (Peek())
                    {
                        case TokenKind.LeftBracket:
                        case TokenKind.LeftParenthesis:
                        case TokenKind.NoSubstitutionTemplate:
                        case TokenKind.TemplateHead:
                            Next();
                            break;
                        default:
                            left = ParseDotMember(left);
                            break;
                    }
                    continue;
            }
            break;
        }

        if (optionalChain) left = _ast.CreateOptionalChain(left);
        _scope.AllowIn = allowIn;
        return left;
    }

    // ---- unary / update / binary / conditional -----------------------------------------------------

    private int ParseUpdateExpression()
    {
        TokenKind kind = _kind;
        if (kind is TokenKind.Increment or TokenKind.Decrement)
        {
            int idx = _tokStart;
            Next();
            int operand = ParseUnaryExpression();
            if (!IsSimpleAssignTarget(K(operand)))
            {
                Error("Invalid left-hand side in assignment");
                NextStatement();
                return Invalid(idx, _tokStart);
            }
            return _ast.CreateUpdateExpression(operand, idx,
                kind == TokenKind.Increment ? UpdateOperator.Increment : UpdateOperator.Decrement, false);
        }

        int left = ParseLeftHandSideExpressionAllowCall();
        if (_kind is TokenKind.Increment or TokenKind.Decrement && !_newLine) return ParsePostfixUpdate(left);
        return left;
    }

    private int ParsePostfixUpdate(int left)
    {
        TokenKind kind = _kind;
        int idx = _tokStart;
        Next();
        if (!IsSimpleAssignTarget(K(left)))
        {
            Error("Invalid left-hand side in assignment");
            NextStatement();
            return Invalid(idx, _tokStart);
        }
        return _ast.CreateUpdateExpression(left, idx,
            kind == TokenKind.Increment ? UpdateOperator.Increment : UpdateOperator.Decrement, true);
    }

    /// <summary>
    /// Fast path for the overwhelmingly common operand shapes (identifiers and literals): parses the
    /// primary directly plus its call tail and postfix update, skipping the assignment → conditional →
    /// binary → unary → update → call descent that go-fAST performs for every operand.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSimpleOperandStart(TokenKind kind) =>
        kind is TokenKind.Identifier or TokenKind.String or TokenKind.Number or TokenKind.This
            or TokenKind.Null or TokenKind.Boolean;

    private int ParseSimpleOperand()
    {
        int start = _tokStart;
        int left = ParsePrimaryExpression();
        if (IsCallTailStart(_kind)) left = ParseCallTail(left, start);
        if (_kind is TokenKind.Increment or TokenKind.Decrement && !_newLine) left = ParsePostfixUpdate(left);
        return left;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSimpleAssignTarget(NodeKind kind) =>
        kind is NodeKind.Identifier or NodeKind.MemberExpression or NodeKind.PrivateDotExpression;

    private int ParseUnaryExpression()
    {
        if (StackIsLow())
        {
            return OnNewStack(ParseUnaryExpressionCore);
        }
        return ParseUnaryExpressionCore();
    }

    private int ParseUnaryExpressionCore()
    {
        TokenKind kind = _kind;
        byte op = s_unaryOperator[(int)kind];
        if (op != NotAnOperator)
        {
            int idx = _tokStart;
            Next();
            int operand = ParseUnaryExpression();
            return _ast.CreateUnaryExpression(operand, idx, (UnaryOperator)op);
        }

        if (kind == TokenKind.Await && _scope.AllowAwait)
        {
            int idx = _tokStart;
            Next();
            if (!_scope.InAsync)
            {
                ErrorUnexpectedToken(TokenKind.Await);
                return Invalid(idx, _tokStart);
            }
            if (_scope.InFuncParams) Error("Illegal await-expression in formal parameters of async function");
            return _ast.CreateAwaitExpression(ParseUnaryExpression(), idx);
        }

        return ParseUpdateExpression();
    }

    private int _deepMinPrecedence;

    private int ParseBinaryDeep() => ParseBinaryExpressionOrHigherCore(_deepMinPrecedence);

    /// <summary>Pratt loop over binary and logical operators (see go-fAST parseBinaryExpressionOrHigher).</summary>
    private int ParseBinaryExpressionOrHigher(int minPrecedence)
    {
        if (StackIsLow())
        {
            _deepMinPrecedence = minPrecedence;
            return OnNewStack(ParseBinaryDeep);
        }
        return ParseBinaryExpressionOrHigherCore(minPrecedence);
    }

    private int ParseBinaryExpressionOrHigherCore(int minPrecedence)
    {
        if (IsSimpleOperandStart(_kind))
            return ParseBinaryExpressionRest(ParseSimpleOperand(), false, minPrecedence);

        bool lhsParenthesized = _kind == TokenKind.LeftParenthesis;
        int lhs = _scope.AllowIn && _kind == TokenKind.PrivateIdentifier
            ? ParsePrivateInExpression(minPrecedence)
            : ParseUnaryExpression();
        return ParseBinaryExpressionRest(lhs, lhsParenthesized, minPrecedence);
    }

    /// <summary>The Pratt operator loop after the left operand (go-fAST parseBinaryExpressionRest).</summary>
    private int ParseBinaryExpressionRest(int lhs, bool lhsParenthesized, int minPrecedence)
    {
        byte[] precedence = s_binaryPrecedence;
        while (true)
        {
            TokenKind kind = _kind;
            int lbp = precedence[(int)kind];
            if (lbp <= minPrecedence) break;
            if (kind == TokenKind.In && !_scope.AllowIn) break;

            Next();
            bool rhsParenthesized = _kind == TokenKind.LeftParenthesis;
            int rhs = ParseBinaryExpressionOrHigher(lbp ^ 1);

            byte op = s_binaryOperator[(int)kind];
            if ((op & LogicalFlag) != 0)
            {
                if (kind == TokenKind.Coalesce)
                {
                    if ((!rhsParenthesized && IsAndOr(rhs)) || (!lhsParenthesized && IsAndOr(lhs)))
                        Error("Logical expressions and coalesce expressions cannot be mixed. Wrap either by parentheses");
                }
                lhs = _ast.CreateLogicalExpression(lhs, rhs, (LogicalOperator)(op & ~LogicalFlag));
            }
            else
            {
                if (kind == TokenKind.Exponent && !lhsParenthesized && K(lhs) is NodeKind.UnaryExpression or NodeKind.AwaitExpression)
                    Error("Unary operator used immediately before exponentiation expression. Parenthesis must be used to disambiguate operator precedence");
                lhs = _ast.CreateBinaryExpression(lhs, rhs, (BinaryOperator)op);
            }
            lhsParenthesized = false;
        }
        return lhs;
    }

    private bool IsAndOr(int id)
    {
        ref NodeRecord r = ref N(id);
        return r.Kind == NodeKind.LogicalExpression && (LogicalOperator)r.Op is LogicalOperator.And or LogicalOperator.Or;
    }

    /// <summary>The <c>#name in obj</c> form.</summary>
    private int ParsePrivateInExpression(int minPrecedence)
    {
        int ident = _ast.CreateIdentifier(_tokKnown, ScopeContext.Unresolved, _tokStart, _tokEnd - _tokStart);
        int left = _ast.CreatePrivateIdentifier(ident);
        Next();
        if (_kind != TokenKind.In || PrecCompare <= minPrecedence) return left;
        Next();
        int rhs = ParseBinaryExpressionOrHigher(PrecCompare);
        return _ast.CreateBinaryExpression(left, rhs, BinaryOperator.In);
    }

    private int ParseConditionalExpression()
    {
        int test = ParseBinaryExpressionOrHigher(PrecLowest);
        if (_kind != TokenKind.QuestionMark) return test;
        return ParseConditionalRest(test);
    }

    /// <summary>The <c>? consequent : alternate</c> part after the test (current token is <c>?</c>).</summary>
    private int ParseConditionalRest(int test)
    {
        Next();
        bool allowIn = _scope.AllowIn;
        _scope.AllowIn = true;
        int consequent = ParseAssignmentExpression();
        _scope.AllowIn = allowIn;
        Expect(TokenKind.Colon);
        int alternate = ParseAssignmentExpression();
        return _ast.CreateConditionalExpression(test, consequent, alternate);
    }

    // ---- assignment / arrow functions --------------------------------------------------------------

    private int ParseAssignmentExpression()
    {
        if (StackIsLow())
        {
            return OnNewStack(ParseAssignmentExpressionCore);
        }
        return ParseAssignmentExpressionCore();
    }

    private int ParseAssignmentExpressionCore()
    {
        // `(` and `async` may start an arrow function whose parameters are re-parsed from a checkpoint; they are
        // handled out of line so the common path carries no checkpoint (a struct holding a string, which the
        // JIT would zero-initialize in this method's prologue on every call).
        switch (_kind)
        {
            case TokenKind.LeftParenthesis:
                return ParseParenthesizedAssignment();
            case TokenKind.Async:
                return ParseAsyncAssignment();
            case TokenKind.Yield:
                if (_scope.AllowYield) return ParseYieldExpression();
                break;
        }

        int start = _tokStart;
        TokenToBindingId();
        if (IsSimpleOperandStart(_kind))
        {
            // Fast path: parse the operand directly; most arguments/initializers end right here.
            int operand = ParseSimpleOperand();
            TokenKind next = _kind;
            if (next is TokenKind.Comma or TokenKind.RightParenthesis or TokenKind.Semicolon
                or TokenKind.RightBracket or TokenKind.RightBrace or TokenKind.Colon or TokenKind.Eof)
                return operand;
            if (s_binaryPrecedence[(int)next] != 0) operand = ParseBinaryExpressionRest(operand, false, PrecLowest);
            if (_kind == TokenKind.QuestionMark) operand = ParseConditionalRest(operand);
            return ParseAssignmentTail(start, operand);
        }
        return ParseAssignmentTail(start, ParseConditionalExpression());
    }

    /// <summary>An assignment expression starting with <c>(</c>: a parenthesized expression or arrow parameters.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ParseParenthesizedAssignment()
    {
        int start = _tokStart;
        Checkpoint state = Mark();
        return ParseAssignmentTail(start, ParseConditionalExpression(), parenthesis: true, async: false, in state);
    }

    /// <summary>An assignment expression starting with <c>async</c>: an async arrow function, or <c>async</c> as a name.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ParseAsyncAssignment()
    {
        int start = _tokStart;
        TokenKind next = Peek();
        if (IsBindingId(next) && !_peekNewLine)
        {
            // async x => ...: `async [no LineTerminator here] x =>`. Without the arrow, `async` is a plain name
            // (in `for await (async of xs)` the identifier after it belongs to the loop head).
            Checkpoint name = Mark();
            Next();
            if (Peek() == TokenKind.Arrow) return ParseSingleArgArrowFunction(start, true);
            Restore(name);
        }
        Checkpoint state = default;
        bool async = false;
        if (next == TokenKind.LeftParenthesis)
        {
            state = Mark();
            async = true;
        }
        return ParseAssignmentTail(start, ParseConditionalExpression(), parenthesis: false, async, in state);
    }

    /// <summary>The arrow-function / assignment-operator part after the conditional-level expression.</summary>
    private int ParseAssignmentTail(int start, int left, bool parenthesis, bool async, in Checkpoint state)
    {
        TokenKind kind = _kind;
        if (kind == TokenKind.Arrow)
            return ParseArrowFromCover(start, left, parenthesis, async, in state);
        byte op = s_assignOperator[(int)kind];
        return op != NotAnOperator ? ParseAssignmentOperator(left, (AssignmentOperator)op, parenthesis) : left;
    }

    /// <summary><see cref="ParseAssignmentTail(int, int, bool, bool, in Checkpoint)"/> without a parenthesized or async cover.</summary>
    private int ParseAssignmentTail(int start, int left)
    {
        TokenKind kind = _kind;
        if (kind == TokenKind.Arrow)
            return ParseArrowWithoutCover(start, left);
        byte op = s_assignOperator[(int)kind];
        return op != NotAnOperator ? ParseAssignmentOperator(left, (AssignmentOperator)op, parenthesis: false) : left;
    }

    /// <summary>An arrow function after a single unparenthesized parameter (<c>x =&gt; ...</c>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ParseArrowWithoutCover(int start, int left)
    {
        Checkpoint none = default; // unused without a parenthesized or async cover
        return ParseArrowFromCover(start, left, parenthesis: false, async: false, in none);
    }

    /// <summary>An assignment <c>left op= right</c>; the current token is the operator.</summary>
    private int ParseAssignmentOperator(int left, AssignmentOperator assignOp, bool parenthesis)
    {
        int idx = _tokStart;
        Next();
        int target = 0;
        switch (K(left))
        {
            case NodeKind.Identifier:
            case NodeKind.PrivateDotExpression:
            case NodeKind.MemberExpression:
                target = left;
                break;
            case NodeKind.ArrayLiteral:
            case NodeKind.ObjectLiteral:
                if (!parenthesis && assignOp == AssignmentOperator.Assign)
                    target = PatternFromExpression(left, binding: false);
                break;
        }
        if (target != 0)
            return _ast.CreateAssignExpression(target, ParseAssignmentExpression(), assignOp);

        Error("Invalid left-hand side in assignment");
        NextStatement();
        return Invalid(idx, _tokStart);
    }

    /// <summary>Builds the parameter list of an arrow function from the already-parsed cover grammar.</summary>
    private int ParseArrowFromCover(int start, int left, bool parenthesis, bool async, in Checkpoint state)
    {
        int parameters = 0;
        if (K(left) == NodeKind.Identifier)
        {
            int decl = _ast.CreateVariableDeclarator(left, 0);
            int open = parenthesis ? _parenOpen : _ast.GetStart(left);
            int close = parenthesis ? _parenClose : _ast.GetEnd(left) - 1;
            parameters = _ast.CreateParameterList(_ast.AddList([decl]), 0, open, close);
        }
        else if (parenthesis)
        {
            if (_errors.Count == state.ErrorCount)
            {
                parameters = ReinterpretAsParameters(left, _parenOpen, _parenClose);
            }
            else
            {
                Restore(state);
                parameters = ParseFunctionParameterList();
            }
        }
        else if (async)
        {
            bool savedAwait = _scope.AllowAwait;
            _scope.AllowAwait = true;
            ref NodeRecord call = ref N(left);
            if (call.Kind == NodeKind.CallExpression)
            {
                if (_errors.Count == state.ErrorCount && K(call.A) == NodeKind.Identifier)
                {
                    // async (a, b) => ...: the arguments of the "async(...)" call are the parameters.
                    int lp = call.D, rp = call.E;
                    parameters = ReinterpretListAsParameters(call.B, call.C, lp, rp);
                }
                else
                {
                    Restore(state);
                    Next(); // async
                    parameters = ParseFunctionParameterList();
                }
            }
            if (parameters == 0)
            {
                Error("Malformed arrow function parameter list");
                _scope.AllowAwait = savedAwait;
                return Invalid(_ast.GetStart(left), _ast.GetEnd(left));
            }
            int result = ParseArrowFunction(start, parameters, true);
            _scope.AllowAwait = savedAwait;
            return result;
        }

        if (parameters == 0)
        {
            Error("Malformed arrow function parameter list");
            return Invalid(_ast.GetStart(left), _ast.GetEnd(left));
        }
        return ParseArrowFunction(start, parameters, async);
    }

    /// <summary>Reinterprets a parenthesized cover expression (sequence or single expression) as parameters.</summary>
    private int ReinterpretAsParameters(int cover, int open, int close)
    {
        ref NodeRecord r = ref N(cover);
        if (r.Kind == NodeKind.SequenceExpression)
            return ReinterpretListAsParameters(r.A, r.B, open, close);

        int decl = DeclaratorFromExpression(cover);
        return _ast.CreateParameterList(_ast.AddList([decl]), 0, open, close);
    }

    private int ReinterpretListAsParameters(int listStart, int count, int open, int close)
    {
        int mark = _scratchTop;
        int rest = 0;
        for (int i = 0; i < count; i++)
        {
            int element = _ast._lists[listStart + i];
            if (K(element) == NodeKind.SpreadElement)
            {
                if (i != count - 1)
                {
                    Error("Rest parameter must be last formal parameter");
                    _scratchTop = mark;
                    return _ast.CreateParameterList(default, 0, open, close);
                }
                rest = PatternFromExpression(N(element).A, binding: true);
                break;
            }
            Push(DeclaratorFromExpression(element));
        }
        return _ast.CreateParameterList(FinishList(mark), rest, open, close);
    }

    private int ParseArrowFunction(int start, int parameters, bool async)
    {
        Expect(TokenKind.Arrow);
        int body = ParseArrowFunctionBody(async);
        return _ast.CreateArrowFunctionLiteral(parameters, body, ScopeContext.Unresolved, start, async);
    }

    private int ParseSingleArgArrowFunction(int start, bool async)
    {
        bool savedAwait = _scope.AllowAwait;
        _scope.AllowAwait = async;
        TokenToBindingId();
        if (_kind != TokenKind.Identifier)
        {
            ErrorUnexpectedToken(_kind);
            Next();
            _scope.AllowAwait = savedAwait;
            return Invalid(start, _tokStart);
        }

        int ident = ParseIdentifier();
        int decl = _ast.CreateVariableDeclarator(ident, 0);
        int parameters = _ast.CreateParameterList(_ast.AddList([decl]), 0, _ast.GetStart(ident), _ast.GetEnd(ident));
        int result = ParseArrowFunction(start, parameters, async);
        _scope.AllowAwait = savedAwait;
        return result;
    }

    private int ParseYieldExpression()
    {
        int idx = Expect(TokenKind.Yield);
        if (_scope.InFuncParams) Error("Yield expression not allowed in formal parameter");

        bool @delegate = false;
        if (!_newLine && _kind == TokenKind.Multiply)
        {
            @delegate = true;
            Next();
        }

        int argument = 0;
        if (!CanInsertSemicolon() && _kind is not (TokenKind.RightParenthesis or TokenKind.RightBracket
                or TokenKind.Comma or TokenKind.Colon or TokenKind.In or TokenKind.Of))
        {
            var state = Mark();
            int expr = ParseAssignmentExpression();
            if (K(expr) == NodeKind.InvalidExpression)
            {
                Restore(state);
            }
            else
            {
                argument = expr;
            }
        }
        return _ast.CreateYieldExpression(argument, idx, @delegate);
    }

    private int ParseExpression()
    {
        int left = ParseAssignmentExpression();
        if (_kind != TokenKind.Comma) return left;

        int mark = _scratchTop;
        Push(left);
        while (_kind == TokenKind.Comma)
        {
            Next();
            Push(ParseAssignmentExpression());
        }
        return _ast.CreateSequenceExpression(FinishList(mark));
    }

    // ---- patterns ----------------------------------------------------------------------------------

    /// <summary>Parses a binding target (identifier, array or object pattern) directly.</summary>
    private int ParseBindingTarget()
    {
        if (StackIsLow())
        {
            return OnNewStack(ParseBindingTargetCore);
        }
        return ParseBindingTargetCore();
    }

    private int ParseBindingTargetCore()
    {
        TokenToBindingId();
        switch (_kind)
        {
            case TokenKind.Identifier:
                return ParseIdentifier();
            case TokenKind.LeftBracket:
                return ParseArrayBindingPattern();
            case TokenKind.LeftBrace:
                return ParseObjectBindingPattern();
            default:
                {
                    int idx = Expect(TokenKind.Identifier);
                    NextStatement();
                    return Invalid(idx, _tokStart);
                }
        }
    }

    /// <summary>A binding target with an optional <c>= default</c>.</summary>
    private int ParseBindingElement()
    {
        int target = ParseBindingTarget();
        if (_kind != TokenKind.Assign) return target;
        Next();
        return _ast.CreateAssignmentPattern(target, ParseAssignmentExpression());
    }

    // Binding patterns allow `in` between their brackets even in a for head (`for (const {a = b in c} of d)`):
    // their initializers and computed keys are [+In] in the grammar, as in array and object literals.
    private int ParseArrayBindingPattern()
    {
        int lb = Expect(TokenKind.LeftBracket);
        bool allowIn = _scope.AllowIn;
        _scope.AllowIn = true;
        int mark = _scratchTop;
        int rest = 0;
        while (_kind != TokenKind.RightBracket && _kind != TokenKind.Eof)
        {
            if (_kind == TokenKind.Comma)
            {
                Next();
                Push(0); // elision
                continue;
            }
            if (_kind == TokenKind.Ellipsis)
            {
                Next();
                rest = ParseBindingTarget();
                break;
            }
            Push(ParseBindingElement());
            if (_kind != TokenKind.RightBracket) Expect(TokenKind.Comma);
        }
        _scope.AllowIn = allowIn;
        int rb = Expect(TokenKind.RightBracket);
        return _ast.CreateArrayPattern(FinishList(mark), rest, lb, rb);
    }

    private int ParseObjectBindingPattern()
    {
        int lb = Expect(TokenKind.LeftBrace);
        bool allowIn = _scope.AllowIn;
        _scope.AllowIn = true;
        int mark = _scratchTop;
        int rest = 0;
        while (_kind != TokenKind.RightBrace && _kind != TokenKind.Eof)
        {
            if (_kind == TokenKind.Ellipsis)
            {
                Next();
                rest = ParseObjectRestBinding();
                if (_kind == TokenKind.Comma) Next();
                break;
            }
            int property = ParseBindingProperty();
            if (property != 0) Push(property);
            if (_kind != TokenKind.RightBrace) Expect(TokenKind.Comma);
        }
        _scope.AllowIn = allowIn;
        int rb = Expect(TokenKind.RightBrace);
        return _ast.CreateObjectPattern(FinishList(mark), rest, lb, rb);
    }

    private int ParseBindingProperty()
    {
        int key = ParseObjectPropertyKey();
        if (key == 0) return 0;
        if (_kind == TokenKind.Colon)
        {
            Next();
            return _ast.CreatePatternKeyValue(key, ParseBindingElement());
        }
        TokenKind kind = _kind;
        if (IsBindingId(_keyKind) && kind is TokenKind.Comma or TokenKind.RightBrace or TokenKind.Assign)
        {
            int name = KeyIdentifier();
            int initializer = 0;
            if (kind == TokenKind.Assign)
            {
                Next();
                initializer = ParseAssignmentExpression();
            }
            return _ast.CreatePatternShorthand(name, initializer);
        }
        ErrorUnexpectedToken(kind);
        return 0;
    }

    private int ParseObjectRestBinding()
    {
        TokenToBindingId();
        if (_kind == TokenKind.Identifier) return ParseIdentifier();
        int idx = _tokStart;
        Error("Invalid destructuring binding target");
        return Invalid(idx, _tokStart);
    }

    private int ParseVariableDeclarator()
    {
        int target = ParseBindingTarget();
        int initializer = 0;
        if (_kind == TokenKind.Assign)
        {
            Next();
            initializer = ParseAssignmentExpression();
        }
        return _ast.CreateVariableDeclarator(target, initializer);
    }

    private ListRange ParseVariableDeclarationList()
    {
        int mark = _scratchTop;
        while (true)
        {
            Push(ParseVariableDeclarator());
            if (_kind != TokenKind.Comma) break;
            Next();
        }
        return FinishList(mark);
    }

    // ---- cover grammar → pattern conversion --------------------------------------------------------

    private int InvalidPattern(int node, bool binding)
    {
        Error(binding ? "Invalid destructuring binding target" : "Invalid destructuring assignment target");
        return Invalid(_ast.GetStart(node), _ast.GetEnd(node));
    }

    private int _deepExpr;
    private bool _deepBinding;

    private int PatternFromExpressionDeep() => PatternFromExpressionCore(_deepExpr, _deepBinding);

    private int EnsureBindingTargetDeep() => EnsureBindingTargetCore(_deepExpr) ? 1 : 0;

    /// <summary>Converts a cover-grammar expression into a pattern (0 stays 0: an elision).</summary>
    private int PatternFromExpression(int expr, bool binding)
    {
        if (StackIsLow())
        {
            _deepExpr = expr;
            _deepBinding = binding;
            return OnNewStack(PatternFromExpressionDeep);
        }
        return PatternFromExpressionCore(expr, binding);
    }

    private int PatternFromExpressionCore(int expr, bool binding)
    {
        if (expr == 0) return 0;
        ref NodeRecord r = ref N(expr);
        switch (r.Kind)
        {
            case NodeKind.Identifier:
                if (binding && _scope.AllowAwait && r.A == KnownStrings.Await) break;
                return expr;
            case NodeKind.MemberExpression:
            case NodeKind.PrivateDotExpression:
                if (!binding) return expr;
                break;
            case NodeKind.ArrayLiteral:
                return ArrayPatternFromLiteral(expr, binding);
            case NodeKind.ObjectLiteral:
                return ObjectPatternFromLiteral(expr, binding);
            case NodeKind.AssignExpression:
                if ((AssignmentOperator)r.Op == AssignmentOperator.Assign)
                {
                    int left = r.A, right = r.B;
                    if (binding && !EnsureBindingTarget(left))
                        return Invalid(_ast.GetStart(expr), _ast.GetEnd(expr));
                    return _ast.CreateAssignmentPattern(left, right);
                }
                break;
        }
        return InvalidPattern(expr, binding);
    }

    private int ArrayPatternFromLiteral(int literal, bool binding)
    {
        ref NodeRecord lit = ref N(literal);
        int start = lit.A, count = lit.B, lb = lit.C, rb = lit.D;
        int mark = _scratchTop;
        int rest = 0;
        for (int i = 0; i < count; i++)
        {
            int element = _ast._lists[start + i];
            if (element != 0 && K(element) == NodeKind.SpreadElement)
            {
                if (i != count - 1)
                {
                    _scratchTop = mark;
                    Error("Rest element must be last element");
                    return Invalid(lb, rb + 1);
                }
                CheckComma(_ast.GetEnd(element), rb);
                rest = PatternFromExpression(N(element).A, binding);
                break;
            }
            Push(PatternFromExpression(element, binding));
        }
        return _ast.CreateArrayPattern(FinishList(mark), rest, lb, rb);
    }

    private int ObjectPatternFromLiteral(int literal, bool binding)
    {
        ref NodeRecord lit = ref N(literal);
        int start = lit.A, count = lit.B, lb = lit.C, rb = lit.D;
        int mark = _scratchTop;
        int rest = 0;
        for (int i = 0; i < count; i++)
        {
            int property = _ast._lists[start + i];
            ref NodeRecord p = ref N(property);
            switch (p.Kind)
            {
                case NodeKind.PropertyKeyValue:
                    {
                        int key = p.A, value = p.B;
                        int pattern = PatternFromExpression(value, binding);
                        Push(_ast.CreatePatternKeyValue(key, pattern));
                        break;
                    }
                case NodeKind.PropertyShort:
                    Push(_ast.CreatePatternShorthand(p.A, p.B));
                    break;
                case NodeKind.SpreadElement:
                    if (i != count - 1)
                    {
                        _scratchTop = mark;
                        Error("Rest element must be last element");
                        return Invalid(lb, rb + 1);
                    }
                    rest = RestPattern(p.A, binding);
                    break;
                default:
                    _scratchTop = mark;
                    return InvalidPattern(literal, binding);
            }
        }
        return _ast.CreateObjectPattern(FinishList(mark), rest, lb, rb);
    }

    /// <summary>An object rest target: a simple binding/assignment target without a default.</summary>
    private int RestPattern(int expr, bool binding)
    {
        switch (K(expr))
        {
            case NodeKind.Identifier:
                return expr;
            case NodeKind.MemberExpression:
            case NodeKind.PrivateDotExpression:
                if (!binding) return expr;
                break;
        }
        return InvalidPattern(expr, binding);
    }

    /// <summary>Whether a pattern built in assignment mode is a valid binding target (no member leaves).</summary>
    private bool EnsureBindingTarget(int pattern)
    {
        if (StackIsLow())
        {
            _deepExpr = pattern;
            return OnNewStack(EnsureBindingTargetDeep) != 0;
        }
        return EnsureBindingTargetCore(pattern);
    }

    private bool EnsureBindingTargetCore(int pattern)
    {
        if (pattern == 0) return true;
        ref NodeRecord r = ref N(pattern);
        switch (r.Kind)
        {
            case NodeKind.Identifier:
            case NodeKind.InvalidExpression:
                return true;
            case NodeKind.MemberExpression:
            case NodeKind.PrivateDotExpression:
                return false;
            case NodeKind.AssignmentPattern:
                return EnsureBindingTarget(r.A);
            case NodeKind.ArrayPattern:
                {
                    int start = r.A, count = r.B, rest = r.C;
                    for (int i = 0; i < count; i++)
                        if (!EnsureBindingTarget(_ast._lists[start + i])) return false;
                    return EnsureBindingTarget(rest);
                }
            case NodeKind.ObjectPattern:
                {
                    int start = r.A, count = r.B, rest = r.C;
                    for (int i = 0; i < count; i++)
                    {
                        int property = _ast._lists[start + i];
                        if (K(property) == NodeKind.PatternKeyValue && !EnsureBindingTarget(N(property).B)) return false;
                    }
                    return EnsureBindingTarget(rest);
                }
        }
        return false;
    }

    /// <summary>Converts an arrow parameter (parsed as an expression) into a declarator; defaults become initializers.</summary>
    private int DeclaratorFromExpression(int expr)
    {
        ref NodeRecord r = ref N(expr);
        if (r.Kind == NodeKind.AssignExpression && (AssignmentOperator)r.Op == AssignmentOperator.Assign)
        {
            int left = r.A, right = r.B;
            if (!EnsureBindingTarget(left))
                return _ast.CreateVariableDeclarator(Invalid(_ast.GetStart(expr), _ast.GetEnd(expr)), 0);
            return _ast.CreateVariableDeclarator(left, right);
        }
        return _ast.CreateVariableDeclarator(PatternFromExpression(expr, binding: true), 0);
    }

    private void CheckComma(int from, int to)
    {
        if (to > from && from >= 0 && new ReadOnlySpan<char>(_src + from, Math.Min(to, _len) - from).Contains(','))
            Error("Comma is not allowed here");
    }
}
