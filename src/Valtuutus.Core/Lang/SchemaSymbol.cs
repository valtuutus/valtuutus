using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core.Lang;

public enum SymbolType
{
    Entity,
    Function,
    Relation,
    Attribute,
    Permission,
}

public abstract record Symbol
{
    protected Symbol(SymbolType type, string name)
    {
        Name = name;
        Type = type;
    }

    public SymbolType Type { get; private set; }
    public string Name { get; init; }
};

public record SchemaSymbol : Symbol
{
    public SchemaSymbol(SymbolType type, string name) : base(type, name)
    {
    }
};

public abstract record EntitySymbol : SchemaSymbol
{
    [SetsRequiredMembers]
    protected EntitySymbol(SymbolType type, string name, string entityName) : base(type, name)
    {
        EntityName = entityName;
    }

    public required string EntityName { get; init; }
};

public record PermissionSymbol : EntitySymbol
{
    [SetsRequiredMembers]
    public PermissionSymbol(string name, string entityName) : base(SymbolType.Permission, name, entityName) { }
};


public record RelationSymbol : EntitySymbol
{
    [SetsRequiredMembers]
    public RelationSymbol(string name, string entityName, IList<RelationReference> references) : base(
        SymbolType.Relation, name, entityName)
    {
        References = references;
    }

    public required IList<RelationReference> References { get; init; }
};

public record RelationReference
{
    public required string ReferencedEntityName { get; init; }
    public required string? ReferencedEntityRelation { get; init; }
}

public record AttributeSymbol : EntitySymbol
{
    [SetsRequiredMembers]
    public AttributeSymbol(string name, string entityName, Type attributeType) : base(SymbolType.Attribute, name, entityName)
    {
        AttributeType = attributeType;
    }

    public required Type AttributeType { get; init; }
};