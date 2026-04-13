namespace Valtuutus.Core.Engines.LookupEntity;

/// <summary>
/// Constrains a LookupEntity query to entities that have a specific relation
/// to a given subject. All fields are required when a scope is provided.
/// Use this to efficiently answer queries like "which tasks in project Y
/// can user X view?" without loading all entities globally.
/// </summary>
public readonly record struct EntityScope(
    string Relation,
    string SubjectType,
    string SubjectId
);
