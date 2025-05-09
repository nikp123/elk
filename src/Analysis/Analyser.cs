using System;
using System.Collections.Generic;
using System.Linq;
using Elk.Exceptions;
using Elk.Std.Bindings;
using Elk.Lexing;
using Elk.Parsing;
using Elk.Scoping;
using Elk.Services;
using Elk.Std.DataTypes;

namespace Elk.Analysis;

class Analyser(RootModuleScope rootModule)
{
    private Scope _scope = rootModule;
    private Expr? _enclosingFunction;
    private Expr? _currentExpr;
    private List<SemanticToken>? _semanticTokens;

    public static IList<SemanticToken> GetSemanticTokens(Ast ast, ModuleScope module)
    {
        var analyser = new Analyser(module.RootModule)
        {
            _scope = module,
            _semanticTokens = [],
        };
        analyser.Start(ast, module, AnalysisScope.OverwriteExistingModule);

        return analyser._semanticTokens;
    }

    public static Ast Analyse(Ast ast, ModuleScope module, AnalysisScope analysisScope)
    {
        if (analysisScope == AnalysisScope.OncePerModule && module.AnalysisStatus != AnalysisStatus.None)
            return module.Ast;

        var analyser = new Analyser(module.RootModule)
        {
            _scope = module,
        };

        try
        {
            return analyser.Start(ast, module, analysisScope);
        }
        catch (RuntimeException ex)
        {
            ex.StartPosition ??= analyser._currentExpr?.StartPosition;
            ex.EndPosition ??= analyser._currentExpr?.EndPosition;

            throw;
        }
    }

    private Ast Start(Ast ast, ModuleScope module, AnalysisScope analysisScope)
    {
        module.AnalysisStatus = AnalysisStatus.Analysed;
        ResolveImports(module);

        try
        {
            var analysedExpressions = ast
                .Expressions
                .Select(expr => Next(expr))
                .ToList();
            var analysedAst = new Ast(analysedExpressions);

            if (analysisScope == AnalysisScope.OncePerModule)
                module.Ast = analysedAst;

            return analysedAst;
        }
        catch (RuntimeException ex)
        {
            ex.StartPosition = _currentExpr?.StartPosition;
            ex.EndPosition = _currentExpr?.EndPosition;
            throw;
        }
    }

    private static void ResolveImports(ModuleScope module)
    {
        foreach (var (importScope, token) in module.ImportedUnknowns)
        {
            var importedFunction = importScope.FindFunction(token.Value, lookInImports: false);
            if (importedFunction != null)
            {
                if (importedFunction.Expr.AccessLevel != AccessLevel.Public)
                {
                    throw new RuntimeException(
                        $"Cannot import private symbol '{importedFunction.Expr.Identifier.Value}'",
                        token.Position,
                        token.EndPosition
                    );
                }

                module.ImportFunction(importedFunction);
                continue;
            }

            var importedStruct = importScope.FindStruct(token.Value, lookInImports: false);
            if (importedStruct != null)
            {
                if (importedStruct.Expr?.AccessLevel is not (AccessLevel.Public or null))
                {
                    throw new RuntimeException(
                        $"Cannot import private symbol '{importedStruct.Expr?.Identifier.Value}'",
                        token.Position,
                        token.EndPosition
                    );
                }

                module.ImportStruct(importedStruct);
                continue;
            }

            var importedModule = importScope.FindModule([token], lookInImports: false);
            if (importedModule != null)
            {
                if (importedModule.AccessLevel != AccessLevel.Public)
                {
                    throw new RuntimeException(
                        $"Cannot import private symbol '{importedModule.Name}'",
                        token.Position,
                        token.EndPosition
                    );
                }

                module.ImportModule(token.Value, importedModule);
                continue;
            }

            if (importedModule == null)
            {
                throw new RuntimeException(
                    $"Module does not contain symbol '{token.Value}'",
                    token.Position,
                    token.EndPosition
                );
            }
        }

        module.ClearUnknowns();
    }

    private Expr Next(Expr expr)
    {
        expr.EnclosingFunction = _enclosingFunction;
        var previousExpr = _currentExpr;
        _currentExpr = expr;

        var analysedExpr = expr switch
        {
            ModuleExpr e => Visit(e),
            StructExpr e => Visit(e),
            FunctionExpr e => Visit(e),
            LetExpr e => Visit(e),
            NewExpr e => Visit(e),
            IfExpr e => Visit(e),
            ForExpr e => Visit(e),
            WhileExpr e => Visit(e),
            TupleExpr e => Visit(e),
            ListExpr e => Visit(e),
            SetExpr e => Visit(e),
            DictionaryExpr e => Visit(e),
            BlockExpr e => Visit(e),
            KeywordExpr e => Visit(e),
            BinaryExpr e => Visit(e),
            UnaryExpr e => Visit(e),
            FieldAccessExpr e => Visit(e),
            RangeExpr e => Visit(e),
            IndexerExpr e => Visit(e),
            TypeExpr e => Visit(e),
            VariableExpr e => Visit(e),
            CallExpr e => Visit(e),
            LiteralExpr e => Visit(e),
            StringInterpolationExpr e => Visit(e),
            ClosureExpr e => Visit(e),
            TryExpr e => Visit(e),
            _ => throw new NotSupportedException(),
        };

        analysedExpr.EnclosingFunction = expr.EnclosingFunction;
        _currentExpr = previousExpr;

        return analysedExpr;
    }

    private Expr NextCallOrClosure(Expr expr, Expr? pipedValue, bool hasClosure, bool validateParameters = true)
    {
        expr.EnclosingFunction = _enclosingFunction;
        var previousExpr = _currentExpr;
        _currentExpr = expr;

        var analysedExpr = expr switch
        {
            ClosureExpr closureExpr => Visit(closureExpr, pipedValue),
            CallExpr callExpr => Visit(callExpr, pipedValue, hasClosure, validateParameters),
            _ => throw new RuntimeException("Expected function call to the right of pipe."),
        };

        analysedExpr.EnclosingFunction = expr.EnclosingFunction;
        _currentExpr = previousExpr;

        return analysedExpr;
    }

    private ModuleExpr Visit(ModuleExpr expr)
    {
        var block = (BlockExpr)Next(expr.Body);
        block.IsRoot = true;

        return new ModuleExpr(expr.AccessLevel, expr.Identifier, block);
    }

    private StructExpr Visit(StructExpr expr)
    {
        var newStruct = new StructExpr(
            expr.AccessLevel,
            expr.Identifier,
            AnalyseParameters(expr.Parameters),
            expr.Module,
            expr.StartPosition,
            expr.EndPosition
        );

        var uniqueParameters = new HashSet<string>(newStruct.Parameters.Select(x => x.Identifier.Value));
        if (uniqueParameters.Count != newStruct.Parameters.Count)
            throw new RuntimeException("Duplicate field in struct");

        expr.Module.AddStruct(newStruct);

        return newStruct;
    }

    private FunctionExpr Visit(FunctionExpr expr)
    {
        if (expr.AnalysisStatus != AnalysisStatus.None)
            return expr;

        expr.EnclosingFunction = expr;
        var parameters = AnalyseParameters(expr.Parameters);
        foreach (var parameter in parameters)
        {
            expr.Block.Scope.AddVariable(parameter.Identifier.Value, RuntimeNil.Value);
        }

        var newFunction = new FunctionExpr(
            expr.AccessLevel,
            expr.Identifier,
            parameters,
            expr.Block,
            expr.Module,
            expr.HasClosure
        )
        {
            IsRoot = expr.IsRoot,
            AnalysisStatus = AnalysisStatus.Analysed,
            ClosureSymbol = expr.HasClosure
                ? new VariableSymbol("closure", RuntimeNil.Value)
                : null,
        };

        // Need to set _enclosingFunction *before* analysing the block
        // since it's used inside the block.
        _enclosingFunction = newFunction;

        try
        {
            var previousScope = _scope;
            _scope = expr.Module;
            newFunction.Block = (BlockExpr)Next(expr.Block);
            _scope = previousScope;
        }
        catch (RuntimeException)
        {
            expr.AnalysisStatus = AnalysisStatus.Failed;

            throw;
        }

        _enclosingFunction = null;
        expr.Module.AddFunction(newFunction);

        return newFunction;
    }

    private List<Parameter> AnalyseParameters(ICollection<Parameter> parameters)
    {
        var encounteredDefaultParameter = false;
        var newParameters = new List<Parameter>();
        foreach (var (parameter, i) in parameters.WithIndex())
        {
            if (parameter.DefaultValue == null)
            {
                if (encounteredDefaultParameter)
                    throw new RuntimeException("Optional parameters may only occur at the end of parameter lists");

                newParameters.Add(parameter);
            }
            else
            {
                var isLiteral = parameter.DefaultValue is
                    LiteralExpr or
                    ListExpr { Values.Count: 0 } or
                    DictionaryExpr { Entries.Count: 0 } or
                    StringInterpolationExpr { Parts: [LiteralExpr] };
                if (!isLiteral)
                    throw new RuntimeException("Expected literal or empty collection as default parameter");

                newParameters.Add(
                    parameter with { DefaultValue = Next(parameter.DefaultValue) }
                );
                encounteredDefaultParameter = true;
            }

            if (parameter.IsVariadic)
            {
                if (i != parameters.Count - 1)
                    throw new RuntimeException("Variadic parameters may only occur at the end of parameter lists");

                break;
            }
        }

        return newParameters;
    }

    private LetExpr Visit(LetExpr expr)
    {
        if (expr.IdentifierList.Count > 1 && expr.IdentifierList.Any(x => x.Value.StartsWith('$')))
            throw new RuntimeException("Cannot destructure into an environment variable");

        return new LetExpr(
            expr.IdentifierList,
            Next(expr.Value),
            _scope,
            expr.StartPosition
        );
    }

    private NewExpr Visit(NewExpr expr)
    {
        var module = _scope.ModuleScope.FindModule(expr.ModulePath, lookInImports: true);
        if (module == null)
        {
            var firstModule = expr.ModulePath.FirstOrDefault()?.Value;
            if (firstModule == null || !StdBindings.HasModule(firstModule))
                throw new RuntimeModuleNotFoundException(expr.ModulePath);

            var stdStruct = StdBindings.GetStruct(expr.Identifier.Value, firstModule);
            if (stdStruct == null)
                throw new RuntimeNotFoundException(expr.Identifier.Value);

            var argumentCount = expr.Arguments.Count;
            if (argumentCount < stdStruct.MinArgumentCount ||
                argumentCount > stdStruct.MaxArgumentCount)
            {
                throw new RuntimeWrongNumberOfArgumentsException(
                    stdStruct.Name,
                    stdStruct.MinArgumentCount,
                    argumentCount,
                    stdStruct.VariadicStart.HasValue
                );
            }

            return new NewExpr(
                expr.Identifier,
                expr.ModulePath,
                expr.Arguments.Select(Next).ToList(),
                _scope,
                expr.StartPosition,
                expr.EndPosition
            )
            {
                StructSymbol = new StructSymbol(stdStruct),
            };
        }

        var symbol = module.FindStruct(expr.Identifier.Value, lookInImports: true);
        if (symbol?.Expr == null)
            throw new RuntimeNotFoundException(expr.Identifier.Value);

        if (module != _scope.ModuleScope && symbol.Expr.AccessLevel != AccessLevel.Public)
            throw new RuntimeAccessLevelException(symbol.Expr.AccessLevel, expr.Identifier.Value);

        ValidateArguments(
            expr.Identifier.Value,
            expr.Arguments,
            symbol.Expr.Parameters,
            isReference: false
        );

        return new NewExpr(
            expr.Identifier,
            expr.ModulePath,
            expr.Arguments.Select(Next).ToList(),
            _scope,
            expr.StartPosition,
            expr.EndPosition
        )
        {
            StructSymbol = symbol,
        };
    }

    private IfExpr Visit(IfExpr expr)
    {
        expr.ThenBranch.IsRoot = expr.IsRoot;

        var ifCondition = Next(expr.Condition);
        var thenBranch = Next(expr.ThenBranch);

        Expr? elseBranch = null;
        if (expr.ElseBranch != null)
        {
            expr.ElseBranch.IsRoot = expr.IsRoot;
            elseBranch = Next(expr.ElseBranch);
        }

        return new IfExpr(ifCondition, thenBranch, elseBranch, _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private ForExpr Visit(ForExpr expr)
    {
        var forValue = Next(expr.Value);
        foreach (var identifier in expr.IdentifierList)
            expr.Branch.Scope.AddVariable(identifier.Value, RuntimeNil.Value);

        expr.Branch.IsRoot = true;
        var branch = (BlockExpr)Next(expr.Branch);

        return new ForExpr(expr.IdentifierList, forValue, branch, _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private WhileExpr Visit(WhileExpr expr)
    {
        expr.Branch.IsRoot = true;

        var whileCondition = Next(expr.Condition);
        var whileBranch = (BlockExpr)Next(expr.Branch);

        return new WhileExpr(whileCondition, whileBranch, _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private TupleExpr Visit(TupleExpr expr)
    {
        var tupleValues = expr.Values.Select(Next).ToList();

        return new TupleExpr(tupleValues, _scope, expr.StartPosition, expr.EndPosition)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private ListExpr Visit(ListExpr expr)
    {
        var listValues = expr.Values.Select(Next).ToList();

        return new ListExpr(listValues, _scope, expr.StartPosition, expr.EndPosition)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private SetExpr Visit(SetExpr expr)
    {
        foreach (var value in expr.Entries)
            Next(value);

        return new SetExpr(expr.Entries.Select(Next).ToList(), _scope, expr.StartPosition, expr.EndPosition)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private DictionaryExpr Visit(DictionaryExpr expr)
    {
        var dictEntries = expr.Entries
            .Select(x => (Next(x.Item1), Next(x.Item2)))
            .ToList();

        return new DictionaryExpr(dictEntries, _scope, expr.StartPosition, expr.EndPosition)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private Expr Visit(BlockExpr expr)
    {
        _scope = expr.Scope;
        var blockExpressions = new List<Expr>();
        foreach (var analysed in expr.Expressions.Select(Next))
        {
            if (expr.IsRoot)
                analysed.IsRoot = true;

            blockExpressions.Add(analysed);
        }

        var newExpr = new BlockExpr(
            blockExpressions,
            expr.ParentStructureKind,
            expr.Scope,
            expr.StartPosition,
            expr.EndPosition
        )
        {
            IsRoot = expr.IsRoot,
        };
        _scope = _scope.Parent!;

        return newExpr;
    }

    private KeywordExpr Visit(KeywordExpr expr)
    {
        Expr? keywordValue = null;
        if (expr.Value != null)
            keywordValue = Next(expr.Value);

        return new KeywordExpr(expr.Keyword, keywordValue, _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private Expr Visit(BinaryExpr expr)
    {
        if (expr.Operator == OperationKind.Equals)
            return AnalyseAssignment(expr);

        if (expr.Operator is OperationKind.NonRedirectingAnd or OperationKind.NonRedirectingOr)
        {
            expr.Left.IsRoot = true;

            // Necessary for nested non-redirecting and/or expressions
            if (expr.Left is BinaryExpr
                    { Operator: OperationKind.NonRedirectingAnd or OperationKind.NonRedirectingOr } binaryRight)
            {
                binaryRight.Right.IsRoot = true;
            }
        }


        var left = Next(expr.Left);
        if (expr.Operator is OperationKind.Pipe or OperationKind.PipeErr or OperationKind.PipeAll)
        {
            expr.Right.IsRoot = expr.IsRoot;

            var isProgramCall = left is CallExpr { CallType: CallType.Program or CallType.Unknown };
            if (!isProgramCall && expr.Operator is OperationKind.PipeErr or OperationKind.PipeAll)
            {
                var pipeString = expr.Operator == OperationKind.PipeErr ? "|err" : "|all";

                throw new RuntimeInvalidOperationException(pipeString, "non-program");
            }

            if (isProgramCall)
            {
                var leftCall = (CallExpr)left;
                leftCall.RedirectionKind = expr.Operator switch
                {
                    OperationKind.PipeErr => RedirectionKind.Error,
                    OperationKind.PipeAll => RedirectionKind.All,
                    _ => RedirectionKind.Output,
                };
            }

            return NextCallOrClosure(expr.Right, left, hasClosure: false);
        }

        return new BinaryExpr(left, expr.Operator, Next(expr.Right), _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private Expr AnalyseAssignment(BinaryExpr expr)
    {
        if (expr.Left is VariableExpr variableExpr)
        {
            AnalyseVariable(variableExpr);
        }
        else if (expr.Left is not (IndexerExpr or FieldAccessExpr))
        {
            var message = expr.Left is CallExpr
                ? "Invalid assignment. The left expression was parsed as a call, but a variable was expected"
                : "Invalid assignment";

            throw new RuntimeException(message);
        }

        return new BinaryExpr(Next(expr.Left), expr.Operator, Next(expr.Right), _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private UnaryExpr Visit(UnaryExpr expr)
    {
        return new UnaryExpr(expr.Operator, Next(expr.Value), _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private FieldAccessExpr Visit(FieldAccessExpr expr)
    {
        return new FieldAccessExpr(Next(expr.Object), expr.Identifier, _scope)
        {
            IsRoot = expr.IsRoot,
            RuntimeIdentifier = new RuntimeString(expr.Identifier.Value),
        };
    }

    private RangeExpr Visit(RangeExpr expr)
    {
        var from = expr.From == null
            ? null
            : Next(expr.From);
        var to = expr.To == null
            ? null
            : Next(expr.To);

        return new RangeExpr(from, to, expr.Inclusive, _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private IndexerExpr Visit(IndexerExpr expr)
    {
        return new IndexerExpr(Next(expr.Value), Next(expr.Index), _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private TypeExpr Visit(TypeExpr expr)
    {
        var stdType = StdBindings.GetRuntimeType(expr.Identifier.Value);
        var userType = _scope.ModuleScope.FindStruct(expr.Identifier.Value, lookInImports: true);
        if (stdType == null && userType == null)
            throw new RuntimeNotFoundException(expr.Identifier.Value);

        return new TypeExpr(expr.Identifier, _scope)
        {
            RuntimeValue = stdType != null
                ? new RuntimeType(stdType)
                : new RuntimeType(userType!),
            IsRoot = expr.IsRoot,
        };
    }

    private VariableExpr Visit(VariableExpr expr)
    {
        return AnalyseVariable(expr);
    }

    private VariableExpr AnalyseVariable(VariableExpr expr)
    {
        var variableExpr = new VariableExpr(expr.Identifier, _scope)
        {
            IsRoot = expr.IsRoot,
        };

        if (!expr.Identifier.Value.StartsWith('$') && !_scope.HasVariable(expr.Identifier.Value))
                throw new RuntimeNotFoundException(expr.Identifier.Value);

        if (expr.EnclosingFunction is not ClosureExpr closure)
            return variableExpr;

        var capturedVariable = closure.Body.Scope.Parent?.FindVariable(expr.Identifier.Value);
        if (capturedVariable != null && !IsVariableDeclaredInClosure(expr.Identifier.Value, _scope, closure.Body.Scope))
        {
            closure.CapturedVariables.Add(expr.Identifier.Value);
            capturedVariable.IsCaptured = true;
            variableExpr.IsCaptured = true;
        }

        return variableExpr;
    }

    private bool IsVariableDeclaredInClosure(string name, Scope startScope, Scope closureScope)
    {
        if (startScope.HasDeclarationOfVariable(name))
            return true;

        var currentScope = startScope;
        while (currentScope != closureScope && currentScope != null)
        {
            if (currentScope.HasDeclarationOfVariable(name))
                return true;

            currentScope = currentScope.Parent;
        }

        return false;
    }

    private CallExpr Visit(
        CallExpr expr,
        Expr? pipedValue = null,
        bool hasClosure = false,
        bool validateParameters = true)
    {
        var name = expr.Identifier.Value;
        CallType? builtIn = name switch
        {
            "exec" => CallType.BuiltInExec,
            "closure" => CallType.BuiltInClosure,
            "call" => CallType.BuiltInCall,
            "source" => CallType.BuiltInSource,
            _ => null,
        };

        FunctionExpr? enclosingClosureProvidingFunction = null;
        if (builtIn == CallType.BuiltInClosure)
        {
            var enclosing = expr.EnclosingFunction;
            while (enclosing is ClosureExpr enclosingClosure)
                enclosing = enclosingClosure.Function.EnclosingFunction;

            if (enclosing is not FunctionExpr { HasClosure: true } enclosingFunction)
            {
                throw new RuntimeException(
                    "Unexpected call to 'closure'. This function can only be called within functions with a closure signature."
                );
            }

            enclosingClosureProvidingFunction = enclosingFunction;
            if (expr.EnclosingFunction is ClosureExpr nearestEnclosingClosure &&
                enclosingClosureProvidingFunction.ClosureSymbol != null)
            {
                enclosingClosureProvidingFunction.ClosureSymbol.IsCaptured = true;
                nearestEnclosingClosure.CapturedVariables.Add(enclosingClosureProvidingFunction.ClosureSymbol.Name);
            }
        }

        var stdFunction = !builtIn.HasValue
            ? ResolveStdFunction(name, expr.ModulePath)
            : null;
        var functionSymbol = stdFunction == null
            ? ResolveFunctionSymbol(name, expr.ModulePath)
            : null;
        var callType = builtIn switch
        {
            not null => builtIn.Value,
            _ => (stdFunction, functionSymbol) switch
            {
                (not null, null) => CallType.StdFunction,
                (null, not null) => CallType.Function,
                (null, null) => CallType.Program,
                _ => CallType.Function,
            },
        };

        var evaluatedArguments = expr.Arguments.Select(Next).ToList();
        if (pipedValue != null && callType != CallType.Program)
            evaluatedArguments.Insert(0, pipedValue);

        var definitionHasClosure = functionSymbol?.Expr.HasClosure is true || stdFunction?.HasClosure is true;
        if (!definitionHasClosure && hasClosure)
        {
            var additionalInfo = callType == CallType.Program
                ? " The call was evaluated as a program invocation since a function with this name could not be found."
                : "";

            throw new RuntimeException($"Unexpected closure.{additionalInfo}");
        }

        if (definitionHasClosure && !hasClosure)
            throw new RuntimeException("Expected closure.");

        var argumentCount = evaluatedArguments.Count;
        if (stdFunction != null && validateParameters)
        {
            var hasEnoughArguments = expr.IsReference || argumentCount >= stdFunction.MinArgumentCount;
            if (!hasEnoughArguments || argumentCount > stdFunction.MaxArgumentCount)
            {
                throw new RuntimeWrongNumberOfArgumentsException(
                    stdFunction.Name,
                    stdFunction.MinArgumentCount,
                    argumentCount,
                    stdFunction.VariadicStart.HasValue
                );
            }

            if (stdFunction.HasClosure && !hasClosure)
                throw new RuntimeException("Expected closure.");
        }

        if (functionSymbol != null && validateParameters)
        {
            if (hasClosure && !functionSymbol.Expr.HasClosure)
                throw new RuntimeException("Expected closure.");

            ValidateArguments(
                functionSymbol.Expr.Identifier.Value,
                evaluatedArguments,
                functionSymbol.Expr.Parameters,
                expr.IsReference
            );
        }

        if (pipedValue is CallExpr { CallType: CallType.Program } pipedCall)
        {
            // Don't buffer stdout/stderr if it's piped straight to a program's stdin
            // or piped to an std function that expects a Pipe. Std functions that
            // explicitly expect Pipes will handle them properly and not pass them
            // around more.
            pipedCall.DisableRedirectionBuffering = callType == CallType.Program ||
                stdFunction?.ConsumesPipe is true;
        }

        if (stdFunction?.StartsPipeManually is true)
        {
            foreach (var argument in evaluatedArguments)
            {
                if (argument is CallExpr call)
                    call.AutomaticStart = false;
            }
        }

        var environmentVariables = new Dictionary<string, Expr>();
        foreach (var (key, value) in expr.EnvironmentVariables)
        {
            environmentVariables.Add(key, Next(value));
        }

        if (expr.Identifier.Value == "bat")
        {
        }

        return new CallExpr(
            expr.Identifier,
            expr.ModulePath,
            evaluatedArguments,
            expr.CallStyle,
            callType,
            _scope,
            expr.EndPosition
        )
        {
            IsRoot = expr.IsRoot,
            StdFunction = stdFunction,
            FunctionSymbol = functionSymbol,
            PipedToProgram = callType == CallType.Program
                ? pipedValue
                : null,
            RedirectionKind = expr.RedirectionKind,
            DisableRedirectionBuffering = expr.DisableRedirectionBuffering,
            IsReference = expr.IsReference,
            EnvironmentVariables = environmentVariables,
            EnclosingClosureProvidingFunction = enclosingClosureProvidingFunction,
        };
    }

    private void ValidateArguments(string name, IList<Expr> arguments, IList<Parameter> parameters, bool isReference)
    {
        var argumentCount = arguments.Count;
        var isVariadic = parameters.LastOrDefault()?.IsVariadic is true;
        var tooManyArguments = argumentCount > parameters.Count && !isVariadic;
        var tooFewArguments = !isReference && !isVariadic && parameters.Count > argumentCount &&
            parameters[argumentCount].DefaultValue == null;

        if (tooManyArguments || tooFewArguments)
            throw new RuntimeWrongNumberOfArgumentsException(name, parameters.Count, argumentCount, isVariadic);
    }

    private Expr Visit(LiteralExpr expr)
    {
        if (expr.Value.Kind == TokenKind.BashLiteral)
        {
            // Everything after `$:`
            var bashContent = expr.Value.Value[2..];
            var arguments = new List<Expr>
            {
                new LiteralExpr(
                    new Token(TokenKind.SingleQuoteStringLiteral, "-c", expr.Value.Position),
                    expr.Scope
                )
                {
                    RuntimeValue = new RuntimeString("-c"),
                },
                new LiteralExpr(
                    new Token(TokenKind.SingleQuoteStringLiteral, bashContent, expr.Value.Position),
                    expr.Scope
                )
                {
                    RuntimeValue = new RuntimeString(bashContent),
                },
            };

            return new CallExpr(
                new Token(TokenKind.Identifier, "bash", expr.Value.Position),
                Array.Empty<Token>(),
                arguments,
                CallStyle.Parenthesized,
                CallType.Program,
                expr.Scope,
                expr.EndPosition
            );
        }

        RuntimeObject value = expr.Value.Kind switch
        {
            TokenKind.IntegerLiteral => new RuntimeInteger(ParseInt(expr.Value.Value)),
            TokenKind.FloatLiteral => double.TryParse(expr.Value.Value, out var parsed)
                ? new RuntimeFloat(parsed)
                : throw new RuntimeException("Invalid number literal"),
            TokenKind.DoubleQuoteStringLiteral or TokenKind.SingleQuoteStringLiteral => new RuntimeString(expr.Value.Value),
            TokenKind.TextArgumentStringLiteral => new RuntimeString(expr.Value.Value)
            {
                IsTextArgument = true,
            },
            TokenKind.True => RuntimeBoolean.True,
            TokenKind.False => RuntimeBoolean.False,
            _ => RuntimeNil.Value,
        };

        return new LiteralExpr(expr.Value, _scope)
        {
            RuntimeValue = value,
            IsRoot = expr.IsRoot,
        };
    }

    private static long ParseInt(string numberLiteral)
    {
        try
        {
            if (numberLiteral.StartsWith("0x"))
                return Convert.ToInt32(numberLiteral[2..], 16);
            if (numberLiteral.StartsWith("0o"))
                return Convert.ToInt32(numberLiteral[2..], 8);
            if (numberLiteral.StartsWith("0b"))
                return Convert.ToInt32(numberLiteral[2..], 2);

            return long.Parse(numberLiteral);
        }
        catch
        {
            throw new RuntimeException("Invalid number literal");
        }
    }

    private StringInterpolationExpr Visit(StringInterpolationExpr expr)
    {
        var parts = expr.Parts.Select(Next).ToList();

        return new StringInterpolationExpr(parts, expr.StartPosition, _scope)
        {
            IsRoot = expr.IsRoot,
        };
    }

    private Expr Visit(ClosureExpr expr, Expr? pipedValue = null)
    {
        expr.EnclosingFunction = expr;

        var function = (CallExpr)NextCallOrClosure(expr.Function, pipedValue, hasClosure: true);
        var closure = new ClosureExpr(function, expr.Parameters, expr.Body, _scope)
        {
            IsRoot = expr.IsRoot,
            CapturedVariables = expr.CapturedVariables,
        };

        var previousEnclosingFunction = _enclosingFunction;
        _enclosingFunction = closure;
        closure.Body = (BlockExpr)Next(expr.Body);
        _enclosingFunction = previousEnclosingFunction;
        expr.EnclosingFunction = previousEnclosingFunction;

        // If closure inside a closure captures a variable that is outside its parent,
        // the parent needs to capture it as well, in order to pass it on to the child.
        if (_enclosingFunction is ClosureExpr enclosingClosure)
        {
            var captures = expr.CapturedVariables
                .Where(x => enclosingClosure.Body.Scope.HasVariable(x))
                .Where(x => !enclosingClosure.Body.Scope.HasDeclarationOfVariable(x))
                .Where(x => !expr.Body.Scope.HasDeclarationOfVariable(x))
                .Where(x => enclosingClosure.Parameters.All(param => param.Value != x));
            foreach (var captured in captures)
                enclosingClosure.CapturedVariables.Add(captured);
        }

        if (closure.Body.Expressions.Count != 1 ||
            closure.Body.Expressions.First() is not CallExpr { IsReference: true } functionReference)
        {
            return closure;
        }

        var closureParameters = function switch
        {
            not { StdFunction: null } => Enumerable
                .Repeat(functionReference.Identifier, function.StdFunction.ClosureParameterCount!.Value)
                .Select((x, i) => x with { Value = "'" + i }),
            not { FunctionSymbol: null } => function.FunctionSymbol.Expr.Parameters
                .Select(x => x.Identifier with { Value = "'" + x.Identifier.Value }),
            _ => new List<Token> { functionReference.Identifier with { Value = "'a" } },
        };
        var implicitArguments = closureParameters
            .Select(x => new VariableExpr(x, expr.Body.Scope)
            {
                EnclosingFunction = closure,
            });
        functionReference.Arguments = implicitArguments
            .Concat(functionReference.Arguments)
            .ToList();
        functionReference.IsReference = false;

        foreach (var parameter in closureParameters)
            closure.Body.Scope.AddVariable(parameter.Value, RuntimeNil.Value);

        closure.Parameters.Clear();
        closure.Parameters.AddRange(closureParameters);
        closure.Body.Expressions.Clear();
        closure.Body.Expressions.Add(functionReference);

        return closure;
    }

    private TryExpr Visit(TryExpr expr)
    {
        expr.Body.IsRoot = expr.IsRoot;

        var tryBranch = (BlockExpr)Next(expr.Body);
        var catchExpressions = new List<CatchExpr>();
        foreach (var catchExpression in expr.CatchExpressions)
        {
            catchExpression.Body.IsRoot = expr.IsRoot;

            var type = catchExpression.Type == null
                ? null
                : (TypeExpr)Next(catchExpression.Type);
            var body = (BlockExpr)Next(catchExpression.Body);

            var newExpr = new CatchExpr(
                catchExpression.Identifier,
                type,
                body,
                catchExpression.Scope
            );

            catchExpressions.Add(newExpr);
        }

        return new TryExpr(
            tryBranch,
            catchExpressions.ToList(),
            _scope
        )
        {
            IsRoot = expr.IsRoot,
        };
    }

    private StdFunction? ResolveStdFunction(string name, IList<Token> modulePath)
    {
        var module = modulePath.Select(x => x.Value);
        var function = StdBindings.GetFunction(name, module);
        if (function == null && StdBindings.HasModule(module))
            throw new RuntimeNotFoundException(name);

        return function;
    }

    private FunctionSymbol? ResolveFunctionSymbol(string name, IList<Token> modulePath)
    {
        var module = _scope.ModuleScope.FindModule(modulePath, lookInImports: true);
        if (module == null)
            throw new RuntimeModuleNotFoundException(modulePath);

        var symbol = module.FindFunction(name, lookInImports: true);
        if (symbol == null)
            return null;

        if (module != _scope.ModuleScope && symbol.Expr.AccessLevel != AccessLevel.Public)
            throw new RuntimeAccessLevelException(symbol.Expr.AccessLevel, name);

        return symbol;
    }
}
