using Weasel.SqlServer;
using Weasel.SqlServer.Tables;

namespace Valtuutus.Data.SqlServer.Weasel;

/// <summary>
/// The Valtuutus tables as Weasel models, matching the provider's migration scripts so an
/// installation made from either diffs clean against the other.
/// </summary>
internal static class SqlServerSchema
{
    // Weasel does not compare collations, so the scripts' collation is declared in the type for
    // fresh installs on databases whose default differs.
    private static string N(int length) => $"nvarchar({length}) COLLATE SQL_Latin1_General_CP1_CI_AS";
    private const string TxId = "nchar(26)";

    public static Table[] Tables(ValtuutusSqlServerOptions options) =>
        [Attributes(options), Relations(options), Transactions(options), PendingTransactions(options)];

    private static Table NewTable(ValtuutusSqlServerOptions options, string name) =>
        new(new SqlServerObjectName(options.Schema, name)) { PrimaryKeyName = $"PK_{name}", AddOnlyMigrations = true };

    private static void Index(Table t, string name, string[] columns, string[]? include = null, string? where = null, bool unique = false) =>
        t.Indexes.Add(new IndexDefinition(name) { Columns = columns, IncludedColumns = include ?? [], Predicate = where!, IsUnique = unique });

    private static Table Attributes(ValtuutusSqlServerOptions options)
    {
        var t = NewTable(options, options.AttributesTableName);
        t.AddColumn("id", "bigint").AsPrimaryKey().AutoIncrement();
        t.AddColumn("entity_type", N(256)).NotNull();
        t.AddColumn("entity_id", N(64)).NotNull();
        t.AddColumn("attribute", N(64)).NotNull();
        t.AddColumn("value", N(256)).NotNull();
        t.AddColumn("created_tx_id", TxId).NotNull();
        t.AddColumn("deleted_tx_id", TxId).AllowNulls();
        Index(t, "idx_attributes_entity_id_entity_type_attribute", ["entity_id", "entity_type", "attribute"], ["value"]);
        Index(t, "idx_attributes_attribute_entity_type_entity_id", ["attribute", "entity_type"], ["entity_id", "value", "created_tx_id", "deleted_tx_id"]);
        Index(t, "IX_UniqueAttribute", ["entity_id", "entity_type", "attribute"], where: "deleted_tx_id IS NULL", unique: true);
        return t;
    }

    private static Table Relations(ValtuutusSqlServerOptions options)
    {
        var t = NewTable(options, options.RelationsTableName);
        t.AddColumn("id", "bigint").AsPrimaryKey().AutoIncrement();
        t.AddColumn("entity_type", N(256)).NotNull();
        t.AddColumn("entity_id", N(64)).NotNull();
        t.AddColumn("relation", N(64)).NotNull();
        t.AddColumn("subject_type", N(256)).NotNull();
        t.AddColumn("subject_id", N(64)).NotNull();
        t.AddColumn("subject_relation", N(64)).AllowNulls();
        t.AddColumn("created_tx_id", TxId).NotNull();
        t.AddColumn("deleted_tx_id", TxId).AllowNulls();
        Index(t, "idx_relation_tuples_entity_type_relation_subject_type_subject_id", ["entity_type", "relation", "subject_type", "subject_id"]);
        Index(t, "idx_relation_tuples_entity_type_entity_id_relation", ["entity_type", "entity_id", "relation"]);
        Index(t, "idx_relation_tuples_relation_subject_type_subject_id_entity_type", ["relation", "subject_type", "subject_id", "entity_type"], ["id", "entity_id", "subject_relation"]);
        Index(t, "idx_relation_tuples_relation_entity_type_entity_id", ["relation", "entity_type", "entity_id"], ["subject_type", "subject_id", "subject_relation"]);
        Index(t, "idx_relation_tuples_entity_type_relation_subject_type_subject_id_id", ["entity_type", "relation", "subject_type", "subject_id", "id"], ["entity_id", "subject_relation"]);
        Index(t, "idx_relationtuples_entitytype_entityid_relation_createdtxid_deletedtxid_id", ["entity_type", "entity_id", "relation", "created_tx_id", "deleted_tx_id", "id"], ["subject_type", "subject_id", "subject_relation"]);
        Index(t, "idx_relationtuples_relation_createdtxid_subjecttype", ["relation", "created_tx_id", "subject_type"], ["id", "entity_type", "entity_id", "subject_id", "subject_relation", "deleted_tx_id"]);
        Index(t, "idx_relationtuples_relation_createdtxid_subjecttype_noinclude", ["relation", "created_tx_id", "subject_type"]);
        Index(t, "idx_relationtuples_subjecttype_subjectid_relation", ["subject_type", "subject_id", "relation"], ["id", "entity_type", "entity_id", "subject_relation", "created_tx_id", "deleted_tx_id"]);
        Index(t, "idx_relation_tuples_direct", ["entity_type", "entity_id", "relation", "subject_id"], ["subject_type", "created_tx_id", "deleted_tx_id"], "[subject_relation] = ''");
        Index(t, "idx_relation_tuples_indirect", ["entity_type", "entity_id", "relation"], ["subject_type", "subject_id", "subject_relation", "created_tx_id", "deleted_tx_id"], "[subject_relation] <> ''");
        return t;
    }

    private static Table PendingTransactions(ValtuutusSqlServerOptions options)
    {
        var t = NewTable(options, $"{options.TransactionsTableName}_pending");
        t.AddColumn("slot", "int").AsPrimaryKey().AutoIncrement();
        t.AddColumn("lower_bound", TxId).AllowNulls();
        t.AddColumn("claimed_at", "datetime2(7)").AllowNulls();
        return t;
    }

    private static Table Transactions(ValtuutusSqlServerOptions options)
    {
        var t = NewTable(options, options.TransactionsTableName);
        t.AddColumn("id", TxId).AsPrimaryKey();
        t.AddColumn("created_at", "datetime2(7)").NotNull();
        return t;
    }
}
