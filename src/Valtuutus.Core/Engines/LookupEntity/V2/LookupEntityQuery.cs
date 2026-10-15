namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>The data query a <see cref="LookupEntityQuery"/> describes.</summary>
public enum LookupEntityQueryKind
{
    /// <summary>
    /// Tuples of <see cref="LookupEntityQuery.Relation"/> on <see cref="LookupEntityQuery.EntityType"/> whose
    /// subject is <see cref="LookupEntityQuery.SubjectType"/>:id#<see cref="LookupEntityQuery.SubjectRelation"/>
    /// for an id in <see cref="LookupEntityQuery.Ids"/>, within <see cref="LookupEntityQuery.Scope"/> when set.
    /// </summary>
    Relations,

    /// <summary><see cref="Relations"/> for every relation in <see cref="LookupEntityQuery.Names"/> at once.</summary>
    RelationsMulti,

    /// <summary>
    /// Tuples of <see cref="LookupEntityQuery.Relation"/> on <see cref="LookupEntityQuery.EntityType"/> whose
    /// subject is <see cref="LookupEntityQuery.SubEntityType"/>:X#<see cref="LookupEntityQuery.SubjectRelation"/>
    /// for an X that has <see cref="LookupEntityQuery.SubRelation"/> to the direct subject
    /// <see cref="LookupEntityQuery.SubjectType"/>:id for the single id in <see cref="LookupEntityQuery.Ids"/>, within <see cref="LookupEntityQuery.Scope"/> when set.
    /// </summary>
    Joined,

    /// <summary>
    /// Values of the attributes in <see cref="LookupEntityQuery.Names"/> of every
    /// <see cref="LookupEntityQuery.EntityType"/> entity, within <see cref="LookupEntityQuery.Scope"/> when set,
    /// keyed by (attribute, entity id).
    /// </summary>
    Attributes,

    /// <summary>
    /// Values of the attributes in <see cref="LookupEntityQuery.Names"/> of the
    /// <see cref="LookupEntityQuery.EntityType"/> entities in <see cref="LookupEntityQuery.Ids"/>, keyed by
    /// (attribute, entity id).
    /// </summary>
    AttributesWithIds,

    /// <summary>Values of the single attribute <c>Names[0]</c> of every <see cref="LookupEntityQuery.EntityType"/> entity.</summary>
    Attribute,

    /// <summary>Every <see cref="LookupEntityQuery.EntityType"/> entity id except <see cref="LookupEntityQuery.Ids"/>.</summary>
    EntitiesExcluding,

    /// <summary>
    /// Entities of <see cref="LookupEntityQuery.EntityType"/> reached from <see cref="LookupEntityQuery.Ids"/> in
    /// up to <see cref="LookupEntityQuery.Levels"/> hops through <see cref="LookupEntityQuery.Relation"/>: a level's
    /// entities are those whose tuples of the relation name <c>EntityType:x#Relation</c> for an x of the level
    /// before, and <see cref="LookupEntityQuery.LevelOneIds"/> join the first level. One row per entity; its
    /// relation is <see cref="LookupEntityQuery.Relation"/> when the first level reaching it is the last, and
    /// empty otherwise.
    /// </summary>
    RelationsLevels,

    /// <summary>
    /// <see cref="RelationsLevels"/> started from the direct subject: level 0 is the
    /// <see cref="LookupEntityQuery.EntityType"/> entities whose tuples of <see cref="LookupEntityQuery.Relation"/>
    /// name <see cref="LookupEntityQuery.SubjectType"/>:id with no subject relation, for the single id in
    /// <see cref="LookupEntityQuery.Ids"/>, and each of the <see cref="LookupEntityQuery.Levels"/> levels after it
    /// follows the level before as <see cref="RelationsLevels"/> does. One row per entity; its relation is
    /// <see cref="LookupEntityQuery.Relation"/> when the first level reaching it is the last, and empty otherwise.
    /// </summary>
    RelationsLevelsFromSubject,
}

/// <summary>
/// One data query a LookupEntity graph waits on, described as data. A query runner decides how it
/// runs and against which snap token, then stores the provider's answer in <see cref="Result"/>.
/// </summary>
public sealed class LookupEntityQuery
{
    public required LookupEntityQueryKind Kind { get; init; }
    public string EntityType { get; init; } = "";
    public string Relation { get; init; } = "";
    public string[] Names { get; init; } = [];
    public string SubjectType { get; init; } = "";

    /// <summary>The relation the read tuples' subjects carry; empty for a direct subject.</summary>
    public string SubjectRelation { get; init; } = "";

    public string SubEntityType { get; init; } = "";

    /// <summary>For <see cref="LookupEntityQueryKind.Joined"/>, the relation on <see cref="SubEntityType"/> whose direct subjects are matched.</summary>
    public string SubRelation { get; init; } = "";

    /// <summary>
    /// The ids the query reads for. A runner reads them only while running the step; the engine may
    /// reuse the collection once the step's answers have been read.
    /// </summary>
    public IReadOnlyCollection<string> Ids { get; init; } = [];
    public EntityScope? Scope { get; init; }

    /// <summary>
    /// Hops a <see cref="LookupEntityQueryKind.RelationsLevels"/> query follows, 2 or more; hops a
    /// <see cref="LookupEntityQueryKind.RelationsLevelsFromSubject"/> query follows after level 0, 1 or more.
    /// </summary>
    public int Levels { get; init; }

    /// <summary>
    /// For <see cref="LookupEntityQueryKind.RelationsLevels"/>, ids that join its first level. A runner reads
    /// them only while running the step, as it does <see cref="Ids"/>.
    /// </summary>
    public IReadOnlyCollection<string> LevelOneIds { get; init; } = [];

    /// <summary>
    /// The answer: <c>List&lt;RelationTuple&gt;</c> for <see cref="LookupEntityQueryKind.Relations"/>,
    /// <see cref="LookupEntityQueryKind.RelationsMulti"/>, <see cref="LookupEntityQueryKind.Joined"/>,
    /// <see cref="LookupEntityQueryKind.RelationsLevels"/> and <see cref="LookupEntityQueryKind.RelationsLevelsFromSubject"/>;
    /// <c>Dictionary&lt;(string AttributeName, string EntityId), AttributeTuple&gt;</c> for
    /// <see cref="LookupEntityQueryKind.Attributes"/> and <see cref="LookupEntityQueryKind.AttributesWithIds"/>;
    /// <c>List&lt;AttributeTuple&gt;</c> for <see cref="LookupEntityQueryKind.Attribute"/>;
    /// <c>List&lt;string&gt;</c> for <see cref="LookupEntityQueryKind.EntitiesExcluding"/>.
    /// A <c>List&lt;RelationTuple&gt;</c> answer passes to the engine, which returns it to the shared
    /// list pool once the lookup has read it; a runner keeps no reference to it.
    /// </summary>
    public object? Result { get; set; }

    // Set once the query is handed to a step, so a query several nodes share runs once.
    internal bool Queued { get; set; }

    // The relation names as one key, computed once per template and shared by every request.
    internal string NamesKey => _namesKey ??= string.Join('\0', Names);
    private string? _namesKey;

    // A plan keeps a query's shape without ids or scope; each request binds its own (and, for
    // RelationsLevels, the levels its depth allows and the ids joining level one) to a fresh copy.
    internal LookupEntityQuery For(IReadOnlyCollection<string> ids, EntityScope? scope, int? levels = null,
        IReadOnlyCollection<string>? levelOneIds = null) => new()
    {
        Kind = Kind, EntityType = EntityType, Relation = Relation, Names = Names, SubjectType = SubjectType,
        SubjectRelation = SubjectRelation, SubEntityType = SubEntityType, SubRelation = SubRelation, Ids = ids,
        Scope = scope, Levels = levels ?? Levels, LevelOneIds = levelOneIds ?? LevelOneIds,
    };
}
