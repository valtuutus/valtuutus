using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql;

public record ValtuutusMySqlOptions : IValtuutusDbOptions
{
    public ValtuutusMySqlOptions(MySqlServerType serverType)
    {
        ServerType = serverType;
    }

    public ValtuutusMySqlOptions(MySqlServerType serverType,
        string schema,
        string transactionsTableName,
        string relationsTableName,
        string attributesTableName) : this(serverType)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (string.IsNullOrWhiteSpace(transactionsTableName)) throw new ArgumentNullException(nameof(transactionsTableName));
        if (string.IsNullOrWhiteSpace(relationsTableName)) throw new ArgumentNullException(nameof(relationsTableName));
        if (string.IsNullOrWhiteSpace(attributesTableName)) throw new ArgumentNullException(nameof(attributesTableName));
        Schema = schema;
        TransactionsTableName = transactionsTableName;
        RelationsTableName = relationsTableName;
        AttributesTableName = attributesTableName;
    }

    public MySqlServerType ServerType { get; }

    /// <summary>
    /// The database holding the tables. Empty uses the connection's own database, with unqualified
    /// table names.
    /// </summary>
    public string Schema { get; private set; } = "";
    public string TransactionsTableName { get; private set; } = "transactions";
    public string RelationsTableName { get; private set; } = "relation_tuples";
    public string AttributesTableName { get; private set; } = "attributes";

    /// <summary>
    /// Levels of a userset chain one LookupSubject query reads, and hops of a self-recursive
    /// relation's walk one LookupEntity query follows; 1 reads one level per round trip. The
    /// statement stops at the first level that adds nothing new, so a chain shallower than this
    /// costs no extra levels.
    /// </summary>
    public int LookupHopsPerQuery { get; init; } = 12;
}
