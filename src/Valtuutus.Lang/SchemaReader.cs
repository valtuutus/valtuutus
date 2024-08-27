using Antlr4.Runtime;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Lang;

public static class SchemaReader
{
    private static readonly Dictionary<string, Type> _types = new()
    {
        { "string", typeof(string) },
        { "int", typeof(int)},
        { "bool", typeof(bool)},
        { "decimal", typeof(decimal) }
    };

    public static Schema Parse(string schema)
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
        errorListener.ThrowIfErrors();
        
        // Parse functions
        foreach (var funcs in tree.functionDefinition())
        {
            //funcs.
        }
        
        // Parse entities
        foreach (var entityCtx in tree.entityDefinition())
        {
            var entityBuilder = schemaBuilder.WithEntity(entityCtx.ENTITY_NAME().GetText());

            var relationCtx = entityCtx.entityBody().relationDefinition()!;

            foreach (var relation in relationCtx)
            {
                entityBuilder.WithRelation(relation.RELATION_NAME().GetText(), relationBuilder =>
                {
                    var subjectRelation = relation.POUND() != null;
                    if (!subjectRelation)
                    {
                        relationBuilder.WithEntityType(relation.TARGET_ENTITY_NAME().GetText());
                    }
                    else
                    {
                        relationBuilder.WithEntityType(relation.TARGET_ENTITY_NAME().GetText(), relation.SUBJECT_RELATION_NAME().GetText());
                    }
                });
            }
            
            var attributeCtx = entityCtx.entityBody().attributeDefinition()!;

            foreach (var attribute in attributeCtx)
            {
                var attrType = _types[attribute.ATTRIBUTE_TYPE().GetText()];
                entityBuilder.WithAttribute(attribute.ATTRIBUTE_NAME().GetText(), attrType);
            }
            
            var permissionCtx = entityCtx.entityBody().permissionDefinition()!;

            foreach (var permission in permissionCtx)
            {
                // Convert permission expression to PermissionNode
                var permissionTree = BuildPermissionNode(permission.permissionExpression());
                entityBuilder.WithPermission(permission.PERMISSION_NAME().GetText(), permissionTree);
            }
            
        }
        return schemaBuilder.Build();

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
                return PermissionNode.Leaf(idCtx.IDENTIFIER().GetText());

            
            // TODO: Do some magic to handle the creation of expressions
            // case ValtuutusParser.FunctionCallExpressionContext funcCtx:
            //     var funcName = funcCtx.ID().GetText();
            //     if (funcName == "is_weekday")
            //     {
            //         return PermissionNode.AttributeStringExpression("day_of_week", day => day != "saturday" && day != "sunday");
            //     }
            //     else if (funcName == "check_credit")
            //     {
            //         return PermissionNode.AttributeIntExpression("credit", credit => credit > 5000);
            //     }
            //     throw new Exception($"Unknown function: {funcName}");

            case ValtuutusParser.ParenthesisPermissionExpressionContext parenCtx:
                return BuildPermissionNode(parenCtx.permissionExpression());

            default:
                throw new Exception("Unsupported expression type");
        }
    }
}