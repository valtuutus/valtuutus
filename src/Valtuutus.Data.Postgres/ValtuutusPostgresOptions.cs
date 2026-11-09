using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

public record ValtuutusPostgresOptions : IValtuutusDbOptions
{
    /// <summary>
    /// The schema holding the tables.
    /// </summary>
    public string Schema { get; init => field = NotBlank(value, nameof(Schema)); } = "public";

    public string TransactionsTableName { get; init => field = NotBlank(value, nameof(TransactionsTableName)); } = "transactions";
    public string RelationsTableName { get; init => field = NotBlank(value, nameof(RelationsTableName)); } = "relation_tuples";
    public string AttributesTableName { get; init => field = NotBlank(value, nameof(AttributesTableName)); } = "attributes";

    public int MaxAutoPrepare { get; init; } = 64;
    public int AutoPrepareMinUsages { get; init; } = 2;

    /// <summary>
    /// Levels of a userset chain one LookupSubject V2 query reads, and hops of a self-recursive
    /// relation's walk one LookupEntity V2 query follows; 1 reads one level per round trip.
    /// </summary>
    public int LookupHopsPerQuery { get; init; } = 12;

    /// <summary>
    /// Tuples of the checked entity a userset or tuple-to-userset check reads before searching from
    /// the subject's memberships instead. An entity with fewer is answered from its own tuples; one
    /// with more (a folder shared with thousands of groups) is answered from the subject's side.
    /// Raise it when ordinary entities hold more tuples per relation than this; lowering it only
    /// pays off when large entities are checked often and their subjects belong to few entities.
    /// </summary>
    public int TwoHopEntityScanLimit { get; init; } = 32;

    private static string NotBlank(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentNullException(name) : value;
}
