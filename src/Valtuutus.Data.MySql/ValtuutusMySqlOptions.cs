using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql;

public record ValtuutusMySqlOptions : IValtuutusDbOptions
{
    private string _schema = "";
    private string _transactionsTableName = "transactions";
    private string _relationsTableName = "relation_tuples";
    private string _attributesTableName = "attributes";

    public ValtuutusMySqlOptions(MySqlServerType serverType)
    {
        ServerType = serverType;
    }

    public MySqlServerType ServerType { get; }

    /// <summary>
    /// The database holding the tables. Empty uses the connection's own database, with unqualified
    /// table names.
    /// </summary>
    public string Schema { get => _schema; init => _schema = value ?? throw new ArgumentNullException(nameof(Schema)); }

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
    /// Levels of a userset chain one LookupSubject query reads, and hops of a self-recursive
    /// relation's walk one LookupEntity query follows; 1 reads one level per round trip. The
    /// statement stops at the first level that adds nothing new, so a chain shallower than this
    /// costs no extra levels.
    /// </summary>
    public int LookupHopsPerQuery { get; init; } = 12;

    private static string NotBlank(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentNullException(name) : value;
}
