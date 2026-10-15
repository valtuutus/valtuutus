namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>The data query a <see cref="LookupSubjectQuery"/> describes.</summary>
public enum LookupSubjectQueryKind
{
    /// <summary>
    /// Tuples of <see cref="LookupSubjectQuery.Relation"/> on <see cref="LookupSubjectQuery.EntityType"/>
    /// for <see cref="LookupSubjectQuery.Ids"/> whose subject is <see cref="LookupSubjectQuery.SubjectType"/>
    /// and <see cref="LookupSubjectQuery.SubjectRelation"/>.
    /// </summary>
    Relations,

    /// <summary>
    /// <see cref="Relations"/> for every relation in <see cref="LookupSubjectQuery.Names"/> at once.
    /// </summary>
    RelationsMulti,

    /// <summary>
    /// Direct <see cref="LookupSubjectQuery.SubjectType"/> subjects reached from <see cref="LookupSubjectQuery.Ids"/>
    /// through <see cref="LookupSubjectQuery.Relation"/> tuples whose subject is
    /// <see cref="LookupSubjectQuery.SubEntityType"/>:X#<see cref="LookupSubjectQuery.SubjectRelation"/>, and then
    /// <see cref="LookupSubjectQuery.SubRelation"/> on each such X.
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

    /// <summary>
    /// Subjects of <see cref="LookupSubjectQuery.SubjectType"/> reached from <see cref="LookupSubjectQuery.Ids"/>
    /// through <see cref="LookupSubjectQuery.Relation"/> on <see cref="LookupSubjectQuery.EntityType"/>, following
    /// userset subjects for <see cref="LookupSubjectQuery.Levels"/> levels: level 0 is the relation's
    /// own tuples, level i+1 the tuples of the usersets level i lists. The answer holds the plain
    /// subjects of that type from every level and the userset subjects of the last level only;
    /// only subject columns are read.
    /// </summary>
    UsersetLevels,
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

    /// <summary>The relation the read tuples' subjects carry; empty for a direct subject.</summary>
    public string SubjectRelation { get; init; } = "";

    /// <summary>For <see cref="LookupSubjectQueryKind.Joined"/>, the relation on <see cref="SubEntityType"/> whose direct subjects are read.</summary>
    public string SubRelation { get; init; } = "";
    public string SubEntityType { get; init; } = "";
    public IReadOnlyCollection<string> Ids { get; init; } = [];

    /// <summary>Levels a <see cref="LookupSubjectQueryKind.UsersetLevels"/> query follows; 2 or more.</summary>
    public int Levels { get; init; }

    /// <summary>
    /// The answer: <c>List&lt;RelationTuple&gt;</c> for <see cref="LookupSubjectQueryKind.Relations"/>,
    /// <see cref="LookupSubjectQueryKind.RelationsMulti"/>, <see cref="LookupSubjectQueryKind.Joined"/> and <see cref="LookupSubjectQueryKind.UsersetLevels"/>;
    /// <c>Dictionary&lt;(string AttributeName, string EntityId), AttributeTuple&gt;</c> for
    /// <see cref="LookupSubjectQueryKind.Attributes"/>; <c>List&lt;AttributeTuple&gt;</c> for
    /// <see cref="LookupSubjectQueryKind.Attribute"/>; <c>List&lt;string&gt;</c> for
    /// <see cref="LookupSubjectQueryKind.SubjectsExcluding"/>.
    /// A <c>List&lt;RelationTuple&gt;</c> answer passes to the engine, which returns it to the shared
    /// list pool once the lookup has read it; a runner keeps no reference to it.
    /// </summary>
    public object? Result { get; set; }

    // Set once the query is handed to a step, so a query several nodes share runs once.
    internal bool Queued { get; set; }

    // A plan keeps a query's shape without ids; each request binds its own ids (and, for
    // UsersetLevels, the levels its depth allows) to a fresh copy.
    internal LookupSubjectQuery For(IReadOnlyCollection<string> ids, int? levels = null) => new()
    {
        Kind = Kind, EntityType = EntityType, Relation = Relation, Names = Names, SubjectType = SubjectType,
        SubjectRelation = SubjectRelation, SubRelation = SubRelation, SubEntityType = SubEntityType, Ids = ids, Levels = levels ?? Levels,
    };
}
