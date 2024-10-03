using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

public record ValtuutusSqlServerOptions : IValtuutusDbOptions
{
    internal ValtuutusSqlServerOptions()
    {
        
    }
    public ValtuutusSqlServerOptions(string schema,
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
    
    public string Schema { get; } = "dbo";
    public string TransactionsTableName { get; } = "transactions";
    public string RelationsTableName { get; } = "relation_tuples";
    public string AttributesTableName { get; } = "attributes";
    
};