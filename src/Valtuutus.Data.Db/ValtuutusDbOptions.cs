namespace Valtuutus.Data.Db;

public interface IValtuutusDbOptions
{
    public string Schema { get; }
    public string TransactionsTableName { get; }
    public string RelationsTableName { get; }
    public string AttributesTableName { get; }

    /// <summary>
    /// Levels of a userset chain (a relation whose subjects are other entities' relations) a
    /// LookupSubject V2 query reads at once, and hops of a self-recursive relation's walk a
    /// LookupEntity V2 query follows at once; 1 reads one level per step.
    /// </summary>
    public int LookupHopsPerQuery => 1;

    /// <summary>
    /// Tuples of the checked entity a userset or tuple-to-userset check reads before searching from
    /// the subject's memberships instead. An entity with fewer is answered from its own tuples; one
    /// with more (a folder shared with thousands of groups) is answered from the subject's side.
    /// </summary>
    public int TwoHopEntityScanLimit => 32;
}