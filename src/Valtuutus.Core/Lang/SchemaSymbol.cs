using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Lang;

internal enum SymbolType
{
    Entity,
    Function,
    Relation,
    Attribute,
    Permission,
}

internal abstract record Symbol
{
    protected Symbol(SymbolType type, string name, int declarationLine, int startPosition)
    {
        Name = name;
        DeclarationLine = declarationLine;
        StartPosition = startPosition;
        Type = type;
    }

    public SymbolType Type { get; private set; }
    public int DeclarationLine { get; private set; }
    public int StartPosition { get; }
    public string Name { get; private set; }
};

internal record SchemaSymbol : Symbol
{
    public SchemaSymbol(SymbolType type, string name, int declarationLine, int startPosition)
        : base(type, name, declarationLine, startPosition)
    {
    }
};

internal record FunctionSymbol : SchemaSymbol
{
    public List<FunctionParameter> Parameters { get; }

    public FunctionSymbol(string name, int declarationLine, int startPosition,
        List<FunctionParameter> parameters) : base(SymbolType.Function, name, declarationLine, startPosition)
    {
        Parameters = parameters;
    }
}

internal abstract record EntitySymbol : SchemaSymbol
{
    [SetsRequiredMembers]
    protected EntitySymbol(SymbolType type, string name, int declarationLine, int startPosition,
        string entityName) : base(type, name, declarationLine, startPosition)
    {
        EntityName = entityName;
    }

    public required string EntityName { get; init; }
};

internal record PermissionSymbol : EntitySymbol
{
    [SetsRequiredMembers]
    public PermissionSymbol(string name, int declarationLine, int startPosition, string entityName)
        : base(SymbolType.Permission, name, declarationLine, startPosition, entityName)
    {
    }
};

internal record RelationSymbol : EntitySymbol
{
    [SetsRequiredMembers]
    public RelationSymbol(string name, int declarationLine, int startPosition, string entityName,
        IList<RelationReference> references)
        : base(SymbolType.Relation, name, declarationLine, startPosition, entityName)
    {
        References = references;
    }

    public required IList<RelationReference> References { get; init; }
};

internal record RelationReference
{
    public required string ReferencedEntityName { get; init; }
    public required string? ReferencedEntityRelation { get; init; }
}

internal record AttributeSymbol : EntitySymbol
{
    [SetsRequiredMembers]
    public AttributeSymbol(string name, int declarationLine, int startPosition, string entityName, LangType attributeType)
        : base(SymbolType.Attribute, name, declarationLine, startPosition, entityName)
    {
        AttributeType = attributeType;
    }

    public required LangType AttributeType { get; init; }
};