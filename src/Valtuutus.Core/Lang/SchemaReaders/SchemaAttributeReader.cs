using Valtuutus.Lang;

namespace Valtuutus.Core.Lang.SchemaReaders;

internal class SchemaAttributeReader(SchemaReader schemaReader)
{
    public OneOf<(string attributeName, Type attributeType), List<LangError>> Parse(
        ValtuutusParser.AttributeDefinitionContext attribute,
        string entityName)
    {
        var attr = new AttributeSymbol(attribute.ID().GetText(), attribute.Start.Line, attribute.Start.Column,
            entityName, attribute.type().ToLangType());

        if (schemaReader.FindEntityAttribute(entityName, attr.Name) is not null)
        {
            return new List<LangError>()
            {
                new($"Entity '{entityName}' '{attr.Name}' attribute already been defined.",
                    attribute.Start.Line, attribute.Start.Column)
            };
        }

        schemaReader.AddSymbol(attr);

        return (attr.Name, attr.AttributeType.ToClrType());
    }
}