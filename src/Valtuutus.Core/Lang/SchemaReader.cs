using Antlr4.Runtime;
using System.Linq.Expressions;
using System.Text;
using Valtuutus.Core.Schemas;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang;

public class SchemaReader
{
    private static readonly Dictionary<string, Type> _types = new()
    {
        { "string", typeof(string) },
        { "int", typeof(int) },
        { "bool", typeof(bool) },
        { "decimal", typeof(decimal) }
    };

    private readonly List<LangError> _errors = new();
    private readonly List<SchemaSymbol> _symbols = new();

    private SchemaSymbol? FindEntity(string entityName)
    {
        return _symbols.FirstOrDefault(s => s.Name == entityName && s.Type == SymbolType.Entity);
    }

    private FunctionSymbol? FindFunction(string functionName)
    {
        return _symbols
            .OfType<FunctionSymbol>()
            .FirstOrDefault(s => s.Name == functionName && s.Type == SymbolType.Function);
    }

    private RelationSymbol? FindEntityRelation(string entityName, string relationName)
    {
        return _symbols.OfType<RelationSymbol>()
            .FirstOrDefault(s => s.EntityName == entityName &&
                                 s.Name == relationName
                                 && s.Type == SymbolType.Relation);
    }

    private AttributeSymbol? FindEntityAttribute(string entityName, string attrName)
    {
        return _symbols.OfType<AttributeSymbol>()
            .FirstOrDefault(s => s.EntityName == entityName &&
                                 s.Name == attrName
                                 && s.Type == SymbolType.Attribute);
    }
    
    private PermissionSymbol? FindEntityPermission(string entityName, string permission)
    {
        return _symbols.OfType<PermissionSymbol>()
            .FirstOrDefault(s => s.EntityName == entityName &&
                                 s.Name == permission
                                 && s.Type == SymbolType.Attribute);
    }

    public OneOf<Schema, List<LangError>> Parse(string schema)
    {
        var schemaBuilder = new SchemaBuilder();

        var str = new AntlrInputStream(schema);
        var lexer = new ValtuutusLexer(str);
        var errorListener = new ParserSchemaErrorListener();
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errorListener);
        var tokens = new CommonTokenStream(lexer);
        var parser = new ValtuutusParser(tokens);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errorListener);
        var tree = parser.schema();

        if (errorListener.HasErrors)
        {
            return errorListener.Errors;
        }

        // Parse functions
        foreach (var funcs in tree.functionDefinition())
        {
            try
            {
                var functionNode = ParseFunctionDefinition(funcs);
                schemaBuilder.WithFunction(functionNode);
            }
            catch (LangException ex)
            {
                _errors.Add(ex.ToLangError());
            }
        }

        // Parse entities
        foreach (var entityCtx in tree.entityDefinition())
        {
            var entityName = entityCtx.ID().GetText();

            var existingEntity = FindEntity(entityName);
            if (existingEntity is not null)
            {
                _errors.Add(new LangError()
                {
                    Line = entityCtx.Start.Line,
                    Message =
                        $"Entity '{entityName}' already declared in line {existingEntity.DeclarationLine}:{existingEntity.StartPosition}.",
                    StartPos = existingEntity.StartPosition,
                });
            }

            _symbols.Add(new SchemaSymbol(SymbolType.Entity, entityName, entityCtx.Start.Line,
                entityCtx.ID().Symbol.Column));

            var entityBuilder = schemaBuilder.WithEntity(entityName);

            var relationCtx = entityCtx.entityBody().relationDefinition()!;

            foreach (var relation in relationCtx)
            {
                var relationName = relation.ID().GetText();
                var refRelations = new List<RelationReference>();

                if (FindEntityRelation(entityName, relationName) is not null)
                {
                    _errors.Add(new LangError()
                    {
                        Line = relation.Start.Line,
                        StartPos = relation.Start.Column,
                        Message = $"Entity '{entityName}' '{relationName}' relation already been defined.",
                    });

                    continue;
                }

                entityBuilder.WithRelation(relationName, relationBuilder =>
                {
                    foreach (var member in relation.relationMember())
                    {
                        var subjectRelation = member.POUND() != null;
                        var refEntityName = member.ID(0).GetText();

                        if (FindEntity(refEntityName) is null)
                        {
                            _errors.Add(new LangError()
                            {
                                Line = relation.Start.Line,
                                StartPos = relation.Start.Column,
                                Message = $"Entity '{refEntityName}' is not defined.",
                            });
                        }

                        if (!subjectRelation)
                        {
                            relationBuilder.WithEntityType(refEntityName);
                            refRelations.Add(new RelationReference()
                            {
                                ReferencedEntityName = refEntityName, ReferencedEntityRelation = null
                            });
                        }
                        else
                        {
                            var refEntityRelation = member.ID(1).GetText();

                            if (FindEntityRelation(refEntityName, refEntityRelation) is null)
                            {
                                _errors.Add(new LangError()
                                {
                                    Line = relation.Start.Line,
                                    StartPos = relation.Start.Column,
                                    Message =
                                        $"Entity '{refEntityName}' '{refEntityRelation}' relation is not defined.",
                                });
                            }

                            relationBuilder.WithEntityType(refEntityName, refEntityRelation);
                            refRelations.Add(new RelationReference()
                            {
                                ReferencedEntityName = refEntityName, ReferencedEntityRelation = refEntityRelation
                            });
                        }
                    }
                });

                _symbols.Add(new RelationSymbol(relationName, relation.Start.Line, relation.Start.Column, entityName,
                    refRelations));
            }

            var attributeCtx = entityCtx.entityBody().attributeDefinition()!;

            foreach (var attribute in attributeCtx)
            {
                var attr = new AttributeSymbol(attribute.ID().GetText(), attribute.Start.Line, attribute.Start.Column,
                    entityName, _types[attribute.type().GetText()]);

                if (FindEntityAttribute(entityName, attr.Name) is not null)
                {
                    _errors.Add(new LangError()
                    {
                        Line = attribute.Start.Line,
                        StartPos = attribute.Start.Column,
                        Message = $"Entity '{entityName}' '{attr.Name}' attribute already been defined.",
                    });

                    continue;
                }

                _symbols.Add(attr);

                entityBuilder.WithAttribute(attr.Name, attr.AttributeType);
            }

            var permissionCtx = entityCtx.entityBody().permissionDefinition()!;

            foreach (var permission in permissionCtx)
            {
                // Convert permission expression to PermissionNode
                try
                {
                    var permissionTree = BuildPermissionNode(entityName, permission.permissionExpression());
                    _symbols.Add(new PermissionSymbol(permission.ID().GetText(), permission.Start.Line,
                        permission.Start.Column, entityName));
                    entityBuilder.WithPermission(permission.ID().GetText(), permissionTree);
                }
                catch (LangException ex)
                {
                    _errors.Add(ex.ToLangError());
                }
            }
        }

        if (_errors.Count != 0)
        {
            return _errors;
        }

        return schemaBuilder.Build();
    }


    private Function ParseFunctionDefinition(ValtuutusParser.FunctionDefinitionContext funcCtx)
    {
        var functionName = funcCtx.ID().GetText();

        if (FindFunction(functionName) is not null)
        {
            _errors.Add(new LangError()
            {
                Line = funcCtx.Start.Line,
                StartPos = funcCtx.Start.Column,
                Message = $"Function with name '{functionName}' already defined.",
            });
        }

        var parameters = new List<FunctionParameter>();

        var ids = funcCtx.parameterList().ID();
        for (int i = 0; i < ids.Length; i++)
        {
            var param = ids[i];
            var paramName = param.GetText();
            var paramType = _types[funcCtx.parameterList().type()[i].GetText()];
            parameters.Add(new FunctionParameter() { ParamName = paramName, ParamOrder = i, ParamType = paramType });
        }

        var tree = ParseFunctionExpression(
            parameters.ToDictionary(x => x.ParamName, x => x.ParamType),
            funcCtx.functionBody().functionExpression()
        );

        var parameter = Expression.Parameter(typeof(IDictionary<string, object?>), "args");
        var expression = tree.GetExpression(parameter);

        _symbols.Add(new FunctionSymbol(functionName, funcCtx.Start.Line, funcCtx.Start.Column, parameters));

        return new Function(functionName, parameters, expression.Compile());
    }

    private FunctionNode<bool> ParseFunctionExpression(IDictionary<string, Type> args,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        return exprCtx switch
        {
            ValtuutusParser.AndExpressionContext andCtx => CreateAndExpressionNode(args, andCtx),
            ValtuutusParser.OrExpressionContext orCtx => CreateOrExpressionNode(args, orCtx),
            ValtuutusParser.EqualityExpressionContext eqCtx => CreateEqualExpressionNode(args, eqCtx),
            ValtuutusParser.InequalityExpressionContext neqCtx => CreateNotEqualExpressionNode(args, neqCtx),
            ValtuutusParser.GreaterExpressionContext gtCtx => CreateGreaterExpressionNode(args, gtCtx),
            ValtuutusParser.LessExpressionContext ltCtx => CreateLessExpressionNode(args, ltCtx),
            ValtuutusParser.GreaterOrEqualExpressionContext gteCtx =>
                CreateGreaterOrEqualExpressionNode(args, gteCtx),
            ValtuutusParser.LessOrEqualExpressionContext lteCtx =>
                CreateLessOrEqualExpressionNode(args, lteCtx),
            ValtuutusParser.ParenthesisExpressionContext parenCtx => CreateParenthesisExpressionNode(args, parenCtx),
            ValtuutusParser.NotExpressionContext notCtx => CreateNotExpressionNode(args, notCtx),
            ValtuutusParser.IdentifierExpressionContext => TryHandleBooleanIdOrParamNode(args, exprCtx),
            ValtuutusParser.LiteralExpressionContext => TryHandleBooleanIdOrParamNode(args, exprCtx),
            _ => throw new LangException("Unsupported function expression type", exprCtx.Start.Line,
                exprCtx.Start.Column)
        };
    }

    private FunctionNode<LiteralValueUnion> ParseLiteralExpression(IDictionary<string, Type> args,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        return exprCtx switch
        {
            ValtuutusParser.IdentifierExpressionContext idCtx => CreateParameterIdFnNode(args, idCtx),
            ValtuutusParser.LiteralExpressionContext litCtx => ParseLiteralFnNode(litCtx.literal()),
            _ => throw new LangException("Unsupported function expression type. Expected parameter or literal.",
                exprCtx.Start.Line, exprCtx.Start.Column)
        };
    }

    private FunctionNode<bool> CreateAndExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.AndExpressionContext andCtx)
    {
        return new AndExpressionFnNode
        {
            Left = ParseFunctionExpression(args, andCtx.functionExpression(0)),
            Right = ParseFunctionExpression(args, andCtx.functionExpression(1))
        };
    }

    private FunctionNode<bool> CreateOrExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.OrExpressionContext orCtx)
    {
        return new OrExpressionFnNode
        {
            Left = ParseFunctionExpression(args, orCtx.functionExpression(0)),
            Right = ParseFunctionExpression(args, orCtx.functionExpression(1))
        };
    }

    private FunctionNode<bool> CreateEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.EqualityExpressionContext eqCtx
    )
    {
        var node = new EqualExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, eqCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, eqCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {leftChild.TypeContext} and {rightChild.TypeContext}", eqCtx.Start.Line,
                eqCtx.Start.Column);
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private FunctionNode<bool> CreateNotEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.InequalityExpressionContext neqCtx
    )
    {
        var node = new NotEqualExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, neqCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, neqCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {leftChild.TypeContext} and {rightChild.TypeContext}",
                neqCtx.Start.Line, neqCtx.Start.Column);
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private FunctionNode<bool> CreateGreaterExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.GreaterExpressionContext gtCtx
    )
    {
        var node = new GreaterExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, gtCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, gtCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {leftChild.TypeContext} and {rightChild.TypeContext}",
                gtCtx.Start.Line, gtCtx.Start.Column);
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private FunctionNode<bool> CreateLessExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.LessExpressionContext ltCtx
    )
    {
        var node = new LessExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, ltCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, ltCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {leftChild.TypeContext} and {rightChild.TypeContext}",
                ltCtx.Start.Line, ltCtx.Start.Column);
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private FunctionNode<bool> CreateGreaterOrEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.GreaterOrEqualExpressionContext gteCtx
    )
    {
        var node = new GreaterOrEqualExpressionFnNode();

        var leftChild = ParseLiteralExpression(args, gteCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, gteCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {leftChild.TypeContext} and {rightChild.TypeContext}",
                gteCtx.Start.Line, gteCtx.Start.Column);
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private FunctionNode<bool> CreateLessOrEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.LessOrEqualExpressionContext lteCtx
    )
    {
        var node = new LessOrEqualExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, lteCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, lteCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {leftChild.TypeContext} and {rightChild.TypeContext}",
                lteCtx.Start.Line, lteCtx.Start.Column);
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private FunctionNode<bool> CreateParenthesisExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.ParenthesisExpressionContext parenCtx)
    {
        var node = new ParenthesisExpressionFnNode
        {
            Child = ParseFunctionExpression(args, parenCtx.functionExpression())
        };

        return node;
    }

    private FunctionNode<bool> CreateNotExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.NotExpressionContext notCtx)
    {
        var node = new NotExpressionFnNode { Child = ParseFunctionExpression(args, notCtx.functionExpression()) };

        return node;
    }

    private FunctionNode<bool> TryHandleBooleanIdOrParamNode(IDictionary<string, Type> args,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        var handleLiteralNode = (ValtuutusParser.LiteralExpressionContext litCtx) =>
        {
            if (litCtx.literal().BOOLEAN_LITERAL() != null)
            {
                return new EqualExpressionFnNode()
                {
                    Left = ParseLiteralExpression(args, litCtx), Right = new BooleanLiteralFnNode() { Value = true }
                };
            }

            throw new LangException(
                $"Expected boolean literal, got {litCtx.literal().GetText()}",
                litCtx.Start.Line, litCtx.Start.Column);
        };

        var handleParamNode = (ValtuutusParser.IdentifierExpressionContext idCtx) =>
        {
            var id = idCtx.ID().GetText();
            if (args.TryGetValue(id, out Type? idType) && idType == typeof(bool))
            {
                return new EqualExpressionFnNode()
                {
                    Left = CreateParameterIdFnNode(args, idCtx),
                    Right = new BooleanLiteralFnNode() { Value = true },
                };
            }

            throw new LangException(
                $"Expected boolean parameter, got {idType}",
                idCtx.Start.Line, idCtx.Start.Column);
        };

        var childNode = exprCtx switch
        {
            ValtuutusParser.IdentifierExpressionContext idCtx => handleParamNode(idCtx),
            ValtuutusParser.LiteralExpressionContext litCtx => handleLiteralNode(litCtx),
            _ => throw new InvalidOperationException()
        };

        return childNode;
    }

    private FunctionNode<LiteralValueUnion> CreateParameterIdFnNode(
        IDictionary<string, Type> args,
        ValtuutusParser.IdentifierExpressionContext idCtx
    )
    {
        var id = idCtx.ID().GetText();
        if (!args.TryGetValue(id, out var type))
        {
            throw new LangException($"{idCtx.ID().GetText()} is not defined in the function context.", idCtx.Start.Line,
                idCtx.Start.Column);
        }

        return new ParameterIdFnNode() { ParameterType = type, ParameterName = id };
    }

    private FunctionNode<LiteralValueUnion> ParseLiteralFnNode(ValtuutusParser.LiteralContext literalCtx)
    {
        if (literalCtx.STRING_LITERAL() != null)
        {
            return new StringLiteralFnNode() { Value = literalCtx.STRING_LITERAL().GetText().Trim('"') };
        }

        if (literalCtx.INT_LITERAL() != null)
        {
            return new IntegerLiteralFnNode() { Value = int.Parse(literalCtx.INT_LITERAL().GetText()) };
        }

        if (literalCtx.DECIMAL_LITERAL() != null)
        {
            return new DecimalLiteralFnNode() { Value = decimal.Parse(literalCtx.DECIMAL_LITERAL().GetText()) };
        }

        if (literalCtx.BOOLEAN_LITERAL() != null)
        {
            return new BooleanLiteralFnNode() { Value = bool.Parse(literalCtx.BOOLEAN_LITERAL().GetText()) };
        }

        throw new LangException(
            $"Unrecognized literal, expected string, int, decimal or boolean, got {literalCtx.GetText()}",
            literalCtx.Start.Line, literalCtx.Start.Column);
    }

    private PermissionNode BuildPermissionNode(string entityName, ValtuutusParser.PermissionExpressionContext context)
    {
        switch (context)
        {
            case ValtuutusParser.AndPermissionExpressionContext andCtx:
                var leftAnd = BuildPermissionNode(entityName, andCtx.permissionExpression(0));
                var rightAnd = BuildPermissionNode(entityName, andCtx.permissionExpression(1));
                return PermissionNode.Intersect(leftAnd, rightAnd);

            case ValtuutusParser.OrPermissionExpressionContext orCtx:
                var leftOr = BuildPermissionNode(entityName, orCtx.permissionExpression(0));
                var rightOr = BuildPermissionNode(entityName, orCtx.permissionExpression(1));
                return PermissionNode.Union(leftOr, rightOr);

            case ValtuutusParser.IdentifierPermissionExpressionContext idCtx:
                var builder = new StringBuilder();
                builder.Append(idCtx.ID(0).GetText());
                if (idCtx.ID().Length > 1)
                {
                    var attr = FindEntityAttribute(idCtx.ID(0).GetText(), idCtx.ID(1).GetText());
                    var perm = FindEntityPermission(idCtx.ID(0).GetText(), idCtx.ID(1).GetText());

                    if (attr is null && perm is null)
                    {
                        throw new LangException($"Undefined attribute or permission with name: {idCtx.ID(1).GetText()} for entity {idCtx.ID(0).GetText()}", idCtx.Start.Line, idCtx.Start.Column);
                    }
                    
                    builder.Append(idCtx.DOT().GetText());
                    builder.Append(idCtx.ID(1).GetText());
                }
                else
                {
                    var attrOrPermissionName = idCtx.ID(0).GetText();
                    
                    var attr = FindEntityAttribute(entityName, attrOrPermissionName);
                    var perm = FindEntityPermission(entityName, attrOrPermissionName);

                    if (attr is null && perm is null)
                    {
                        throw new LangException($"Undefined attribute or permission with name: {attrOrPermissionName}", idCtx.Start.Line, idCtx.Start.Column);
                    }
                }

                return PermissionNode.Leaf(builder.ToString());

            case ValtuutusParser.FunctionCallPermissionExpressionContext fnCtx:
                var functionName = fnCtx.functionCall().ID().GetText();

                var function = FindFunction(functionName);
                if (function is null)
                {
                    throw new LangException($"{functionName} is not a defined function", fnCtx.Start.Line,
                        fnCtx.Start.Column);
                }

                if (function.Parameters.Count != fnCtx.functionCall().argumentList().argument().Length)
                {
                    throw new LangException(
                        $"{functionName} invoked with wrong number of arguments, expected {function.Parameters.Count} got {fnCtx.functionCall().argumentList().argument().Length}",
                        fnCtx.Start.Line, fnCtx.Start.Column);
                }

                var args = fnCtx.functionCall().argumentList().argument()
                    .Select((a, i) =>
                    {
                        var functionParam = function.Parameters.First(p => p.ParamOrder == i);

                        if (a.ID() != null)
                        {
                            var attr = FindEntityAttribute(entityName, a.ID().GetText());

                            if (attr is null)
                            {
                                throw new LangException($"Undefined attribute {a.ID().GetText()}", a.Start.Line,
                                    a.Start.Column);
                            }

                            if (functionParam.ParamType != attr.AttributeType)
                            {
                                throw new LangException(
                                    $"Expected type {functionParam.ParamType}, but got a {attr.AttributeType} attribute",
                                    a.Start.Line, a.Start.Column);
                            }

                            return (PermissionNodeExpArgument)new PermissionNodeExpArgumentAttribute()
                            {
                                AttributeName = a.ID().GetText(), ArgOrder = i
                            };
                        }

                        if (a.contextAccess() != null)
                        {
                            return new PermissionNodeExpArgumentContextAccess()
                            {
                                ContextPropertyName = a.contextAccess().ID().GetText(), ArgOrder = i
                            };
                        }

                        if (a.literal() != null)
                        {
                            if (a.literal().STRING_LITERAL() != null)
                            {
                                if (functionParam.ParamType != typeof(string))
                                {
                                    throw new LangException(
                                        $"Expected type {functionParam.ParamType}, but got {typeof(string)}",
                                        a.Start.Line, a.Start.Column);
                                }

                                return new PermissionNodeExpArgumentStringLiteral()
                                {
                                    Value = a.literal().STRING_LITERAL().GetText().Trim('"'), ArgOrder = i
                                };
                            }

                            if (a.literal().INT_LITERAL() != null)
                            {
                                if (functionParam.ParamType != typeof(int))
                                {
                                    throw new LangException(
                                        $"Expected type {functionParam.ParamType}, but got {typeof(int)}", a.Start.Line,
                                        a.Start.Column);
                                }

                                return new PermissionNodeExpArgumentIntLiteral()
                                {
                                    Value = int.Parse(a.literal().INT_LITERAL().GetText()), ArgOrder = i
                                };
                            }

                            if (a.literal().DECIMAL_LITERAL() != null)
                            {
                                if (functionParam.ParamType != typeof(decimal))
                                {
                                    throw new LangException(
                                        $"Expected type {functionParam.ParamType}, but got {typeof(decimal)}",
                                        a.Start.Line, a.Start.Column);
                                }

                                return new PermissionNodeExpArgumentDecimalLiteral()
                                {
                                    Value = decimal.Parse(a.literal().DECIMAL_LITERAL().GetText()), ArgOrder = i
                                };
                            }
                        }

                        throw new LangException(
                            "Unrecognized argument in function call, ",
                            a.Start.Line, a.Start.Column);
                    })
                    .ToArray();

                return PermissionNode.Expression(functionName, args);

            case ValtuutusParser.ParenthesisPermissionExpressionContext parenCtx:
                return BuildPermissionNode(entityName, parenCtx.permissionExpression());

            default:
                throw new LangException(
                    "Unsupported expression type",
                    context.Start.Line, context.Start.Column);
        }
    }
}