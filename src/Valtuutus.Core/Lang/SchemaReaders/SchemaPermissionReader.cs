using System.Diagnostics;
using Valtuutus.Core.Schemas;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang.SchemaReaders;

internal class SchemaPermissionReader(SchemaReader schemaReader)
{
    public PermissionNode Parse(ValtuutusParser.PermissionDefinitionContext permission, string entityName)
    {
        var permissionTree = BuildPermissionNode(entityName, permission.permissionExpression());
        schemaReader.AddSymbol(new PermissionSymbol(permission.ID().GetText(), permission.Start.Line,
            permission.Start.Column, entityName));
        return permissionTree;
    }

    private PermissionNode BuildPermissionNode(string entityName, ValtuutusParser.PermissionExpressionContext context)
    {
        return context switch
        {
            ValtuutusParser.AndPermissionExpressionContext andCtx => ParseIntersectExp(entityName, andCtx),
            ValtuutusParser.OrPermissionExpressionContext orCtx => ParseUnionExp(entityName, orCtx),
            ValtuutusParser.IdentifierPermissionExpressionContext idCtx => ParseIdentifierExp(entityName, idCtx),
            ValtuutusParser.FunctionCallPermissionExpressionContext fnCtx => ParseFunctionCallExp(entityName, fnCtx),
            ValtuutusParser.ParenthesisPermissionExpressionContext parenCtx => BuildPermissionNode(entityName,
                parenCtx.permissionExpression()),
            _ => throw new UnreachableException()
        };
    }

    private PermissionNode ParseFunctionCallExp(string entityName,
        ValtuutusParser.FunctionCallPermissionExpressionContext fnCtx)
    {
        var functionName = fnCtx.functionCall().ID().GetText();

        var function = schemaReader.FindFunction(functionName);
        ValidateUndefinedFunction(fnCtx, function, functionName);
        ValidateWrongArgumentCount(fnCtx, function!, functionName);

        var args = fnCtx.functionCall().argumentList().argument()
            .Select((a, i) => ParseFnArgument(entityName, function, i, a))
            .ToArray();

        return PermissionNode.Expression(functionName, args);
    }

    private PermissionNodeExpArgument ParseFnArgument(string entityName, FunctionSymbol? function, int i,
        ValtuutusParser.ArgumentContext a)
    {
        var functionParam = function!.Parameters.First(p => p.ParamOrder == i);

        if (a.ID() != null)
        {
            return ParseFnAttributeArgument(entityName, a, functionParam, i);
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
            return ParseFnLiteralArgument(a, functionParam, i);
        }

        throw new UnreachableException();
    }

    private static PermissionNodeExpArgument ParseFnLiteralArgument(ValtuutusParser.ArgumentContext argCtx,
        FunctionParameter functionParam, int argumentIndex)
    {
        if (argCtx.literal().STRING_LITERAL() != null)
        {
            return ParseFnLiteralStringArgument(argCtx, functionParam, argumentIndex);
        }

        if (argCtx.literal().INT_LITERAL() != null)
        {
            return ParseFnLiteralIntArgument(argCtx, functionParam, argumentIndex);
        }

        if (argCtx.literal().DECIMAL_LITERAL() != null)
        {
            return ParseFnLiteralDecimalArgument(argCtx, functionParam, argumentIndex);
        }

        if (argCtx.literal().BOOLEAN_LITERAL() != null)
        {
            return ParseFnLiteralBooleanArgument(argCtx, functionParam, argumentIndex);
        }

        throw new UnreachableException();
    }

    private static PermissionNodeExpArgument ParseFnLiteralBooleanArgument(ValtuutusParser.ArgumentContext argCtx,
        FunctionParameter functionParam, int argumentIndex)
    {
        if (functionParam.ParamType != LangType.Boolean)
        {
            throw new LangException(
                $"Expected type {functionParam.ParamType.ToTypeString()}, but got a {LangType.Boolean.ToTypeString()} literal",
                argCtx.Start.Line, argCtx.Start.Column);
        }

        return new PermissionNodeExpArgumentDecimalLiteral()
        {
            Value = decimal.Parse(argCtx.literal().DECIMAL_LITERAL().GetText()), ArgOrder = argumentIndex
        };
    }

    private static PermissionNodeExpArgument ParseFnLiteralDecimalArgument(ValtuutusParser.ArgumentContext argCtx,
        FunctionParameter functionParam, int argumentIndex)
    {
        if (functionParam.ParamType != LangType.Decimal)
        {
            throw new LangException(
                $"Expected type {functionParam.ParamType.ToTypeString()}, but got a {LangType.Decimal.ToTypeString()} literal",
                argCtx.Start.Line, argCtx.Start.Column);
        }

        return new PermissionNodeExpArgumentDecimalLiteral()
        {
            Value = decimal.Parse(argCtx.literal().DECIMAL_LITERAL().GetText()), ArgOrder = argumentIndex
        };
    }

    private static PermissionNodeExpArgument ParseFnLiteralIntArgument(ValtuutusParser.ArgumentContext argCtx,
        FunctionParameter functionParam, int argumentIndex)
    {
        if (functionParam.ParamType != LangType.Int)
        {
            throw new LangException(
                $"Expected type {functionParam.ParamType.ToTypeString()}, but got a {LangType.Int.ToTypeString()} literal",
                argCtx.Start.Line,
                argCtx.Start.Column);
        }

        return new PermissionNodeExpArgumentIntLiteral()
        {
            Value = int.Parse(argCtx.literal().INT_LITERAL().GetText()), ArgOrder = argumentIndex
        };
    }

    private static PermissionNodeExpArgument ParseFnLiteralStringArgument(ValtuutusParser.ArgumentContext argCtx,
        FunctionParameter functionParam, int argumentIndex)
    {
        if (functionParam.ParamType != LangType.String)
        {
            throw new LangException(
                $"Expected type {functionParam.ParamType.ToTypeString()}, but got a {LangType.String.ToTypeString()} literal",
                argCtx.Start.Line, argCtx.Start.Column);
        }

        return new PermissionNodeExpArgumentStringLiteral()
        {
            Value = argCtx.literal().STRING_LITERAL().GetText().Trim('"'), ArgOrder = argumentIndex
        };
    }

    private PermissionNodeExpArgument ParseFnAttributeArgument(string entityName,
        ValtuutusParser.ArgumentContext argCtx,
        FunctionParameter functionParam, int argumentIndex)
    {
        var attr = schemaReader.FindEntityAttribute(entityName, argCtx.ID().GetText());

        if (attr is null)
        {
            throw new LangException($"Undefined attribute {argCtx.ID().GetText()}", argCtx.Start.Line,
                argCtx.Start.Column);
        }

        if (functionParam.ParamType != attr.AttributeType)
        {
            throw new LangException(
                $"Expected type {functionParam.ParamType.ToTypeString()}, but got a {attr.AttributeType.ToTypeString()} attribute",
                argCtx.Start.Line, argCtx.Start.Column);
        }

        return new PermissionNodeExpArgumentAttribute()
        {
            AttributeName = argCtx.ID().GetText(), ArgOrder = argumentIndex
        };
    }

    private static void ValidateUndefinedFunction(ValtuutusParser.FunctionCallPermissionExpressionContext fnCtx,
        FunctionSymbol? function,
        string functionName)
    {
        if (function is null)
        {
            throw new LangException($"{functionName} is not a defined function", fnCtx.Start.Line,
                fnCtx.Start.Column);
        }
    }

    private static void ValidateWrongArgumentCount(ValtuutusParser.FunctionCallPermissionExpressionContext fnCtx,
        FunctionSymbol function,
        string functionName)
    {
        if (function.Parameters.Count != fnCtx.functionCall().argumentList().argument().Length)
        {
            throw new LangException(
                $"{functionName} invoked with wrong number of arguments, expected {function.Parameters.Count} got {fnCtx.functionCall().argumentList().argument().Length}",
                fnCtx.Start.Line, fnCtx.Start.Column);
        }
    }

    private PermissionNode ParseIdentifierExp(string entityName,
        ValtuutusParser.IdentifierPermissionExpressionContext idCtx)
    {
        if (idCtx.ID().Length > 1)
        {
            ValidateFnIndirectIdentifierArgument(entityName, idCtx);
        }
        else
        {
            ValidateDirectIdentifierArgument(entityName, idCtx);
        }

        return PermissionNode.Leaf(idCtx.GetText());
    }

    private void ValidateFnIndirectIdentifierArgument(string entityName,
        ValtuutusParser.IdentifierPermissionExpressionContext idCtx)
    {
        var targetRelationName = idCtx.ID(0).GetText();
        var targetRelation = schemaReader.FindEntityRelation(entityName, targetRelationName);

        if (targetRelation is null)
        {
            throw new LangException($"Undefined relation with name: {targetRelationName}.",
                idCtx.Start.Line, idCtx.Start.Column);
        }

        // The relations are already validated here,
        // so we can get the first without thinking about it
        var targetEntityName = schemaReader.GetFinalEntitiesNames(targetRelation).First();

        var targetEntityRelationName = idCtx.ID(1).GetText();
        var attr = schemaReader.FindEntityAttribute(targetEntityName, targetEntityRelationName);
        var perm = schemaReader.FindEntityPermission(targetEntityName, targetEntityRelationName);
        var relation = schemaReader.FindEntityRelation(targetEntityName, targetEntityRelationName);

        if (attr is null && perm is null && relation is null)
        {
            throw new LangException(
                $"Undefined relation, attribute or permission with name: {targetEntityRelationName} for entity {targetEntityName}",
                idCtx.Start.Line, idCtx.Start.Column);
        }
    }

    private void ValidateDirectIdentifierArgument(string entityName,
        ValtuutusParser.IdentifierPermissionExpressionContext idCtx)
    {
        var attrOrPermissionName = idCtx.ID(0).GetText();

        var attr = schemaReader.FindEntityAttribute(entityName, attrOrPermissionName);
        var perm = schemaReader.FindEntityPermission(entityName, attrOrPermissionName);
        var relation = schemaReader.FindEntityRelation(entityName, attrOrPermissionName);

        if (attr is null && perm is null && relation is null)
        {
            throw new LangException(
                $"Undefined relation, attribute or permission with name: {attrOrPermissionName}",
                idCtx.Start.Line, idCtx.Start.Column);
        }
    }

    private PermissionNode ParseUnionExp(string entityName, ValtuutusParser.OrPermissionExpressionContext orCtx)
    {
        var leftOr = BuildPermissionNode(entityName, orCtx.permissionExpression(0));
        var rightOr = BuildPermissionNode(entityName, orCtx.permissionExpression(1));
        return PermissionNode.Union(leftOr, rightOr);
    }

    private PermissionNode ParseIntersectExp(string entityName, ValtuutusParser.AndPermissionExpressionContext andCtx)
    {
        var leftAnd = BuildPermissionNode(entityName, andCtx.permissionExpression(0));
        var rightAnd = BuildPermissionNode(entityName, andCtx.permissionExpression(1));
        return PermissionNode.Intersect(leftAnd, rightAnd);
    }
}