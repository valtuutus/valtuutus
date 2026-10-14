namespace Valtuutus.Core.Engines.LookupSubject.V2;

internal enum LookupSubjectQueryKind
{
    // Tuples of Relation on EntityType for Ids whose subject is SubjectType (with SubjectRelation).
    Relations,
    // Relations for every relation in Names at once.
    RelationsMulti,
    // Final subjects reached from Ids through Relation and then SubjectRelation on SubEntityType.
    Joined,
    // Values of the attributes in Names of EntityType for Ids, keyed by (attribute, entity id).
    Attributes,
    // Values of the single attribute Names[0] of EntityType for Ids.
    Attribute,
    // Every subject of SubjectType except Ids.
    SubjectsExcluding,
}

/// <summary>
/// One data query a lookup graph waits on, described as data. A query runner decides how it runs
/// and against which snap token, then stores the provider's answer in <see cref="Result"/>.
/// </summary>
internal sealed class LookupSubjectQuery
{
    public required LookupSubjectQueryKind Kind { get; init; }
    public string EntityType { get; init; } = "";
    public string Relation { get; init; } = "";
    public string[] Names { get; init; } = [];
    public string SubjectType { get; init; } = "";
    public string? SubjectRelation { get; init; }
    public string SubEntityType { get; init; } = "";
    public IReadOnlyCollection<string> Ids { get; init; } = [];

    // List<RelationTuple> for Relations, RelationsMulti and Joined;
    // Dictionary<(string, string), AttributeTuple> for Attributes; List<AttributeTuple> for
    // Attribute; List<string> for SubjectsExcluding.
    public object? Result { get; set; }

    // Set once the query is handed to a step, so a query several nodes share runs once.
    public bool Queued { get; set; }
}
