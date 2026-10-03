using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Retro96.Engine.Network;
using Retro96.Engine.Dom;

namespace Retro96.Engine.Js;

// Exception types for interpreter control flow
public class JsTimeoutException : Exception { }
public class JsOutOfMemoryException : Exception { }
public class JsBreakException : Exception
{
    public string? Label { get; }
    public JsBreakException(string? label) { Label = label; }
}
public class JsContinueException : Exception
{
    public string? Label { get; }
    public JsContinueException(string? label) { Label = label; }
}
public class JsReturnException : Exception
{
    public JsValue Value { get; }
    public JsReturnException(JsValue value) { Value = value; }
}
public class JsThrownException : Exception
{
    public JsValue Value { get; }
    public JsThrownException(JsValue value) : base(value.ToJsString()) { Value = value; }
}
public class JsInterpreterException : Exception
{
    public JsInterpreterException(string message) : base(message) { }
}

public class JsInterpreter
{
    private JsScope _globalScope;
    private JsScope _currentScope;
    private DomDocument _document;
    private Action<string> _onNavigate;
    private Action<string> _setStatus;
    private int _timeLimitMs;
    private int _heapLimitBytes;
    private long _allocatedBytes;
    private Stopwatch _stopwatch;
    private List<ScheduledTimer> _timers = new();
    private Random _random = new();
    
    // Track current page URL for same-origin checks
    private string _currentPageUrl;
    
    // Timer support class
    private class ScheduledTimer
    {
        public JsValue Callback { get; set; } = JsValue.Undefined;
        public int Interval { get; set; }
        public long NextTick { get; set; }
        public bool Repeat { get; set; }
    }

    public JsInterpreter(JsScope globalScope, DomDocument document,
                          Action<string> onNavigate, Action<string> setStatus,
                          int timeLimitMs = 5000, int heapLimitBytes = 10_485_760)
    {
        _globalScope = globalScope;
        _currentScope = globalScope;
        _document = document;
        _onNavigate = onNavigate;
        _setStatus = setStatus;
        _timeLimitMs = timeLimitMs;
        _heapLimitBytes = heapLimitBytes;
        _stopwatch = new Stopwatch();
        _allocatedBytes = 0;
        _currentPageUrl = document?.BaseUrl?.ToAbsolute() ?? "unknown";
    }
    
    /// <summary>
    /// Update current page URL (call when navigating to new page)
    /// </summary>
    public void SetCurrentPageUrl(string url) { _currentPageUrl = url; }
    
    /// <summary>
    /// Execute a parsed script. Returns the last evaluated value.
    /// </summary>
    public JsValue Execute(ProgramNode program)
    {
        _stopwatch.Restart();
        try
        {
            // Hoist all function declarations and var declarations before executing
            HoistFunctionsAndVars(program.Body, _currentScope);

            JsValue result = JsValue.Undefined;
            foreach (var stmt in program.Body)
            {
                result = ExecuteStatement(stmt);
                CheckTimeout();
            }
            return result;
        }
        catch (JsTimeoutException)
        {
            _setStatus("Script execution timed out");
            return JsValue.Undefined;
        }
        catch (JsOutOfMemoryException)
        {
            _setStatus("Script ran out of memory");
            return JsValue.Undefined;
        }
    }
    
    /// <summary>
    /// Eval a string in the current scope.
    /// Same-origin only: check that document.URL matches the current page URL.
    /// </summary>
    public JsValue EvalString(string source, JsScope scope)
    {
        // Security: same-origin check
        string currentUrl = _currentPageUrl;
        string documentUrl = _document?.BaseUrl?.ToAbsolute() ?? "unknown";
        
        try
        {
            var currentParsed = ParsedUrl.Parse(currentUrl);
            var docParsed = ParsedUrl.Parse(documentUrl);
            
            bool sameOrigin = currentParsed.Scheme == docParsed.Scheme
                            && currentParsed.Host == docParsed.Host
                            && currentParsed.Port == docParsed.Port;
            
            if (!sameOrigin)
            {
                _setStatus("eval() blocked: same-origin policy");
                return JsValue.Undefined;
            }
        }
        catch
        {
            _setStatus("eval() blocked: invalid URL");
            return JsValue.Undefined;
        }
        
        var program = JsParser.Parse(source);
        var oldScope = _currentScope;
        _currentScope = scope;
        try
        {
            return Execute(program);
        }
        finally
        {
            _currentScope = oldScope;
        }
    }
    
    /// <summary>
    /// Fire a DOM event handler on an element.
    /// </summary>
    public void FireEvent(DomElement element, string eventName,
                           Dictionary<string, JsValue>? eventProps = null)
    {
        if (!element.EventHandlers.TryGetValue(eventName, out var handlerSource))
            return;
        
        JsObject? eventObj = null;
        if (eventProps != null)
        {
            eventObj = new JsObject();
            foreach (var kvp in eventProps)
                eventObj.Set(kvp.Key, kvp.Value);
        }
        
        try
        {
            var program = JsParser.Parse(handlerSource);
            var scope = _currentScope.NewChild();
            if (eventObj != null)
                scope.Define("event", JsValue.FromObject(eventObj));
            Execute(program);
        }
        catch (Exception ex)
        {
            _setStatus($"Error in {eventName} handler: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Tick timers (called by BrowserCanvas timer).
    /// </summary>
    public void TickTimers()
    {
        long now = Environment.TickCount64;
        var toRemove = new List<ScheduledTimer>();
        
        foreach (var timer in _timers)
        {
            if (now >= timer.NextTick)
            {
                try
                {
                    var func = timer.Callback.GetFunction();
                    func.Native!(JsValue.Undefined, Array.Empty<JsValue>());
                }
                catch { }
                
                if (timer.Repeat)
                    timer.NextTick = now + timer.Interval;
                else
                    toRemove.Add(timer);
            }
        }
        
        foreach (var t in toRemove)
            _timers.Remove(t);
    }

    // ------------------- Statement Execution -------------------
    private JsValue ExecuteStatement(object stmt)
    {
        CheckTimeout();
        
        return stmt switch
        {
            BlockStatement block => ExecuteBlock(block),
            VarDeclaration varDecl => ExecuteVarDeclaration(varDecl),
            ExpressionStatement exprStmt => ExecuteExpression(exprStmt.Expression),
            IfStatement ifStmt => ExecuteIf(ifStmt),
            WhileStatement whileStmt => ExecuteWhile(whileStmt),
            DoWhileStatement doWhileStmt => ExecuteDoWhile(doWhileStmt),
            ForStatement forStmt => ExecuteFor(forStmt),
            ForInStatement forInStmt => ExecuteForIn(forInStmt),
            ReturnStatement returnStmt => ExecuteReturn(returnStmt),
            BreakStatement breakStmt => throw new JsBreakException(breakStmt.Label),
            ContinueStatement continueStmt => throw new JsContinueException(continueStmt.Label),
            SwitchStatement switchStmt => ExecuteSwitch(switchStmt),
            ThrowStatement throwStmt => ExecuteThrow(throwStmt),
            TryStatement tryStmt => ExecuteTry(tryStmt),
            LabeledStatement labeledStmt => ExecuteLabeled(labeledStmt),
            WithStatement withStmt => ExecuteWith(withStmt),
            FunctionDeclaration funcDecl => ExecuteFunctionDeclaration(funcDecl),
            EmptyStatement => JsValue.Undefined,
            _ => throw new JsInterpreterException($"Unknown statement type: {stmt.GetType()}")
        };
    }

    private JsValue ExecuteBlock(BlockStatement block)
    {
        JsValue result = JsValue.Undefined;
        foreach (var stmt in block.Body)
        {
            result = ExecuteStatement(stmt);
        }
        return result;
    }

    private JsValue ExecuteVarDeclaration(VarDeclaration varDecl)
    {
        foreach (var declarator in varDecl.Declarations)
        {
            JsValue value = declarator.Init != null 
                ? ExecuteExpression(declarator.Init) 
                : JsValue.Undefined;
            _currentScope.Define(declarator.Id.Name, value);
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteIf(IfStatement ifStmt)
    {
        JsValue testVal = ExecuteExpression(ifStmt.Test);
        if (ToBoolean(testVal))
            return ExecuteStatement(ifStmt.Consequent);
        else if (ifStmt.Alternate != null)
            return ExecuteStatement(ifStmt.Alternate);
        return JsValue.Undefined;
    }

    private JsValue ExecuteWhile(WhileStatement whileStmt)
    {
        while (true)
        {
            CheckTimeout();
            JsValue testVal = ExecuteExpression(whileStmt.Test);
            if (!ToBoolean(testVal)) break;

            try
            {
                ExecuteStatement(whileStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (bex.Label == null) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (cex.Label == null) continue;
                throw;
            }
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteDoWhile(DoWhileStatement doWhileStmt)
    {
        bool firstIteration = true;
        while (true)
        {
            CheckTimeout();
            if (!firstIteration)
            {
                JsValue testVal = ExecuteExpression(doWhileStmt.Test);
                if (!ToBoolean(testVal)) break;
            }
            firstIteration = false;

            try
            {
                ExecuteStatement(doWhileStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (bex.Label == null) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (cex.Label == null) continue;
                throw;
            }
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteFor(ForStatement forStmt)
    {
        // Init
        if (forStmt.Init != null)
        {
            if (forStmt.Init is VarDeclaration varDecl)
                ExecuteVarDeclaration(varDecl);
            else
                ExecuteExpression(forStmt.Init);
        }

        // Loop
        while (true)
        {
            CheckTimeout();
            // Test
            if (forStmt.Test != null)
            {
                JsValue testVal = ExecuteExpression(forStmt.Test);
                if (!ToBoolean(testVal)) break;
            }

            // Body
            try
            {
                ExecuteStatement(forStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (bex.Label == null) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (cex.Label == null)
                {
                    // Execute update then continue
                    if (forStmt.Update != null)
                        ExecuteExpression(forStmt.Update);
                    continue;
                }
                throw;
            }

            // Update
            if (forStmt.Update != null)
                ExecuteExpression(forStmt.Update);
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteForIn(ForInStatement forInStmt)
    {
        JsValue objVal = ExecuteExpression(forInStmt.Right);
        if (objVal.Type == JsType.Null || objVal.Type == JsType.Undefined)
            return JsValue.Undefined;

        JsObject? obj = objVal.Type == JsType.Object ? objVal.GetObject() : null;
        if (obj == null) return JsValue.Undefined;

        var keys = obj.OwnEnumerableKeys();
        foreach (var key in keys)
        {
            // Assign key to left side
            if (forInStmt.Left is VarDeclaration varDecl)
            {
                var id = varDecl.Declarations[0].Id;
                _currentScope.Set(id.Name, JsValue.From(key));
            }
            else if (forInStmt.Left is Identifier ident)
            {
                _currentScope.Set(ident.Name, JsValue.From(key));
            }

            // Execute body
            try
            {
                ExecuteStatement(forInStmt.Body);
            }
            catch (JsBreakException bex)
            {
                if (bex.Label == null) break;
                throw;
            }
            catch (JsContinueException cex)
            {
                if (cex.Label == null) continue;
                throw;
            }
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteReturn(ReturnStatement returnStmt)
    {
        JsValue value = returnStmt.Argument != null 
            ? ExecuteExpression(returnStmt.Argument) 
            : JsValue.Undefined;
        throw new JsReturnException(value);
    }

    private JsValue ExecuteSwitch(SwitchStatement switchStmt)
    {
        JsValue discVal = ExecuteExpression(switchStmt.Discriminant);
        bool foundCase = false;
        int defaultIndex = -1;

        // Find default case first
        for (int i = 0; i < switchStmt.Cases.Count; i++)
        {
            if (switchStmt.Cases[i].Test == null)
            {
                defaultIndex = i;
                break;
            }
        }

        // Find matching case
        for (int i = 0; i < switchStmt.Cases.Count; i++)
        {
            var caseClause = switchStmt.Cases[i];
            if (caseClause.Test == null) continue;

            JsValue caseVal = ExecuteExpression(caseClause.Test);
            if (StrictEqual(discVal, caseVal))
            {
                foundCase = true;
                // Execute from this case onward
                for (int j = i; j < switchStmt.Cases.Count; j++)
                {
                    var caseToExec = switchStmt.Cases[j];
                    foreach (var stmt in caseToExec.Consequent)
                    {
                        try
                        {
                            ExecuteStatement(stmt);
                        }
                        catch (JsBreakException bex)
                        {
                            if (bex.Label == null) goto EndSwitch;
                            throw;
                        }
                    }
                }
                break;
            }
        }

        // Execute default if no case matched
        if (!foundCase && defaultIndex != -1)
        {
            var defaultCase = switchStmt.Cases[defaultIndex];
            foreach (var stmt in defaultCase.Consequent)
            {
                try
                {
                    ExecuteStatement(stmt);
                }
                catch (JsBreakException bex)
                {
                    if (bex.Label == null) goto EndSwitch;
                    throw;
                }
            }
        }

    EndSwitch:
        return JsValue.Undefined;
    }

    private JsValue ExecuteThrow(ThrowStatement throwStmt)
    {
        JsValue value = ExecuteExpression(throwStmt.Argument);
        throw new JsThrownException(value);
    }

    private JsValue ExecuteTry(TryStatement tryStmt)
    {
        try
        {
            ExecuteStatement(tryStmt.Block);
        }
        catch (JsThrownException thrownEx)
        {
            if (tryStmt.Handler != null)
            {
                var catchClause = tryStmt.Handler;
                var oldScope = _currentScope;
                _currentScope = _currentScope.NewChild();
                try
                {
                    _currentScope.Define(catchClause.Param.Name, thrownEx.Value);
                    ExecuteStatement(catchClause.Body);
                }
                finally
                {
                    _currentScope = oldScope;
                }
            }
        }
        finally
        {
            if (tryStmt.Finalizer != null)
                ExecuteStatement(tryStmt.Finalizer);
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteLabeled(LabeledStatement labeledStmt)
    {
        try
        {
            ExecuteStatement(labeledStmt.Body);
        }
        catch (JsBreakException bex)
        {
            if (bex.Label == labeledStmt.Label)
                return JsValue.Undefined;
            throw;
        }
        return JsValue.Undefined;
    }

    private JsValue ExecuteWith(WithStatement withStmt)
    {
        JsValue objVal = ExecuteExpression(withStmt.Object);
        if (objVal.Type != JsType.Object)
            return ExecuteStatement(withStmt.Body);

        var obj = objVal.GetObject();
        var withScope = _currentScope.NewChild();

        // Copy all enumerable properties (own + prototype chain) into with-scope
        // so they shadow outer variables during body execution
        JsObject? current = obj;
        while (current != null)
        {
            foreach (var key in current.OwnEnumerableKeys())
                withScope.Define(key, current.Get(key));
            current = current.Prototype;
        }

        var oldScope = _currentScope;
        _currentScope = withScope;
        try
        {
            return ExecuteStatement(withStmt.Body);
        }
        finally
        {
            _currentScope = oldScope;
        }
    }

    private JsValue ExecuteFunctionDeclaration(FunctionDeclaration funcDecl)
    {
        // Function declarations are hoisted before execution begins via HoistFunctionsAndVars.
        // Nothing to do here at runtime.
        return JsValue.Undefined;
    }

    // ------------------- Expression Execution -------------------
    private JsValue ExecuteExpression(object expr)
    {
        CheckTimeout();
        
        return expr switch
        {
            AssignmentExpr assignExpr => ExecuteAssignment(assignExpr),
            BinaryExpr binaryExpr => ExecuteBinary(binaryExpr),
            LogicalExpr logicalExpr => ExecuteLogical(logicalExpr),
            UnaryExpr unaryExpr => ExecuteUnary(unaryExpr),
            UpdateExpr updateExpr => ExecuteUpdate(updateExpr),
            TernaryExpr ternaryExpr => ExecuteTernary(ternaryExpr),
            CallExpr callExpr => ExecuteCall(callExpr),
            NewExpr newExpr => ExecuteNew(newExpr),
            MemberExpr memberExpr => ExecuteMember(memberExpr),
            FunctionExpr funcExpr => ExecuteFunctionExpr(funcExpr),
            ArrayExpr arrayExpr => ExecuteArray(arrayExpr),
            ObjectExpr objExpr => ExecuteObject(objExpr),
            Identifier ident => _currentScope.Get(ident.Name),
            NumberLiteral num => JsValue.From(num.Value),
            StringLiteral str => JsValue.From(str.Value),
            BoolLiteral boolean => JsValue.From(boolean.Value),
            NullLiteral => JsValue.Null,
            ThisExpr => GetThis(),
            RegexLiteral regex => ExecuteRegex(regex),
            VoidExpr voidExpr => ExecuteVoid(voidExpr),
            TypeofExpr typeofExpr => ExecuteTypeof(typeofExpr),
            DeleteExpr deleteExpr => ExecuteDelete(deleteExpr),
            InExpr inExpr => ExecuteIn(inExpr),
            InstanceofExpr instanceofExpr => ExecuteInstanceof(instanceofExpr),
            _ => throw new JsInterpreterException($"Unknown expression type: {expr.GetType()}")
        };
    }

    private JsValue ExecuteAssignment(AssignmentExpr assignExpr)
    {
        JsValue rightVal = ExecuteExpression(assignExpr.Right);
        
        if (assignExpr.Left is Identifier ident)
        {
            JsValue leftVal = _currentScope.Get(ident.Name);
            JsValue newValue = ApplyCompoundOperator(leftVal, rightVal, assignExpr.Operator);
            _currentScope.Set(ident.Name, newValue);
            return newValue;
        }
        else if (assignExpr.Left is MemberExpr memberExpr)
        {
            JsValue objVal = ExecuteExpression(memberExpr.Object);
            string propName = GetMemberPropertyName(memberExpr);
            JsValue leftVal = GetProperty(objVal, propName);
            JsValue newValue = ApplyCompoundOperator(leftVal, rightVal, assignExpr.Operator);
            SetProperty(objVal, propName, newValue);
            return newValue;
        }
        else
        {
            throw new JsInterpreterException("Invalid left side in assignment");
        }
    }

    private JsValue ExecuteBinary(BinaryExpr binaryExpr)
    {
        JsValue left = ExecuteExpression(binaryExpr.Left);
        JsValue right = ExecuteExpression(binaryExpr.Right);

        return binaryExpr.Operator switch
        {
            "+" => HandleAdd(left, right),
            "-" => JsValue.From(left.ToNumber() - right.ToNumber()),
            "*" => JsValue.From(left.ToNumber() * right.ToNumber()),
            "/" => HandleDivide(left, right),
            "%" => JsValue.From(left.ToNumber() % right.ToNumber()),
            "&" => JsValue.From((long)left.ToNumber() & (long)right.ToNumber()),
            "|" => JsValue.From((long)left.ToNumber() | (long)right.ToNumber()),
            "^" => JsValue.From((long)left.ToNumber() ^ (long)right.ToNumber()),
            "<<" => JsValue.From((long)left.ToNumber() << (int)((long)right.ToNumber() & 0x1F)),
            ">>" => JsValue.From((long)left.ToNumber() >> (int)((long)right.ToNumber() & 0x1F)),
            ">>>" => JsValue.From((ulong)(long)left.ToNumber() >> (int)((long)right.ToNumber() & 0x1F)),
            "==" => JsValue.From(AbstractEqual(left, right)),
            "!=" => JsValue.From(!AbstractEqual(left, right)),
            "===" => JsValue.From(StrictEqual(left, right)),
            "!==" => JsValue.From(!StrictEqual(left, right)),
            "<" => JsValue.From(Compare(left, right) < 0),
            ">" => JsValue.From(Compare(left, right) > 0),
            "<=" => JsValue.From(Compare(left, right) <= 0),
            ">=" => JsValue.From(Compare(left, right) >= 0),
            "instanceof" => HandleInstanceof(left, right),
            "in" => HandleIn(left, right),
            "," => right,  // comma operator: evaluate both, return right
            _ => throw new JsInterpreterException($"Unknown binary operator: {binaryExpr.Operator}")
        };
    }

    private JsValue ExecuteLogical(LogicalExpr logicalExpr)
    {
        JsValue left = ExecuteExpression(logicalExpr.Left);
        return logicalExpr.Operator switch
        {
            "&&" => ToBoolean(left) ? ExecuteExpression(logicalExpr.Right) : left,
            "||" => ToBoolean(left) ? left : ExecuteExpression(logicalExpr.Right),
            _ => throw new JsInterpreterException($"Unknown logical operator: {logicalExpr.Operator}")
        };
    }

    private JsValue ExecuteUnary(UnaryExpr unaryExpr)
    {
        return unaryExpr.Operator switch
        {
            "!" => JsValue.From(!ToBoolean(ExecuteExpression(unaryExpr.Argument))),
            "~" => JsValue.From(~(long)ExecuteExpression(unaryExpr.Argument).ToNumber()),
            "+" => JsValue.From(ExecuteExpression(unaryExpr.Argument).ToNumber()),
            "-" => JsValue.From(-ExecuteExpression(unaryExpr.Argument).ToNumber()),
            "typeof" => JsValue.From(Typeof(ExecuteExpression(unaryExpr.Argument))),
            "void" => ExecuteVoid(new VoidExpr(unaryExpr.Argument)),
            "delete" => ExecuteDelete(new DeleteExpr(unaryExpr.Argument)),
            "++" => ExecutePrefixUpdate(unaryExpr.Argument, 1),
            "--" => ExecutePrefixUpdate(unaryExpr.Argument, -1),
            _ => throw new JsInterpreterException($"Unknown unary operator: {unaryExpr.Operator}")
        };
    }

    private JsValue ExecuteUpdate(UpdateExpr updateExpr)
    {
        return updateExpr.Operator switch
        {
            "++" => ExecutePostfixUpdate(updateExpr.Argument, 1),
            "--" => ExecutePostfixUpdate(updateExpr.Argument, -1),
            _ => throw new JsInterpreterException($"Unknown update operator: {updateExpr.Operator}")
        };
    }

    private JsValue ExecuteTernary(TernaryExpr ternaryExpr)
    {
        JsValue test = ExecuteExpression(ternaryExpr.Test);
        return ToBoolean(test) 
            ? ExecuteExpression(ternaryExpr.Consequent) 
            : ExecuteExpression(ternaryExpr.Alternate);
    }

    private JsValue ExecuteCall(CallExpr callExpr)
    {
        JsValue callee = ExecuteExpression(callExpr.Callee);
        if (callee.Type != JsType.Function)
            throw new JsInterpreterException("Callee is not a function");

        List<JsValue> args = new();
        foreach (var arg in callExpr.Arguments)
            args.Add(ExecuteExpression(arg));

        JsValue thisValue = JsValue.Undefined;
        if (callExpr.Callee is MemberExpr memberCallee)
            thisValue = ExecuteExpression(memberCallee.Object);

        JsFunction func = callee.GetFunction();
        if (func.Native != null)
            return func.Native(thisValue, args.ToArray());

        // User-defined function
        var funcScope = func.ClosureScope.NewChild();
        for (int i = 0; i < func.Params.Count; i++)
        {
            string paramName = func.Params[i];
            JsValue argValue = i < args.Count ? args[i] : JsValue.Undefined;
            funcScope.Define(paramName, argValue);
        }
        funcScope.Define("this", thisValue);

        // Build arguments object
        var argsObj = new JsObject();
        for (int i = 0; i < args.Count; i++)
            argsObj.Set(i.ToString(), args[i]);
        argsObj.Set("length", JsValue.From(args.Count));
        funcScope.Define("arguments", JsValue.FromObject(argsObj));

        // Hoist function declarations and vars in function body
        if (func.Body?.Body is BlockStatement callBody)
            HoistFunctionsAndVars(callBody.Body, funcScope);

        var oldScope = _currentScope;
        _currentScope = funcScope;
        try
        {
            ExecuteStatement(func.Body!.Body!);
            return JsValue.Undefined;
        }
        catch (JsReturnException retEx)
        {
            return retEx.Value;
        }
        finally
        {
            _currentScope = oldScope;
        }
    }

    private JsValue ExecuteNew(NewExpr newExpr)
    {
        JsValue callee = ExecuteExpression(newExpr.Callee);
        if (callee.Type != JsType.Function)
            throw new JsInterpreterException("Constructor is not a function");

        List<JsValue> args = new();
        foreach (var arg in newExpr.Arguments)
            args.Add(ExecuteExpression(arg));

        JsFunction constructor = callee.GetFunction();
        JsObject newObj = new JsObject();
        
        // Set prototype from constructor's prototype property
        JsValue protoVal = constructor.Get("prototype");
        newObj.Prototype = protoVal.Type == JsType.Object 
            ? protoVal.GetObject() 
            : null;

        // Call constructor with newObj as this
        var result = ExecuteCallWithThis(constructor, JsValue.FromObject(newObj), args);
        return (result.Type == JsType.Object || result.Type == JsType.Function) 
            ? result 
            : JsValue.FromObject(newObj);
    }

    private JsValue ExecuteMember(MemberExpr memberExpr)
    {
        JsValue objVal = ExecuteExpression(memberExpr.Object);
        string propName = GetMemberPropertyName(memberExpr);
        return GetProperty(objVal, propName);
    }

    private JsValue ExecuteFunctionExpr(FunctionExpr funcExpr)
    {
        var paramNames = funcExpr.Params.Select(p => p.Name).ToList();
        var func = new JsFunction(funcExpr, paramNames, _currentScope);
        return JsValue.FromFunction(func);
    }

    private JsValue ExecuteArray(ArrayExpr arrayExpr)
    {
        JsObject arrayObj = new JsObject();
        int index = 0;
        foreach (var elem in arrayExpr.Elements)
        {
            if (elem != null)
                arrayObj.Set(index.ToString(), ExecuteExpression(elem));
            else
                arrayObj.Set(index.ToString(), JsValue.Undefined);
            index++;
        }
        arrayObj.Set("length", JsValue.From((double)index));
        // Assign Array.prototype so array methods (push, pop, etc.) are accessible
        var arrayConstructor = _currentScope.Get("Array");
        if (arrayConstructor.Type == JsType.Function)
        {
            var proto = arrayConstructor.GetFunction().Get("prototype");
            if (proto.Type == JsType.Object)
                arrayObj.Prototype = proto.GetObject();
        }
        return JsValue.FromObject(arrayObj);
    }

    private JsValue ExecuteObject(ObjectExpr objExpr)
    {
        JsObject obj = new JsObject();
        foreach (var prop in objExpr.Properties)
        {
            string key = prop.Key is Identifier id ? id.Name : ExecuteExpression(prop.Key).ToJsString();
            JsValue value = ExecuteExpression(prop.Value);
            obj.Set(key, value);
        }
        return JsValue.FromObject(obj);
    }

    private JsValue ExecuteRegex(RegexLiteral regex)
    {
        JsObject regexObj = new JsObject();
        regexObj.Set("source", JsValue.From(regex.Pattern));
        regexObj.Set("flags", JsValue.From(regex.Flags));
        regexObj.Set("global", JsValue.From(regex.Flags.Contains("g")));
        regexObj.Set("ignoreCase", JsValue.From(regex.Flags.Contains("i")));
        regexObj.Set("lastIndex", JsValue.From(0));
        try
        {
            var options = regex.Flags.Contains("i") ? RegexOptions.IgnoreCase : RegexOptions.None;
            var _ = new Regex(regex.Pattern, options); // validate pattern
        }
        catch { }
        // Assign RegExp.prototype so test/exec methods are accessible
        var regexpConstructor = _currentScope.Get("RegExp");
        if (regexpConstructor.Type == JsType.Function)
        {
            var proto = regexpConstructor.GetFunction().Get("prototype");
            if (proto.Type == JsType.Object)
                regexObj.Prototype = proto.GetObject();
        }
        return JsValue.FromObject(regexObj);
    }

    private JsValue ExecuteVoid(VoidExpr voidExpr)
    {
        ExecuteExpression(voidExpr.Argument);
        return JsValue.Undefined;
    }

    private JsValue ExecuteTypeof(TypeofExpr typeofExpr)
    {
        JsValue val = ExecuteExpression(typeofExpr.Argument);
        return JsValue.From(Typeof(val));
    }

    private JsValue ExecuteDelete(DeleteExpr deleteExpr)
    {
        if (deleteExpr.Argument is Identifier)
            return JsValue.From(false); // Can't delete variables
        
        if (deleteExpr.Argument is MemberExpr memberExpr)
        {
            JsValue objVal = ExecuteExpression(memberExpr.Object);
            if (objVal.Type != JsType.Object) return JsValue.From(false);
            string propName = GetMemberPropertyName(memberExpr);
            return JsValue.From(objVal.GetObject().Properties.Remove(propName));
        }
        
        return JsValue.From(true); // No-op
    }

    private JsValue ExecuteIn(InExpr inExpr)
    {
        JsValue left = ExecuteExpression(inExpr.Left);
        JsValue right = ExecuteExpression(inExpr.Right);
        if (right.Type != JsType.Object) return JsValue.From(false);
        string propName = left.ToJsString();
        return JsValue.From(right.GetObject().Has(propName));
    }

    private JsValue ExecuteInstanceof(InstanceofExpr instanceofExpr)
    {
        JsValue left = ExecuteExpression(instanceofExpr.Left);
        JsValue right = ExecuteExpression(instanceofExpr.Right);
        if (left.Type != JsType.Object || right.Type != JsType.Function)
            return JsValue.From(false);
        
        var obj = left.GetObject();
        var func = right.GetFunction();
        var proto = obj.Prototype;
        while (proto != null)
        {
            if (proto == func.Prototype) return JsValue.From(true);
            proto = proto.Prototype;
        }
        return JsValue.From(false);
    }

    // ------------------- Helper Methods -------------------
    private void CheckTimeout()
    {
        if (_stopwatch.ElapsedMilliseconds > _timeLimitMs)
            throw new JsTimeoutException();
    }

    private void CheckMemory()
    {
        if (_allocatedBytes > _heapLimitBytes)
            throw new JsOutOfMemoryException();
    }

    private JsValue GetThis()
    {
        return _currentScope.Get("this");
    }

    private static bool ToBoolean(JsValue value)
    {
        return value.ToBoolean();
    }

    private static string Typeof(JsValue value)
    {
        return value.Type switch
        {
            JsType.Undefined => "undefined",
            JsType.Null => "object", // JS quirk
            JsType.Boolean => "boolean",
            JsType.Number => "number",
            JsType.String => "string",
            JsType.Function => "function",
            JsType.Object => "object",
            _ => "undefined"
        };
    }

    private static bool AbstractEqual(JsValue a, JsValue b)
    {
        return a.AbstractEquals(b);
    }

    private static bool StrictEqual(JsValue a, JsValue b)
    {
        return a.StrictEquals(b);
    }

    private static int Compare(JsValue a, JsValue b)
    {
        // Convert to numbers if both are numbers, else to strings
        if (a.Type == JsType.Number && b.Type == JsType.Number)
        {
            double numA = a.ToNumber();
            double numB = b.ToNumber();
            return numA.CompareTo(numB);
        }
        string strA = a.ToJsString();
        string strB = b.ToJsString();
        return string.Compare(strA, strB, StringComparison.Ordinal);
    }

    private JsValue ApplyCompoundOperator(JsValue left, JsValue right, string op)
    {
        return op switch
        {
            "=" => right,
            "+=" => HandleAdd(left, right),
            "-=" => JsValue.From(left.ToNumber() - right.ToNumber()),
            "*=" => JsValue.From(left.ToNumber() * right.ToNumber()),
            "/=" => HandleDivide(left, right),
            "%=" => JsValue.From(left.ToNumber() % right.ToNumber()),
            _ => right
        };
    }

    private JsValue HandleAdd(JsValue left, JsValue right)
    {
        if (left.Type == JsType.String || right.Type == JsType.String)
            return JsValue.From(left.ToJsString() + right.ToJsString());
        return JsValue.From(left.ToNumber() + right.ToNumber());
    }

    private JsValue HandleDivide(JsValue left, JsValue right)
    {
        // .NET handles all cases correctly: 1.0/0.0=Infinity, -1.0/0.0=-Infinity, 0.0/0.0=NaN
        return JsValue.From(left.ToNumber() / right.ToNumber());
    }

    private JsValue HandleInstanceof(JsValue left, JsValue right)
    {
        if (left.Type != JsType.Object || right.Type != JsType.Function)
            return JsValue.From(false);
        
        var obj = left.GetObject();
        var func = right.GetFunction();
        var proto = obj.Prototype;
        while (proto != null)
        {
            if (proto == func.Prototype) return JsValue.From(true);
            proto = proto.Prototype;
        }
        return JsValue.From(false);
    }

    private JsValue HandleIn(JsValue left, JsValue right)
    {
        if (right.Type != JsType.Object) return JsValue.From(false);
        string propName = left.ToJsString();
        return JsValue.From(right.GetObject().Has(propName));
    }

    private string GetMemberPropertyName(MemberExpr memberExpr)
    {
        if (memberExpr.Computed)
        {
            JsValue propVal = ExecuteExpression(memberExpr.Property);
            return propVal.ToJsString();
        }
        return ((Identifier)memberExpr.Property).Name;
    }

    private JsValue GetProperty(JsValue objVal, string propName)
    {
        if (objVal.Type != JsType.Object) return JsValue.Undefined;
        return objVal.GetObject().Get(propName);
    }

    private void SetProperty(JsValue objVal, string propName, JsValue value)
    {
        if (objVal.Type != JsType.Object) return;
        objVal.GetObject().Set(propName, value);
    }

    private JsValue ExecutePrefixUpdate(object operand, double delta)
    {
        if (operand is Identifier ident)
        {
            JsValue oldVal = _currentScope.Get(ident.Name);
            double num = oldVal.ToNumber();
            JsValue newVal = JsValue.From(num + delta);
            _currentScope.Set(ident.Name, newVal);
            return newVal;
        }
        else if (operand is MemberExpr memberExpr)
        {
            JsValue objVal = ExecuteExpression(memberExpr.Object);
            string propName = GetMemberPropertyName(memberExpr);
            JsValue oldVal = GetProperty(objVal, propName);
            double num = oldVal.ToNumber();
            JsValue newVal = JsValue.From(num + delta);
            SetProperty(objVal, propName, newVal);
            return newVal;
        }
        throw new JsInterpreterException("Invalid operand for update expression");
    }

    private JsValue ExecutePostfixUpdate(object operand, double delta)
    {
        if (operand is Identifier ident)
        {
            JsValue oldVal = _currentScope.Get(ident.Name);
            double num = oldVal.ToNumber();
            JsValue newVal = JsValue.From(num + delta);
            _currentScope.Set(ident.Name, newVal);
            return oldVal;
        }
        else if (operand is MemberExpr memberExpr)
        {
            JsValue objVal = ExecuteExpression(memberExpr.Object);
            string propName = GetMemberPropertyName(memberExpr);
            JsValue oldVal = GetProperty(objVal, propName);
            double num = oldVal.ToNumber();
            JsValue newVal = JsValue.From(num + delta);
            SetProperty(objVal, propName, newVal);
            return oldVal;
        }
        throw new JsInterpreterException("Invalid operand for update expression");
    }

    private JsValue ExecuteCallWithThis(JsFunction func, JsValue thisValue, List<JsValue> args)
    {
        if (func.Native != null)
            return func.Native(thisValue, args.ToArray());

        var funcScope = func.ClosureScope.NewChild();
        for (int i = 0; i < func.Params.Count; i++)
        {
            string paramName = func.Params[i];
            JsValue argValue = i < args.Count ? args[i] : JsValue.Undefined;
            funcScope.Define(paramName, argValue);
        }
        funcScope.Define("this", thisValue);

        // Build arguments object
        var argsObj = new JsObject();
        for (int i = 0; i < args.Count; i++)
            argsObj.Set(i.ToString(), args[i]);
        argsObj.Set("length", JsValue.From(args.Count));
        funcScope.Define("arguments", JsValue.FromObject(argsObj));

        // Hoist function declarations and var declarations in function body
        if (func.Body?.Body is BlockStatement funcBody)
            HoistFunctionsAndVars(funcBody.Body, funcScope);

        var oldScope = _currentScope;
        _currentScope = funcScope;
        try
        {
            ExecuteStatement(func.Body!.Body!);
            return JsValue.Undefined;
        }
        catch (JsReturnException retEx)
        {
            return retEx.Value;
        }
        finally
        {
            _currentScope = oldScope;
        }
    }

    // ------------------- Hoisting -------------------
    private void HoistFunctionsAndVars(IReadOnlyList<object> stmts, JsScope targetScope)
    {
        foreach (var stmt in stmts)
            HoistOne(stmt, targetScope);
    }

    private void HoistOne(object stmt, JsScope targetScope)
    {
        switch (stmt)
        {
            case FunctionDeclaration funcDecl:
                var funcExpr = new FunctionExpr(funcDecl.Id, funcDecl.Params, funcDecl.Body);
                var paramNames = funcDecl.Params.Select(p => p.Name).ToList();
                var func = new JsFunction(funcExpr, paramNames, targetScope);
                targetScope.Define(funcDecl.Id.Name, JsValue.FromFunction(func));
                break;
            case VarDeclaration varDecl:
                foreach (var d in varDecl.Declarations)
                    if (!targetScope.Has(d.Id.Name))
                        targetScope.Define(d.Id.Name, JsValue.Undefined);
                break;
            case BlockStatement block:
                foreach (var s in block.Body) HoistOne(s, targetScope);
                break;
            case IfStatement ifStmt:
                HoistOne(ifStmt.Consequent, targetScope);
                if (ifStmt.Alternate != null) HoistOne(ifStmt.Alternate, targetScope);
                break;
            case WhileStatement w:
                HoistOne(w.Body, targetScope);
                break;
            case DoWhileStatement dw:
                HoistOne(dw.Body, targetScope);
                break;
            case ForStatement f:
                if (f.Init != null) HoistOne(f.Init, targetScope);
                HoistOne(f.Body, targetScope);
                break;
            case ForInStatement fi:
                HoistOne(fi.Body, targetScope);
                break;
            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                    foreach (var s in c.Consequent) HoistOne(s, targetScope);
                break;
            case TryStatement t:
                HoistOne(t.Block, targetScope);
                if (t.Handler != null) HoistOne(t.Handler.Body, targetScope);
                if (t.Finalizer != null) HoistOne(t.Finalizer, targetScope);
                break;
            case LabeledStatement ls:
                HoistOne(ls.Body, targetScope);
                break;
            case WithStatement ws:
                HoistOne(ws.Body, targetScope);
                break;
        }
    }

    // ------------------- Deferred Bindings -------------------
    /// <summary>
    /// Register interpreter-aware bindings that need a live interpreter reference:
    /// eval, Function.prototype.call/apply, and setTimeout/setInterval string support.
    /// Call after constructing JsInterpreter, JsRuntime.PopulateGlobalScope, and DomBindings.RegisterAll.
    /// </summary>
    public void RegisterDeferredBindings()
    {
        // Properly bind eval() to this interpreter
        _globalScope.Define("eval", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            if (args.Length == 0) return JsValue.Undefined;
            return EvalString(args[0].ToJsString(), _currentScope);
        }, _globalScope, "eval")));

        // Add call() and apply() to Function.prototype
        var funcConstructor = _globalScope.Get("Function");
        JsObject funcProto;
        if (funcConstructor.Type == JsType.Function)
        {
            var protoVal = funcConstructor.GetFunction().Get("prototype");
            funcProto = protoVal.Type == JsType.Object ? protoVal.GetObject() : new JsObject();
            funcConstructor.GetFunction().Set("prototype", JsValue.FromObject(funcProto));
        }
        else
        {
            funcProto = new JsObject();
            var funcCtor = new JsFunction((self, args) => JsValue.Undefined, _globalScope, "Function");
            funcCtor.Set("prototype", JsValue.FromObject(funcProto));
            _globalScope.Define("Function", JsValue.FromFunction(funcCtor));
        }

        funcProto.Set("call", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            if (self.Type != JsType.Function)
                throw new JsInterpreterException("Function.prototype.call called on non-function");
            var thisArg = args.Length > 0 ? args[0] : JsValue.Undefined;
            var callArgs = args.Skip(1).ToList();
            return ExecuteCallWithThis(self.GetFunction(), thisArg, callArgs);
        }, _globalScope, "call")));

        funcProto.Set("apply", JsValue.FromFunction(new JsFunction((self, args) =>
        {
            if (self.Type != JsType.Function)
                throw new JsInterpreterException("Function.prototype.apply called on non-function");
            var thisArg = args.Length > 0 ? args[0] : JsValue.Undefined;
            var callArgs = new List<JsValue>();
            if (args.Length > 1 && args[1].Type == JsType.Object)
            {
                var argsObj = args[1].GetObject();
                int len = argsObj.HasOwn("length") ? (int)argsObj.Get("length").GetNumber() : 0;
                for (int i = 0; i < len; i++)
                    callArgs.Add(argsObj.Get(i.ToString()));
            }
            return ExecuteCallWithThis(self.GetFunction(), thisArg, callArgs);
        }, _globalScope, "apply")));

        // Wire up interpreter-aware setTimeout/setInterval (string support) on window
        var windowVal = _globalScope.Get("window");
        if (windowVal.Type == JsType.Object)
        {
            var windowObj = windowVal.GetObject();

            windowObj.Set("setTimeout", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                if (args.Length == 0) return JsValue.From(0);
                int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                JsValue callback = args[0];
                if (callback.Type == JsType.String)
                {
                    string src = callback.ToJsString();
                    callback = JsValue.FromFunction(new JsFunction((s2, a2) =>
                    {
                        var prog = JsParser.Parse(src);
                        return Execute(prog);
                    }, _globalScope));
                }
                if (callback.Type == JsType.Function)
                {
                    var timer = new ScheduledTimer { Callback = callback, Interval = Math.Max(delay, 0), NextTick = Environment.TickCount64 + Math.Max(delay, 0), Repeat = false };
                    _timers.Add(timer);
                    return JsValue.From(_timers.Count);
                }
                return JsValue.From(0);
            }, _globalScope, "setTimeout")));

            windowObj.Set("setInterval", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                if (args.Length == 0) return JsValue.From(0);
                int delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                JsValue callback = args[0];
                if (callback.Type == JsType.String)
                {
                    string src = callback.ToJsString();
                    callback = JsValue.FromFunction(new JsFunction((s2, a2) =>
                    {
                        var prog = JsParser.Parse(src);
                        return Execute(prog);
                    }, _globalScope));
                }
                if (callback.Type == JsType.Function)
                {
                    var timer = new ScheduledTimer { Callback = callback, Interval = Math.Max(delay, 1), NextTick = Environment.TickCount64 + Math.Max(delay, 1), Repeat = true };
                    _timers.Add(timer);
                    return JsValue.From(_timers.Count);
                }
                return JsValue.From(0);
            }, _globalScope, "setInterval")));

            windowObj.Set("clearTimeout", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                if (args.Length > 0) { int id = (int)args[0].ToNumber() - 1; if (id >= 0 && id < _timers.Count) _timers[id] = null!; }
                return JsValue.Undefined;
            }, _globalScope, "clearTimeout")));

            windowObj.Set("clearInterval", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                if (args.Length > 0) { int id = (int)args[0].ToNumber() - 1; if (id >= 0 && id < _timers.Count) _timers[id] = null!; }
                return JsValue.Undefined;
            }, _globalScope, "clearInterval")));
        }

        // Expose timer functions directly on global scope (common usage pattern)
        var winObj2 = _globalScope.Get("window");
        if (winObj2.Type == JsType.Object)
        {
            foreach (var name in new[] { "setTimeout", "setInterval", "clearTimeout", "clearInterval" })
                _globalScope.Define(name, winObj2.GetObject().Get(name));
        }
    }

}