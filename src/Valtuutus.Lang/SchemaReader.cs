using Antlr4.Runtime;
using OneOf;
using System.Text;
using System.Linq.Expressions;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Lang;

public static class SchemaReader
{
    private static readonly Dictionary<string, Type> _types = new()
    {
        { "string", typeof(string) },
        { "int", typeof(int) },
        { "bool", typeof(bool) },
        { "decimal", typeof(decimal) }
    };

    public static OneOf<Schema, List<string>> Parse(string schema)
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
            var entityBuilder = schemaBuilder.WithEntity(entityCtx.ID().GetText());

            var relationCtx = entityCtx.entityBody().relationDefinition()!;

            foreach (var relation in relationCtx)
            {
                var idlen = relation.ID().Length;
                entityBuilder.WithRelation(relation.ID(0).GetText(), relationBuilder =>
                {
                    var subjectRelation = relation.POUND().Length > 0;
                    if (!subjectRelation)
                    {
                        relationBuilder.WithEntityType(relation.ID(idlen - 1).GetText());
                    }
                    else
                    {
                        relationBuilder.WithEntityType(relation.ID(idlen - 2).GetText(),
                            relation.ID(idlen - 1).GetText());
                    }
                });
            }

            var attributeCtx = entityCtx.entityBody().attributeDefinition()!;

            foreach (var attribute in attributeCtx)
            {
                var attrType = _types[attribute.type().GetText()];
                entityBuilder.WithAttribute(attribute.ID().GetText(), attrType);
            }

            var permissionCtx = entityCtx.entityBody().permissionDefinition()!;

            foreach (var permission in permissionCtx)
            {
                // Convert permission expression to PermissionNode
                var permissionTree = BuildPermissionNode(permission.expression());
                entityBuilder.WithPermission(permission.ID().GetText(), permissionTree);
            }
        }

        return schemaBuilder.Build();
    }

    private static Function ParseFunctionDefinition(ValtuutusParser.FunctionDefinitionContext funcCtx)
    {
        var args = new Dictionary<string, Type>();

        var ids = funcCtx.parameterList().ID();
        for (int i = 0; i < ids.Length; i++)
        {
            var param = ids[i];
            var paramName = param.GetText();
            var paramType = _types[funcCtx.parameterList().type()[i].GetText()];
            args.Add(paramName, paramType);
        }

        var tree = ParseFunctionExpression(args, funcCtx.functionBody().functionExpression());

        var parameter = Expression.Parameter(typeof(IDictionary<string, object>), "args");
        var expression = tree.GetExpression(parameter);

        return new Function(funcCtx.ID().GetText(), args, expression.Compile());
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
        var node = new AndExpressionFnNode { };
        var leftChild = ParseFunctionExpression(args, andCtx.functionExpression(0));
        var rightChild = ParseFunctionExpression(args, andCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateOrExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.OrExpressionContext orCtx)
    {
        var node = new OrExpressionFnNode { };
        var leftChild = ParseFunctionExpression(args, orCtx.functionExpression(0));
        var rightChild = ParseFunctionExpression(args, orCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.EqualityExpressionContext eqCtx
    )
    {
        var node = new EqualExpressionFnNode { };
        var leftChild = ParseLiteralExpression(args, eqCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, eqCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateNotEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.InequalityExpressionContext neqCtx
    )
    {
        var node = new NotEqualExpressionFnNode { };
        var leftChild = ParseLiteralExpression(args, neqCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, neqCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateGreaterExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.GreaterExpressionContext gtCtx
    )
    {
        var node = new GreaterExpressionFnNode() { };
        var leftChild = ParseLiteralExpression(args, gtCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, gtCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;


        return node;
    }

    private static FunctionNode<bool> CreateLessExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.LessExpressionContext ltCtx
    )
    {
        var node = new LessExpressionFnNode { };
        var leftChild = ParseLiteralExpression(args, ltCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, ltCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateGreaterOrEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.GreaterOrEqualExpressionContext gteCtx
    )
    {
        var node = new GreaterOrEqualExpressionFnNode { };

        var leftChild = ParseLiteralExpression(args, gteCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, gteCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateLessOrEqualExpressionNode(
        IDictionary<string, Type> args,
        ValtuutusParser.LessOrEqualExpressionContext lteCtx
    )
    {
        var node = new LessOrEqualExpressionFnNode { };
        var leftChild = ParseLiteralExpression(args, lteCtx.functionExpression(0));
        var rightChild = ParseLiteralExpression(args, lteCtx.functionExpression(1));

        if (leftChild.TypeContext != rightChild.TypeContext)
        {
            throw new Exception("Incompatible types");
        }

        node.Left = leftChild;
        node.Right = rightChild;
        node.TypeContext = leftChild.TypeContext;

        return node;
    }

    private static FunctionNode<bool> CreateParenthesisExpressionNode(IDictionary<string, Type> args,
        ValtuutusParser.ParenthesisExpressionContext parenCtx)
    {
        var node = new ParenthesisExpressionFnNode { };

        var childNode = ParseFunctionExpression(args, parenCtx.functionExpression());
        node.TypeContext = childNode.TypeContext;
        node.Child = childNode;

        return node;
    }
    
    private static FunctionNode<LiteralValueUnion> CreateParameterIdFnNode(
        IDictionary<string, Type> args,
        ValtuutusParser.IdentifierExpressionContext idCtx
    )
    {
        var id = idCtx.ID().GetText();
        var type = args[id];

        return new ParameterIdFnNode() { ParameterType = type, ParameterName = id, TypeContext = type };
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

            case ValtuutusParser.ParenthesisPermissionExpressionContext parenCtx:
                return BuildPermissionNode(parenCtx.permissionExpression());

            default:
                throw new NotSupportedException("Unsupported expression type");
        }
    }
}