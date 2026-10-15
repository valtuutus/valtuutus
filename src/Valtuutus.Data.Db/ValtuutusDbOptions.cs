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
}