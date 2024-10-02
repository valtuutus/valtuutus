namespace Valtuutus.Data.Db;

public interface IValtuutusDbOptions
{
    public string Schema { get; }
    public string TransactionsTableName { get; }
    public string RelationsTableName { get; }
    public string AttributesTableName { get; }
}