using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

public record ValtuutusPostgresOptions : IValtuutusDbOptions
{
    internal ValtuutusPostgresOptions()
    {
        
    } 
    public ValtuutusPostgresOptions(string schema,
        string transactionsTableName, 
        string relationsTableName,
        string attributesTableName)
    {
        if(string.IsNullOrWhiteSpace(schema)) throw new ArgumentNullException(nameof(schema));
        if(string.IsNullOrWhiteSpace(transactionsTableName)) throw new ArgumentNullException(nameof(transactionsTableName));
        if(string.IsNullOrWhiteSpace(relationsTableName)) throw new ArgumentNullException(nameof(relationsTableName));
        if(string.IsNullOrWhiteSpace(attributesTableName)) throw new ArgumentNullException(nameof(attributesTableName));
        Schema = schema;
        TransactionsTableName = transactionsTableName;
        RelationsTableName = relationsTableName;
        AttributesTableName = attributesTableName;
    }
    public string Schema { get; private set; } = "public";
    public string TransactionsTableName  { get; private set; } = "transactions";
    public string RelationsTableName  { get; private set; } = "relation_tuples";
    public string AttributesTableName  { get; private set; } = "attributes";
    public int MaxAutoPrepare { get; init; } = 64;
    public int AutoPrepareMinUsages { get; init; } = 2;

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
}
