using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace Valtuutus.Data.Postgres.Weasel;

/// <summary>
/// The Valtuutus tables as Weasel models, matching the provider's migration scripts so an
/// installation made from either diffs clean against the other.
/// </summary>
internal static class PostgresSchema
{
    // Weasel reads char(n) back as character(n); declaring it the same way keeps the diff clean.
    private const string TxId = "character(26)";

    public static Table[] Tables(ValtuutusPostgresOptions options) =>
        [Attributes(options), Relations(options), Transactions(options), PendingTransactions(options)];

    private static Table NewTable(ValtuutusPostgresOptions options, string name) =>
        new(new PostgresqlObjectName(options.Schema, name, SchemaUtils.IdentifierUsage.General))
        {
            // Matches Postgres's default constraint name, which the scripts rely on.
            PrimaryKeyName = $"{name}_pkey",
            AddOnlyMigrations = true,
        };

    private static void AddIdentity(Table table)
    {
        var id = new TableColumn("id", "bigint");
        id.ColumnChecks.Add(new AlwaysIdentity());
        table.AddColumn(id).AsPrimaryKey();
    }

    private static Table Attributes(ValtuutusPostgresOptions options)
    {
        var t = NewTable(options, options.AttributesTableName);
        AddIdentity(t);
        t.AddColumn("entity_type", "character varying(256)").NotNull();
        t.AddColumn("entity_id", "character varying(64)").NotNull();
        t.AddColumn("attribute", "character varying(64)").NotNull();
        t.AddColumn("value", "jsonb").NotNull();
        t.AddColumn("created_tx_id", TxId).NotNull();
        t.AddColumn("deleted_tx_id", TxId).AllowNulls();
        t.Indexes.Add(new IndexDefinition("idx_attributes") { Columns = ["entity_type", "entity_id", "attribute"], IncludeColumns = ["value"] });
        t.Indexes.Add(new IndexDefinition("unique_attributes") { Columns = ["entity_type", "entity_id", "attribute"], IsUnique = true, Predicate = "deleted_tx_id is null" });
        // Concurrent so adding it to a large existing table does not block writes.
        t.Indexes.Add(new IndexDefinition("idx_attributes_type_attribute") { Columns = ["entity_type", "attribute", "entity_id"], IncludeColumns = ["value"], IsConcurrent = true });
        return t;
    }

    private static Table Relations(ValtuutusPostgresOptions options)
    {
        var t = NewTable(options, options.RelationsTableName);
        AddIdentity(t);
        t.AddColumn("entity_type", "character varying(256)").NotNull();
        t.AddColumn("entity_id", "character varying(64)").NotNull();
        t.AddColumn("relation", "character varying(64)").NotNull();
        t.AddColumn("subject_type", "character varying(256)").NotNull();
        t.AddColumn("subject_id", "character varying(64)").NotNull();
        t.AddColumn("subject_relation", "character varying(64)").NotNull();
        t.AddColumn("created_tx_id", TxId).NotNull();
        t.AddColumn("deleted_tx_id", TxId).AllowNulls();
        t.Indexes.Add(new IndexDefinition("idx_tuples_entity_relation") { Columns = ["entity_type", "relation"] });
        t.Indexes.Add(new IndexDefinition("idx_tuples_subject_entities") { Columns = ["entity_type", "relation", "subject_type", "subject_id"], IncludeColumns = ["entity_id", "subject_relation"] });
        t.Indexes.Add(new IndexDefinition("idx_tuples_user") { Columns = ["entity_type", "entity_id", "relation", "subject_id"] });
        t.Indexes.Add(new IndexDefinition("idx_tuples_userset") { Columns = ["entity_type", "entity_id", "relation", "subject_type", "subject_relation"] });
        t.Indexes.Add(new IndexDefinition("idx_tuples_direct") { Columns = ["entity_type", "entity_id", "relation", "subject_id"], IncludeColumns = ["subject_type", "created_tx_id", "deleted_tx_id"], Predicate = "subject_relation = ''" });
        t.Indexes.Add(new IndexDefinition("idx_tuples_indirect") { Columns = ["entity_type", "entity_id", "relation"], IncludeColumns = ["subject_type", "subject_id", "subject_relation", "created_tx_id", "deleted_tx_id"], Predicate = "subject_relation <> ''" });
        return t;
    }

    private static Table PendingTransactions(ValtuutusPostgresOptions options)
    {
        var t = NewTable(options, $"{options.TransactionsTableName}_pending");
        AddIdentity(t);
        t.AddColumn("lower_bound", TxId).NotNull();
        t.AddColumn("xid", "xid8").NotNull();
        t.AddColumn("registered_at", "timestamptz").NotNull().DefaultValueByExpression("clock_timestamp()");
        t.Indexes.Add(new IndexDefinition("idx_pending_lower_bound") { Columns = ["lower_bound"] });
        return t;
    }

    private static Table Transactions(ValtuutusPostgresOptions options)
    {
        var t = NewTable(options, options.TransactionsTableName);
        t.AddColumn("id", TxId).AsPrimaryKey();
        t.AddColumn("created_at", "timestamptz").NotNull();
        t.Indexes.Add(new IndexDefinition("idx_created_at") { Columns = ["created_at"] });
        return t;
    }
}
