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
            var functionNode = ParseFunctionDefinition(funcs);
            schemaBuilder.WithFunction(functionNode);
        }

        // Parse entities
        foreach (var entityCtx in tree.entityDefinition())
        {
            var entityName = entityCtx.ID().GetText();
            
            _symbols.Add(new SchemaSymbol(SymbolType.Entity, entityName));
            
            var entityBuilder = schemaBuilder.WithEntity(entityName);

            var relationCtx = entityCtx.entityBody().relationDefinition()!;

            foreach (var relation in relationCtx)
            {
                var relationName = relation.ID().GetText();
                var refRelations = new List<RelationReference>();
                
                entityBuilder.WithRelation(relationName, relationBuilder =>
                {
                    foreach (var member in relation.relationMember())
                    {
                        var subjectRelation = member.POUND() != null;
                        if (!subjectRelation)
                        {
                            relationBuilder.WithEntityType(member.ID(0).GetText());
                            refRelations.Add(new RelationReference()
                            {
                                ReferencedEntityName = member.ID(0).GetText(),
                                ReferencedEntityRelation = null
                            });
                        }
                        else
                        {
                            relationBuilder.WithEntityType(member.ID(0).GetText(), member.ID(1).GetText());
                            refRelations.Add(new RelationReference()
                            {
                                ReferencedEntityName = member.ID(0).GetText(),
                                ReferencedEntityRelation = member.ID(1).GetText()
                            });
                        }
                    }
                });
                
                _symbols.Add(new RelationSymbol(relationName, entityName, refRelations));
            }

            var attributeCtx = entityCtx.entityBody().attributeDefinition()!;

            foreach (var attribute in attributeCtx)
            {

                var attr = new AttributeSymbol(attribute.ID().GetText(), entityName,
                    _types[attribute.type().GetText()]);
                
                _symbols.Add(attr);
                
                entityBuilder.WithAttribute(attr.Name, attr.AttributeType);
            }

            var permissionCtx = entityCtx.entityBody().permissionDefinition()!;

            foreach (var permission in permissionCtx)
            {
                // Convert permission expression to PermissionNode
                var permissionTree = BuildPermissionNode(permission.permissionExpression());
                _symbols.Add(new PermissionSymbol(permission.ID().GetText(), entityName));
                entityBuilder.WithPermission(permission.ID().GetText(), permissionTree);
            }
        }

        return schemaBuilder.Build();
    }

    private static Function ParseFunctionDefinition(ValtuutusParser.FunctionDefinitionContext funcCtx)
    {
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

        return new Function(funcCtx.ID().GetText(), parameters, expression.Compile());
    }

    private static FunctionNode<bool> ParseFunctionExpression(IDictionary<string, Type> args,
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
            ValtuutusParser.IdentifierExpressionContext  => TryHandleBooleanIdOrParamNode(args, exprCtx),
            ValtuutusParser.LiteralExpressionContext  => TryHandleBooleanIdOrParamNode(args, exprCtx),
            _ => throw new Exception("Unsupported function expression type")
        };
    }

    private static FunctionNode<LiteralValueUnion> ParseLiteralExpression(IDictionary<string, Type> args,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        return exprCtx switch
        {
            ValtuutusParser.IdentifierExpressionContext idCtx => CreateParameterIdFnNode(args, idCtx),
            ValtuutusParser.LiteralExpressionContext litCtx => ParseLiteralFnNode(litCtx.literal()),
            _ => throw new Exception("Unsupported function expression type")
        };
    }

    private static FunctionNode<bool> CreateAndExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.AndExpressionContext andCtx)
    {
        var node = new AndExpressionFnNode();
        var leftChild = ParseFunctionExpression(args, andCtx.functionExpression(0));
        var rightChild = ParseFunctionExpression(args, andCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateOrExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.OrExpressionContext orCtx)
    {
        var node = new OrExpressionFnNode();
        var leftChild = ParseFunctionExpression(args, orCtx.functionExpression(0));
        var rightChild = ParseFunctionExpression(args, orCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.EqualityExpressionContext eqCtx
    )
    {
        var node = new EqualExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, eqCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, eqCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateNotEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.InequalityExpressionContext neqCtx
    )
    {
        var node = new NotEqualExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, neqCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, neqCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateGreaterExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.GreaterExpressionContext gtCtx
    )
    {
        var node = new GreaterExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, gtCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, gtCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateLessExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.LessExpressionContext ltCtx
    )
    {
        var node = new LessExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, ltCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, ltCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateGreaterOrEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.GreaterOrEqualExpressionContext gteCtx
    )
    {
        var node = new GreaterOrEqualExpressionFnNode();

        var leftChild = ParseLiteralExpression(args, gteCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, gteCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateLessOrEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.LessOrEqualExpressionContext lteCtx
    )
    {
        var node = new LessOrEqualExpressionFnNode();
        var leftChild = ParseLiteralExpression(args, lteCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, lteCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;

        return node;
    }

    private static FunctionNode<bool> CreateParenthesisExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.ParenthesisExpressionContext parenCtx)
    {
        var node = new ParenthesisExpressionFnNode
        {
            Child = ParseFunctionExpression(args, parenCtx.functionExpression())
        };

        return node;
    }

    private static FunctionNode<bool> CreateNotExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.NotExpressionContext notCtx)
    {
        var node = new NotExpressionFnNode
        {
            Child = ParseFunctionExpression(args, notCtx.functionExpression())
        };

        return node;
    }

    private static FunctionNode<bool> TryHandleBooleanIdOrParamNode(IDictionary<string, Type> args,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        var handleLiteralNode = (ValtuutusParser.LiteralExpressionContext litCtx) =>
        {
            if (litCtx.literal().BOOLEAN_LITERAL() != null)
            {
                return new EqualExpressionFnNode()
                {
                    Left = ParseLiteralExpression(args, litCtx),
                    Right = new BooleanLiteralFnNode() { Value = true }
                };
            }

            throw new InvalidOperationException();
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

            throw new InvalidOperationException();
        };

        var childNode = exprCtx switch
        {
            ValtuutusParser.IdentifierExpressionContext idCtx => handleParamNode(idCtx),
            ValtuutusParser.LiteralExpressionContext litCtx => handleLiteralNode(litCtx),
            _ => throw new InvalidOperationException()
        };
        
        return childNode;
    }

    private static FunctionNode<LiteralValueUnion> CreateParameterIdFnNode(
        IDictionary<string, Type> args,
        ValtuutusParser.IdentifierExpressionContext idCtx
    )
    {
        var id = idCtx.ID().GetText();
        var type = args[id];

        return new ParameterIdFnNode() { ParameterType = type, ParameterName = id};
    }

    private static FunctionNode<LiteralValueUnion> ParseLiteralFnNode(ValtuutusParser.LiteralContext literalCtx)
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

        throw new Exception("Unrecognized literal");
    }

    private static PermissionNode BuildPermissionNode(ValtuutusParser.PermissionExpressionContext context)
    {
        switch (context)
        {
            case ValtuutusParser.AndPermissionExpressionContext andCtx:
                var leftAnd = BuildPermissionNode(andCtx.permissionExpression(0));
                var rightAnd = BuildPermissionNode(andCtx.permissionExpression(1));
                return PermissionNode.Intersect(leftAnd, rightAnd);

            case ValtuutusParser.OrPermissionExpressionContext orCtx:
                var leftOr = BuildPermissionNode(orCtx.permissionExpression(0));
                var rightOr = BuildPermissionNode(orCtx.permissionExpression(1));
                return PermissionNode.Union(leftOr, rightOr);

            case ValtuutusParser.IdentifierPermissionExpressionContext idCtx:
                var builder = new StringBuilder();
                builder.Append(idCtx.ID(0).GetText());
                if (idCtx.ID().Length > 1)
                {
                    builder.Append(idCtx.DOT().GetText());
                    builder.Append(idCtx.ID(1).GetText());
                }

                return PermissionNode.Leaf(builder.ToString());

            case ValtuutusParser.FunctionCallPermissionExpressionContext fnCtx:
                var functionName = fnCtx.functionCall().ID().GetText();

                var args = fnCtx.functionCall().argumentList().argument()
                    .Select((a, i) =>
                    {
                        if (a.ID() != null)
                        {
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
                                return new PermissionNodeExpArgumentStringLiteral()
                                {
                                    Value = a.literal().STRING_LITERAL().GetText().Trim('"'), ArgOrder = i
                                };
                            }

                            if (a.literal().INT_LITERAL() != null)
                            {
                                return new PermissionNodeExpArgumentIntLiteral()
                                {
                                    Value = int.Parse(a.literal().INT_LITERAL().GetText()), ArgOrder = i
                                };
                            }

                            if (a.literal().DECIMAL_LITERAL() != null)
                            {
                                return new PermissionNodeExpArgumentDecimalLiteral()
                                {
                                    Value = decimal.Parse(a.literal().DECIMAL_LITERAL().GetText()), ArgOrder = i
                                };
                            }
                        }

                        throw new Exception("Unrecognized function call");
                    })
                    .ToArray();

                return PermissionNode.Expression(functionName, args);

            case ValtuutusParser.ParenthesisPermissionExpressionContext parenCtx:
                return BuildPermissionNode(parenCtx.permissionExpression());

            default:
                throw new NotSupportedException("Unsupported expression type");
        }
    }
}