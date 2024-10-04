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
}