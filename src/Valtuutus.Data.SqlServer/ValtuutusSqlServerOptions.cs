using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

public record ValtuutusSqlServerOptions : IValtuutusDbOptions
{
    private string _schema = "dbo";
    private string _transactionsTableName = "transactions";
    private string _relationsTableName = "relation_tuples";
    private string _attributesTableName = "attributes";

    /// <summary>
    /// The schema holding the tables. Defaults to "dbo"; the tables default to "transactions",
    /// "relation_tuples" and "attributes".
    /// </summary>
    public string Schema { get => _schema; init => _schema = NotBlank(value, nameof(Schema)); }

    public string TransactionsTableName
    {
        get => _transactionsTableName;
        init => _transactionsTableName = NotBlank(value, nameof(TransactionsTableName));
    }

    public string RelationsTableName
    {
        get => _relationsTableName;
        init => _relationsTableName = NotBlank(value, nameof(RelationsTableName));
    }

    public string AttributesTableName
    {
        get => _attributesTableName;
        init => _attributesTableName = NotBlank(value, nameof(AttributesTableName));
    }

    /// <summary>
    /// Levels of a userset chain one LookupSubject V2 query reads, and hops of a self-recursive
    /// relation's walk one LookupEntity V2 query follows; 1 reads one level per round trip.
    /// </summary>
    public int LookupHopsPerQuery { get; init; } = 2;

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
