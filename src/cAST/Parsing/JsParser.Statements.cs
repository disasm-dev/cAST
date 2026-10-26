namespace cAST.Parsing;

// Statement parsing, following go-fAST's parser/statement.go. Differences: keyword positions (if/while/
// with/do/switch) are recorded, anonymous classes get no name node, class `get`/`set`/`async` detection
// handles `get = 1` / `get }` fields, and labels are compared as source slices.
public sealed unsafe partial class JsParser
{
    private static VarKind VarKindOf(TokenKind kind) => kind switch
    {
        TokenKind.Let => VarKind.Let,
        TokenKind.Const => VarKind.Const,
        _ => VarKind.Var,
    };

    private int ParseBlockStatement()
    {
        int lb = Expect(TokenKind.LeftBrace);
        ListRange list = ParseStatementList();
        int rb = Expect(TokenKind.RightBrace);
        return _ast.CreateBlockStatement(list, ScopeContext.Unresolved, lb, rb);
    }

    private ListRange ParseStatementList()
    {
        int mark = _scratchTop;
        while (_kind != TokenKind.RightBrace && _kind != TokenKind.Eof)
        {
            _scope.AllowLet = true;
            Push(ParseStatement());
        }
        return FinishList(mark);
    }

    private ListRange ParseSourceElements()
    {
        int mark = _scratchTop;
        while (_kind != TokenKind.Eof)
        {
            _scope.AllowLet = true;
            Push(ParseStatement());
        }
        return FinishList(mark);
    }

    private int ParseStatement()
    {
        if (StackIsLow())
        {
            return OnNewStack(ParseStatementCore);
        }
        return ParseStatementCore();
    }

    private int ParseStatementCore()
    {
        TokenKind tok = _kind;
        switch (tok)
        {
            case TokenKind.Eof:
                ErrorUnexpectedToken(tok);
                return _ast.CreateBadStatement(_tokStart, _tokStart + 1);
            case TokenKind.Semicolon:
                return _ast.CreateEmptyStatement(Expect(TokenKind.Semicolon));
            case TokenKind.LeftBrace:
                return ParseBlockStatement();
            case TokenKind.If:
                return ParseIfStatement();
            case TokenKind.Do:
                return ParseDoWhileStatement();
            case TokenKind.While:
                return ParseWhileStatement();
            case TokenKind.For:
                return ParseForOrForInStatement();
            case TokenKind.Break:
                return ParseBreakStatement();
            case TokenKind.Continue:
                return ParseContinueStatement();
            case TokenKind.Debugger:
                {
                    int idx = Expect(TokenKind.Debugger);
                    RequireSemicolon();
                    return _ast.CreateDebuggerStatement(idx);
                }
            case TokenKind.With:
                return ParseWithStatement();
            case TokenKind.Var:
            case TokenKind.Const:
                return ParseLexicalDeclaration(tok);
            case TokenKind.Let:
                {
                    TokenKind next = Peek();
                    if (next == TokenKind.LeftBracket || (_scope.AllowLet && (TokenKinds.IsIdentifierName(next) || next == TokenKind.LeftBrace)))
                        return ParseLexicalDeclaration(tok);
                    break;
                }
            case TokenKind.Async:
                {
                    int f = ParseMaybeAsyncFunction(true);
                    if (f != 0) return _ast.CreateFunctionDeclaration(f);
                    break;
                }
            case TokenKind.Function:
                return _ast.CreateFunctionDeclaration(ParseFunction(true, false, _tokStart));
            case TokenKind.Class:
                return _ast.CreateClassDeclaration(ParseClass(true));
            case TokenKind.Switch:
                return ParseSwitchStatement();
            case TokenKind.Return:
                return ParseReturnStatement();
            case TokenKind.Throw:
                return ParseThrowStatement();
            case TokenKind.Try:
                return ParseTryStatement();
        }

        int expression = ParseExpression();

        if (_kind == TokenKind.Colon && K(expression) == NodeKind.Identifier)
            return ParseLabelledStatement(expression);

        RequireSemicolon();
        return _ast.CreateExpressionStatement(expression, 0);
    }

    private int ParseLabelledStatement(int identifier)
    {
        int colon = _tokStart;
        Next();

        ref NodeRecord id = ref N(identifier);
        int labelStart = id.C, labelLength = id.D;
        if (HasLabel(labelStart, labelLength))
            Error($"Label '{new string(_src, labelStart, labelLength)}' has already been declared");

        if (_labelCount == _labels.Length) Array.Resize(ref _labels, _labels.Length * 2);
        _labels[_labelCount++] = (labelStart, labelLength);
        _scope.AllowLet = false;
        int statement = ParseStatement();
        _labelCount--;
        return _ast.CreateLabelledStatement(identifier, statement, colon);
    }

    private int ParseTryStatement()
    {
        int idx = Expect(TokenKind.Try);
        int body = ParseBlockStatement();
        int handler = 0, finalizer = 0;

        if (_kind == TokenKind.Catch)
        {
            int catchIdx = _tokStart;
            Next();
            int parameter = 0;
            if (_kind == TokenKind.LeftParenthesis)
            {
                Next();
                parameter = ParseBindingTarget();
                Expect(TokenKind.RightParenthesis);
            }
            int catchBody = ParseBlockStatement();
            handler = _ast.CreateCatchClause(parameter, catchBody, catchIdx);
        }

        if (_kind == TokenKind.Finally)
        {
            Next();
            finalizer = ParseBlockStatement();
        }

        if (handler == 0 && finalizer == 0)
        {
            Error("Missing catch or finally after try");
            return _ast.CreateBadStatement(idx, _ast.GetEnd(body));
        }
        return _ast.CreateTryStatement(body, handler, finalizer, idx);
    }

    private int ParseFunctionParameterList()
    {
        int opening = Expect(TokenKind.LeftParenthesis);
        int mark = _scratchTop;
        int rest = 0;
        bool savedFuncParams = _scope.InFuncParams;
        _scope.InFuncParams = true;

        while (_kind != TokenKind.RightParenthesis && _kind != TokenKind.Eof)
        {
            if (_kind == TokenKind.Ellipsis)
            {
                Next();
                rest = ParseBindingTarget();
                break;
            }
            Push(ParseVariableDeclarator());
            if (_kind != TokenKind.RightParenthesis) Expect(TokenKind.Comma);
        }

        int closing = Expect(TokenKind.RightParenthesis);
        _scope.InFuncParams = savedFuncParams;
        return _ast.CreateParameterList(FinishList(mark), rest, opening, closing);
    }

    private int ParseMaybeAsyncFunction(bool declaration)
    {
        if (Peek() != TokenKind.Function) return 0;
        int idx = _tokStart;
        Next();
        return ParseFunction(declaration, true, idx);
    }

    private int ParseFunction(bool declaration, bool async, int start)
    {
        Expect(TokenKind.Function);
        bool generator = false;
        if (_kind == TokenKind.Multiply)
        {
            generator = true;
            Next();
        }

        bool savedAwait = _scope.AllowAwait, savedYield = _scope.AllowYield;
        if (!declaration)
        {
            _scope.AllowAwait = async;
            _scope.AllowYield = generator;
        }

        TokenToBindingId();
        int name = 0;
        if (_kind == TokenKind.Identifier) name = ParseIdentifier();
        else if (declaration) Expect(TokenKind.Identifier);

        if (declaration)
        {
            _scope.AllowAwait = async;
            _scope.AllowYield = generator;
        }

        int parameters = ParseFunctionParameterList();
        int body = ParseFunctionBlock(async, async, _scope.AllowYield);

        _scope.AllowAwait = savedAwait;
        _scope.AllowYield = savedYield;
        return _ast.CreateFunctionLiteral(name, parameters, body, ScopeContext.Unresolved, start, async, generator);
    }

    private int ParseFunctionBlock(bool async, bool allowAwait, bool allowYield)
    {
        OpenScope();
        _scope.InFunction = true;
        _scope.InAsync = async;
        _scope.AllowAwait = allowAwait;
        _scope.AllowYield = allowYield;
        int body = ParseBlockStatement();
        CloseScope();
        return body;
    }

    private int ParseArrowFunctionBody(bool async)
    {
        if (_kind == TokenKind.LeftBrace)
            return ParseFunctionBlock(async, async, false);

        if (async != _scope.InAsync || async != _scope.AllowAwait)
        {
            bool inAsync = _scope.InAsync, allowAwait = _scope.AllowAwait, allowYield = _scope.AllowYield;
            _scope.InAsync = async;
            _scope.AllowAwait = async;
            _scope.AllowYield = false;
            int expr = ParseAssignmentExpression();
            _scope.InAsync = inAsync;
            _scope.AllowAwait = allowAwait;
            _scope.AllowYield = allowYield;
            return expr;
        }
        return ParseAssignmentExpression();
    }

    private bool _deepDeclaration;

    private int ParseClassDeep() => ParseClassCore(_deepDeclaration);

    private int ParseClass(bool declaration)
    {
        if (StackIsLow())
        {
            _deepDeclaration = declaration;
            return OnNewStack(ParseClassDeep);
        }
        return ParseClassCore(declaration);
    }

    private int ParseClassCore(bool declaration)
    {
        // A class declaration cannot be the body of an if, loop or label (AllowLet is off there); a class
        // expression can appear anywhere an expression can (`if (x) module.exports = class {}`).
        if (declaration && !_scope.AllowLet && _kind == TokenKind.Class) ErrorUnexpectedToken(TokenKind.Class);
        int classIdx = Expect(TokenKind.Class);

        TokenToBindingId();
        int name = 0;
        if (_kind == TokenKind.Identifier) name = ParseIdentifier();
        else if (declaration) Expect(TokenKind.Identifier);

        int superClass = 0;
        if (_kind != TokenKind.LeftBrace)
        {
            Expect(TokenKind.Extends);
            superClass = ParseLeftHandSideExpressionAllowCall();
        }

        Expect(TokenKind.LeftBrace);
        int mark = _scratchTop;
        while (_kind != TokenKind.RightBrace && _kind != TokenKind.Eof)
        {
            if (_kind == TokenKind.Semicolon)
            {
                Next();
                continue;
            }

            int start = _tokStart;
            bool isStatic = false;
            if (_kind == TokenKind.Static)
            {
                switch (Peek())
                {
                    case TokenKind.Assign:
                    case TokenKind.Semicolon:
                    case TokenKind.RightBrace:
                    case TokenKind.LeftParenthesis:
                        break; // a member named "static"
                    default:
                        Next();
                        if (_kind == TokenKind.LeftBrace)
                        {
                            int block = ParseFunctionBlock(false, true, false);
                            Push(_ast.CreateClassStaticBlock(block, start));
                            continue;
                        }
                        isStatic = true;
                        break;
                }
            }

            MethodKind kind = MethodKind.None;
            bool async = false;
            int methodStart = _tokStart;
            if (_kind == TokenKind.Identifier && !_hasEscape && (_tokKnown == KnownStrings.Get || _tokKnown == KnownStrings.Set))
            {
                if (Peek() is not (TokenKind.Semicolon or TokenKind.LeftParenthesis or TokenKind.Assign or TokenKind.RightBrace))
                {
                    kind = _tokKnown == KnownStrings.Get ? MethodKind.Get : MethodKind.Set;
                    Next();
                }
            }
            else if (_kind == TokenKind.Async)
            {
                if (Peek() is not (TokenKind.Semicolon or TokenKind.LeftParenthesis or TokenKind.Assign or TokenKind.RightBrace))
                {
                    async = true;
                    kind = MethodKind.Method;
                    Next();
                }
            }

            bool generator = false;
            if (_kind == TokenKind.Multiply && kind is MethodKind.None or MethodKind.Method)
            {
                generator = true;
                kind = MethodKind.Method;
                Next();
            }

            int key = ParseObjectPropertyKey();
            if (key == 0) continue;
            bool computed = _keyKind == TokenKind.Illegal;
            bool isPrivate = K(key) == NodeKind.PrivateIdentifier;

            if (isStatic && !isPrivate && KeyNameIs(key, "prototype"))
                Error("Classes may not have a static property named 'prototype'");

            if (kind == MethodKind.None && _kind == TokenKind.LeftParenthesis) kind = MethodKind.Method;

            if (kind != MethodKind.None)
            {
                if (!computed && KeyNameIs(key, "constructor"))
                {
                    if (!isStatic)
                    {
                        if (kind != MethodKind.Method) Error("Class constructor may not be an accessor");
                        else if (async) Error("Class constructor may not be an async method");
                        else if (generator) Error("Class constructor may not be a generator");
                    }
                    else if (isPrivate)
                    {
                        Error("Class constructor may not be a private method");
                    }
                }
                int body = ParseMethodDefinition(methodStart, kind, generator, async);
                Push(_ast.CreateMethodDefinition(key, kind, body, start, isStatic));
            }
            else
            {
                if (!computed && KeyNameIs(key, "constructor"))
                    Error("Classes may not have a field named 'constructor'");

                int initializer = 0;
                if (_kind == TokenKind.Assign)
                {
                    Next();
                    initializer = ParseExpression();
                }
                if (!CanInsertSemicolon())
                {
                    ErrorUnexpectedToken(_kind);
                    break;
                }
                Push(_ast.CreateFieldDefinition(key, initializer, start, isStatic));
            }
        }

        ListRange elements = FinishList(mark);
        int rb = Expect(TokenKind.RightBrace);
        return _ast.CreateClassLiteral(name, superClass, elements, classIdx, rb);
    }

    /// <summary>Whether a (non-computed) property key node names <paramref name="text"/>.</summary>
    private bool KeyNameIs(int key, string text)
    {
        switch (K(key))
        {
            case NodeKind.StringLiteral:
                return _ast.GetLazySpan(key, 0).SequenceEqual(text);
            case NodeKind.PrivateIdentifier:
                return _ast.GetLazySpan(N(key).A, 0).SequenceEqual(text);
            default:
                return false;
        }
    }

    private int ParseReturnStatement()
    {
        int idx = Expect(TokenKind.Return);
        if (!_scope.InFunction)
        {
            Error("Illegal return statement");
            NextStatement();
            return _ast.CreateBadStatement(idx, _tokStart);
        }

        int argument = 0;
        if (!CanInsertSemicolon()) argument = ParseExpression();
        RequireSemicolon();
        return _ast.CreateReturnStatement(argument, idx);
    }

    private int ParseThrowStatement()
    {
        int idx = Expect(TokenKind.Throw);
        if (_newLine)
        {
            Error("Illegal newline after throw");
            NextStatement();
            return _ast.CreateBadStatement(idx, _tokStart);
        }
        int argument = ParseExpression();
        RequireSemicolon();
        return _ast.CreateThrowStatement(argument, idx);
    }

    private int ParseSwitchStatement()
    {
        int idx = Expect(TokenKind.Switch);
        Expect(TokenKind.LeftParenthesis);
        int discriminant = ParseExpression();
        Expect(TokenKind.RightParenthesis);
        Expect(TokenKind.LeftBrace);

        bool inSwitch = _scope.InSwitch;
        _scope.InSwitch = true;

        int mark = _scratchTop;
        int defaultIndex = -1;
        for (int index = 0; _kind != TokenKind.Eof; index++)
        {
            if (_kind == TokenKind.RightBrace)
            {
                Next();
                break;
            }
            int clause = ParseSwitchCase();
            if (N(clause).A == 0)
            {
                if (defaultIndex != -1) Error("Already saw a default in switch");
                defaultIndex = index;
            }
            Push(clause);
        }

        _scope.InSwitch = inSwitch;
        return _ast.CreateSwitchStatement(discriminant, FinishList(mark), idx);
    }

    private int ParseSwitchCase()
    {
        int caseIdx = _tokStart;
        int test = 0;
        if (_kind == TokenKind.Default)
        {
            Next();
        }
        else
        {
            Expect(TokenKind.Case);
            test = ParseExpression();
        }
        Expect(TokenKind.Colon);

        int mark = _scratchTop;
        while (_kind is not (TokenKind.Eof or TokenKind.RightBrace or TokenKind.Case or TokenKind.Default))
        {
            _scope.AllowLet = true;
            Push(ParseStatement());
        }
        return _ast.CreateSwitchCase(test, FinishList(mark), caseIdx);
    }

    private int ParseWithStatement()
    {
        int idx = Expect(TokenKind.With);
        Expect(TokenKind.LeftParenthesis);
        int obj = ParseExpression();
        Expect(TokenKind.RightParenthesis);
        _scope.AllowLet = false;
        int body = ParseStatement();
        return _ast.CreateWithStatement(obj, body, idx);
    }

    private int ParseIterationStatement()
    {
        bool inIteration = _scope.InIteration;
        _scope.InIteration = true;
        _scope.AllowLet = false;
        int result = ParseStatement();
        _scope.InIteration = inIteration;
        return result;
    }

    private bool AtOf() => _kind == TokenKind.Of || (_kind == TokenKind.Identifier && !_hasEscape && _tokKnown == KnownStrings.Of);

    private int ParseForOrForInStatement()
    {
        int idx = Expect(TokenKind.For);
        bool forAwait = false;
        if (_kind == TokenKind.Await)
        {
            if (!_scope.AllowAwait) Error("for-await-of is only allowed in async functions");
            Next();
            forAwait = true;
        }
        Expect(TokenKind.LeftParenthesis);

        int initializer = 0, into = 0;
        bool forIn = false, forOf = false;

        if (_kind != TokenKind.Semicolon)
        {
            bool allowIn = _scope.AllowIn;
            _scope.AllowIn = false;

            TokenKind tok = _kind;
            if (tok == TokenKind.Let)
            {
                TokenKind next = Peek();
                if (!(next is TokenKind.Identifier or TokenKind.LeftBracket or TokenKind.LeftBrace || next > TokenKind.EscapedReservedWord))
                    tok = TokenKind.Identifier;
            }

            if (tok is TokenKind.Var or TokenKind.Let or TokenKind.Const)
            {
                int declIdx = _tokStart;
                Next();
                ListRange list = ParseVariableDeclarationList();
                if (list.Count == 1)
                {
                    if (_kind == TokenKind.In)
                    {
                        Next();
                        forIn = true;
                    }
                    else if (AtOf())
                    {
                        Next();
                        forOf = true;
                    }
                }
                int decl = _ast.CreateVariableDeclaration(list, 0, declIdx, VarKindOf(tok));
                if (forIn || forOf)
                {
                    if (N(_ast._lists[list.Start]).B != 0)
                        Error("for-in loop variable declaration may not have an initializer");
                    into = decl;
                }
                else
                {
                    EnsurePatternInit(list);
                    initializer = decl;
                }
            }
            else
            {
                // for ( [lookahead not in {let, async of}] LeftHandSideExpression of ...); for await only excludes let.
                bool startsWithLet = _kind == TokenKind.Let && !_hasEscape;
                bool startsWithAsync = _kind == TokenKind.Async && !_hasEscape;
                int expr = ParseExpression();
                if (_kind == TokenKind.In)
                {
                    Next();
                    forIn = true;
                }
                else if (AtOf())
                {
                    if (startsWithLet) Error("The left-hand side of a for-of loop may not start with 'let'");
                    else if (startsWithAsync && !forAwait && K(expr) == NodeKind.Identifier) Error("The left-hand side of a for-of loop may not be 'async'");
                    Next();
                    forOf = true;
                }

                if (forIn || forOf)
                {
                    switch (K(expr))
                    {
                        case NodeKind.Identifier:
                        case NodeKind.PrivateDotExpression:
                        case NodeKind.MemberExpression:
                        case NodeKind.ArrayLiteral:
                        case NodeKind.ObjectLiteral:
                            into = PatternFromExpression(expr, binding: false);
                            break;
                        default:
                            Error("Invalid left-hand side in for-in or for-of");
                            NextStatement();
                            return _ast.CreateBadStatement(idx, _tokStart);
                    }
                }
                else
                {
                    initializer = expr;
                }
            }
            _scope.AllowIn = allowIn;
        }

        if (forIn)
        {
            if (forAwait) Error("for-await-of is only allowed with for-of");
            int source = ParseExpression();
            Expect(TokenKind.RightParenthesis);
            int body = ParseIterationStatement();
            return _ast.CreateForInStatement(into, source, body, idx);
        }
        if (forOf)
        {
            int source = ParseAssignmentExpression();
            Expect(TokenKind.RightParenthesis);
            int body = ParseIterationStatement();
            return _ast.CreateForOfStatement(into, source, body, idx, forAwait);
        }

        if (forAwait) Error("for-await-of is only allowed with for-of");
        Expect(TokenKind.Semicolon);

        int test = 0, update = 0;
        if (_kind != TokenKind.Semicolon) test = ParseExpression();
        Expect(TokenKind.Semicolon);
        if (_kind != TokenKind.RightParenthesis) update = ParseExpression();
        Expect(TokenKind.RightParenthesis);
        int loopBody = ParseIterationStatement();
        return _ast.CreateForStatement(initializer, update, test, loopBody, idx);
    }

    private void EnsurePatternInit(ListRange list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            ref NodeRecord decl = ref N(_ast._lists[list.Start + i]);
            if (K(decl.A) is NodeKind.ArrayPattern or NodeKind.ObjectPattern && decl.B == 0)
            {
                Error("Missing initializer in destructuring declaration");
                break;
            }
        }
    }

    private int ParseLexicalDeclaration(TokenKind tok)
    {
        int idx = Expect(tok);
        if (!_scope.AllowLet && tok != TokenKind.Var)
            Error("Lexical declaration cannot appear in a single-statement context");

        ListRange list = ParseVariableDeclarationList();
        EnsurePatternInit(list);
        RequireSemicolon();
        return _ast.CreateVariableDeclaration(list, 0, idx, VarKindOf(tok));
    }

    private int ParseDoWhileStatement()
    {
        bool inIteration = _scope.InIteration;
        _scope.InIteration = true;

        int idx = Expect(TokenKind.Do);
        int body;
        if (_kind == TokenKind.LeftBrace)
        {
            body = ParseBlockStatement();
        }
        else
        {
            _scope.AllowLet = false;
            body = ParseStatement();
        }

        Expect(TokenKind.While);
        Expect(TokenKind.LeftParenthesis);
        int test = ParseExpression();
        Expect(TokenKind.RightParenthesis);
        if (_kind == TokenKind.Semicolon) Next();

        _scope.InIteration = inIteration;
        return _ast.CreateDoWhileStatement(test, body, idx);
    }

    private int ParseWhileStatement()
    {
        int idx = Expect(TokenKind.While);
        Expect(TokenKind.LeftParenthesis);
        int test = ParseExpression();
        Expect(TokenKind.RightParenthesis);
        int body = ParseIterationStatement();
        return _ast.CreateWhileStatement(test, body, idx);
    }

    private int ParseIfStatement()
    {
        int idx = Expect(TokenKind.If);
        Expect(TokenKind.LeftParenthesis);
        int test = ParseExpression();
        Expect(TokenKind.RightParenthesis);

        int consequent;
        if (_kind == TokenKind.LeftBrace)
        {
            consequent = ParseBlockStatement();
        }
        else
        {
            _scope.AllowLet = false;
            consequent = ParseStatement();
        }

        int alternate = 0;
        if (_kind == TokenKind.Else)
        {
            Next();
            _scope.AllowLet = false;
            alternate = ParseStatement();
        }
        return _ast.CreateIfStatement(test, consequent, alternate, idx);
    }

    private int ParseBreakStatement()
    {
        int idx = Expect(TokenKind.Break);

        if (CanInsertSemicolon())
        {
            if (_kind == TokenKind.Semicolon) Next();
            if (!_scope.InIteration && !_scope.InSwitch) goto illegal;
            return _ast.CreateBreakStatement(0, idx);
        }

        TokenToBindingId();
        if (_kind == TokenKind.Identifier)
        {
            int label = ParseIdentifier();
            ref NodeRecord id = ref N(label);
            if (!HasLabel(id.C, id.D))
            {
                Error($"Undefined label '{new string(_src, id.C, id.D)}'");
                return _ast.CreateBadStatement(idx, _ast.GetEnd(label));
            }
            RequireSemicolon();
            return _ast.CreateBreakStatement(label, idx);
        }

        Expect(TokenKind.Identifier);

    illegal:
        Error("Illegal break statement");
        NextStatement();
        return _ast.CreateBadStatement(idx, _tokStart);
    }

    private int ParseContinueStatement()
    {
        int idx = Expect(TokenKind.Continue);

        if (CanInsertSemicolon())
        {
            if (_kind == TokenKind.Semicolon) Next();
            if (!_scope.InIteration) goto illegal;
            return _ast.CreateContinueStatement(0, idx);
        }

        TokenToBindingId();
        if (_kind == TokenKind.Identifier)
        {
            int label = ParseIdentifier();
            ref NodeRecord id = ref N(label);
            if (!HasLabel(id.C, id.D))
            {
                Error($"Undefined label '{new string(_src, id.C, id.D)}'");
                return _ast.CreateBadStatement(idx, _ast.GetEnd(label));
            }
            if (!_scope.InIteration) goto illegal;
            RequireSemicolon();
            return _ast.CreateContinueStatement(label, idx);
        }

        Expect(TokenKind.Identifier);

    illegal:
        Error("Illegal continue statement");
        NextStatement();
        return _ast.CreateBadStatement(idx, _tokStart);
    }

    /// <summary>Error recovery: skips to the next token that can start a statement.</summary>
    private void NextStatement()
    {
        while (true)
        {
            switch (_kind)
            {
                case TokenKind.Break:
                case TokenKind.Continue:
                case TokenKind.For:
                case TokenKind.If:
                case TokenKind.Return:
                case TokenKind.Switch:
                case TokenKind.Var:
                case TokenKind.Do:
                case TokenKind.Try:
                case TokenKind.With:
                case TokenKind.While:
                case TokenKind.Throw:
                case TokenKind.Catch:
                case TokenKind.Finally:
                    // Return only if progress was made since the last sync, or if we have not stalled
                    // 10 times at the same place; otherwise consume at least one token.
                    if (_tokStart == _recoverIdx && _recoverCount < 10)
                    {
                        _recoverCount++;
                        return;
                    }
                    if (_tokStart > _recoverIdx)
                    {
                        _recoverIdx = _tokStart;
                        _recoverCount = 0;
                        return;
                    }
                    break;
                case TokenKind.Eof:
                    return;
            }
            Next();
        }
    }
}
