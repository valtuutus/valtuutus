namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>The data query a <see cref="LookupSubjectQuery"/> describes.</summary>
public enum LookupSubjectQueryKind
{
    /// <summary>
    /// Tuples of <see cref="LookupSubjectQuery.Relation"/> on <see cref="LookupSubjectQuery.EntityType"/>
    /// for <see cref="LookupSubjectQuery.Ids"/> whose subject is <see cref="LookupSubjectQuery.SubjectType"/>
    /// (with <see cref="LookupSubjectQuery.SubjectRelation"/> when set).
    /// </summary>
    Relations,

    /// <summary>
    /// <see cref="Relations"/> for every relation in <see cref="LookupSubjectQuery.Names"/> at once.
    /// </summary>
    RelationsMulti,

    /// <summary>
    /// Final subjects reached from <see cref="LookupSubjectQuery.Ids"/> through
    /// <see cref="LookupSubjectQuery.Relation"/> and then <see cref="LookupSubjectQuery.SubjectRelation"/>
    /// on <see cref="LookupSubjectQuery.SubEntityType"/>.
    /// </summary>
    Joined,

    /// <summary>
    /// Values of the attributes in <see cref="LookupSubjectQuery.Names"/> of
    /// <see cref="LookupSubjectQuery.EntityType"/> for <see cref="LookupSubjectQuery.Ids"/>, keyed by
    /// (attribute, entity id).
    /// </summary>
    Attributes,

    /// <summary>
    /// Values of the single attribute <c>Names[0]</c> of <see cref="LookupSubjectQuery.EntityType"/> for
    /// <see cref="LookupSubjectQuery.Ids"/>.
    /// </summary>
    Attribute,

    /// <summary>Every subject of <see cref="LookupSubjectQuery.SubjectType"/> except <see cref="LookupSubjectQuery.Ids"/>.</summary>
    SubjectsExcluding,
}

/// <summary>
/// One data query a lookup graph waits on, described as data. A query runner decides how it runs
/// and against which snap token, then stores the provider's answer in <see cref="Result"/>.
/// </summary>
public sealed class LookupSubjectQuery
{
    public required LookupSubjectQueryKind Kind { get; init; }
    public string EntityType { get; init; } = "";
    public string Relation { get; init; } = "";
    public string[] Names { get; init; } = [];
    public string SubjectType { get; init; } = "";
    public string? SubjectRelation { get; init; }
    public string SubEntityType { get; init; } = "";
    public IReadOnlyCollection<string> Ids { get; init; } = [];

    /// <summary>
    /// The answer: <c>List&lt;RelationTuple&gt;</c> for <see cref="LookupSubjectQueryKind.Relations"/>,
    /// <see cref="LookupSubjectQueryKind.RelationsMulti"/> and <see cref="LookupSubjectQueryKind.Joined"/>;
    /// <c>Dictionary&lt;(string AttributeName, string EntityId), AttributeTuple&gt;</c> for
    /// <see cref="LookupSubjectQueryKind.Attributes"/>; <c>List&lt;AttributeTuple&gt;</c> for
    /// <see cref="LookupSubjectQueryKind.Attribute"/>; <c>List&lt;string&gt;</c> for
    /// <see cref="LookupSubjectQueryKind.SubjectsExcluding"/>.
    /// </summary>
    public object? Result { get; set; }

    // Set once the query is handed to a step, so a query several nodes share runs once.
    internal bool Queued { get; set; }
}
