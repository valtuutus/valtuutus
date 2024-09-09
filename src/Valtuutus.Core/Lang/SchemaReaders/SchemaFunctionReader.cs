using System.Linq.Expressions;
using Valtuutus.Core.Schemas;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang.SchemaReaders;

internal class SchemaFunctionReader(SchemaReader schemaReader)
{
    public Function Parse(ValtuutusParser.FunctionDefinitionContext funcCtx)
    {
        var functionName = funcCtx.ID().GetText();

        if (schemaReader.FindFunction(functionName) is not null)
        {
            schemaReader.AddError(new LangError()
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
            var paramType = funcCtx.parameterList().type()[i].ToLangType();
            parameters.Add(new FunctionParameter() { ParamName = paramName, ParamOrder = i, ParamType = paramType });
        }

        var tree = ParseFunctionExpression(
            parameters.ToDictionary(x => x.ParamName, x => x.ParamType),
            funcCtx.functionBody().functionExpression()
        );

        var parameter = Expression.Parameter(typeof(IDictionary<string, object?>), "args");
        var expression = tree.GetExpression(parameter);

        schemaReader.AddSymbol(new FunctionSymbol(functionName, funcCtx.Start.Line, funcCtx.Start.Column, parameters));

        return new Function(functionName, parameters, expression.Compile());
    }

    private FunctionNode<bool> ParseFunctionExpression(IDictionary<string, LangType> args,
        ValtuutusParser.FunctionExpressionContext exprCtx)
    {
        return exprCtx switch
        {
            ValtuutusParser.AndExpressionContext andCtx => CreateAndExpressionNode(args, andCtx),
            ValtuutusParser.OrExpressionContext orCtx => CreateOrExpressionNode(args, orCtx),
            ValtuutusParser.EqualityExpressionContext or
                ValtuutusParser.InequalityExpressionContext or
                ValtuutusParser.GreaterExpressionContext or
                ValtuutusParser.LessExpressionContext or
                ValtuutusParser.GreaterOrEqualExpressionContext or
                ValtuutusParser.LessOrEqualExpressionContext => CreateComparisonExpressionNode(args, exprCtx),
            ValtuutusParser.ParenthesisExpressionContext parenCtx => CreateParenthesisExpressionNode(args, parenCtx),
            ValtuutusParser.NotExpressionContext notCtx => CreateNotExpressionNode(args, notCtx),
            ValtuutusParser.IdentifierExpressionContext => TryHandleBooleanIdOrParamNode(args, exprCtx),
            ValtuutusParser.LiteralExpressionContext => TryHandleBooleanIdOrParamNode(args, exprCtx),
            _ => throw new LangException("Unsupported function expression type", exprCtx.Start.Line,
                exprCtx.Start.Column)
        };
    }

    private FunctionNode<LiteralValueUnion> ParseLiteralExpression(IDictionary<string, LangType> args,
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

    private FunctionNode<bool> CreateAndExpressionNode(IDictionary<string, LangType> args,
        ValtuutusParser.AndExpressionContext andCtx)
    {
        return new AndExpressionFnNode
        {
            Left = ParseFunctionExpression(args, andCtx.functionExpression(0)),
            Right = ParseFunctionExpression(args, andCtx.functionExpression(1))
        };
    }

    private FunctionNode<bool> CreateOrExpressionNode(IDictionary<string, LangType> args,
        ValtuutusParser.OrExpressionContext orCtx)
    {
        return new OrExpressionFnNode
        {
            Left = ParseFunctionExpression(args, orCtx.functionExpression(0)),
            Right = ParseFunctionExpression(args, orCtx.functionExpression(1))
        };
    }

    private FunctionNode<bool> CreateComparisonExpressionNode(IDictionary<string, LangType> args,
        ValtuutusParser.FunctionExpressionContext ctx)
    {
        BinaryFunctionNode<bool, LiteralValueUnion> node = ctx switch
        {
            ValtuutusParser.EqualityExpressionContext eqCtx => new EqualExpressionFnNode()
            {
                Left = ParseLiteralExpression(args, eqCtx.functionExpression(0)),
                Right = ParseLiteralExpression(args, eqCtx.functionExpression(1))
            },
            ValtuutusParser.InequalityExpressionContext neqCtx => new NotEqualExpressionFnNode()
            {
                Left = ParseLiteralExpression(args, neqCtx.functionExpression(0)),
                Right = ParseLiteralExpression(args, neqCtx.functionExpression(1))
            },
            ValtuutusParser.GreaterExpressionContext gtCtx => new GreaterExpressionFnNode()
            {
                Left = ParseLiteralExpression(args, gtCtx.functionExpression(0)),
                Right = ParseLiteralExpression(args, gtCtx.functionExpression(1))
            },
            ValtuutusParser.LessExpressionContext ltCtx => new LessExpressionFnNode()
            {
                Left = ParseLiteralExpression(args, ltCtx.functionExpression(0)),
                Right = ParseLiteralExpression(args, ltCtx.functionExpression(1))
            },
            ValtuutusParser.GreaterOrEqualExpressionContext gteCtx => new GreaterOrEqualExpressionFnNode()
            {
                Left = ParseLiteralExpression(args, gteCtx.functionExpression(0)),
                Right = ParseLiteralExpression(args, gteCtx.functionExpression(1))
            },
            ValtuutusParser.LessOrEqualExpressionContext lteCtx => new LessOrEqualExpressionFnNode()
            {
                Left = ParseLiteralExpression(args, lteCtx.functionExpression(0)),
                Right = ParseLiteralExpression(args, lteCtx.functionExpression(1))
            },
        };

        if (node.Left.TypeContext != node.Right.TypeContext)
        {
            throw new LangException(
                $"Incompatible types, comparing {node.Left.TypeContext.ToTypeString()} and {node.Right.TypeContext.ToTypeString()}",
                ctx.Start.Line,
                ctx.Start.Column);
        }

        return node;
    }

    private FunctionNode<bool> CreateParenthesisExpressionNode(IDictionary<string, LangType> args,
        ValtuutusParser.ParenthesisExpressionContext parenCtx)
    {
        var node = new ParenthesisExpressionFnNode
        {
            Child = ParseFunctionExpression(args, parenCtx.functionExpression())
        };

        return node;
    }

    private FunctionNode<bool> CreateNotExpressionNode(IDictionary<string, LangType> args,
        ValtuutusParser.NotExpressionContext notCtx)
    {
        var node = new NotExpressionFnNode { Child = ParseFunctionExpression(args, notCtx.functionExpression()) };

        return node;
    }

    private FunctionNode<bool> TryHandleBooleanIdOrParamNode(IDictionary<string, LangType> args,
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
            var paramNode = CreateParameterIdFnNode(args, idCtx);
            if (paramNode.TypeContext == LangType.Boolean)
            {
                return new EqualExpressionFnNode()
                {
                    Left = paramNode, Right = new BooleanLiteralFnNode() { Value = true },
                };
            }

            throw new LangException(
                $"Expected boolean parameter, got {paramNode.TypeContext.ToTypeString()}",
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
        IDictionary<string, LangType> args,
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

        throw new InvalidOperationException("Unknown literal");
    }
}