using Weasel.MySql;
using Weasel.MySql.Tables;

namespace Valtuutus.Data.MySql.Weasel;

/// <summary>
/// The Valtuutus tables as Weasel models, matching the provider's migration scripts so an
/// installation made from either diffs clean against the other.
/// </summary>
internal static class MySqlSchema
{
    private const string TxId = "CHAR(26) CHARACTER SET ascii COLLATE ascii_bin";

    /// <param name="database">The database holding the tables; the options' schema, or the connection's database when that is empty.</param>
    public static Table[] Tables(ValtuutusMySqlOptions options, string database) =>
        [Transactions(options, database), Relations(options, database), Attributes(options, database)];

    private static T Configure<T>(T table) where T : Table
    {
        table.Engine = "InnoDB";
        table.Charset = "utf8mb4";
        table.Collation = "utf8mb4_bin";
        table.AddOnlyMigrations = true;
        return table;
    }

    private static Table Transactions(ValtuutusMySqlOptions options, string database)
    {
        var t = Configure(new Table(new MySqlObjectName(database, options.TransactionsTableName)));
        t.AddColumn("id", TxId).AsPrimaryKey();
        t.AddColumn("created_at", "DATETIME(6)").NotNull();
        t.Indexes.Add(new IndexDefinition("idx_created_at") { Columns = ["created_at"] });
        return t;
    }

    private static Table Relations(ValtuutusMySqlOptions options, string database)
    {
        var t = Configure(new Table(new MySqlObjectName(database, options.RelationsTableName)));
        t.AddColumn("id", "BIGINT").AsPrimaryKey().AutoIncrement();
        t.AddColumn("entity_type", "VARBINARY(256)").NotNull();
        t.AddColumn("entity_id", "VARBINARY(256)").NotNull();
        t.AddColumn("relation", "VARBINARY(64)").NotNull();
        t.AddColumn("subject_type", "VARBINARY(256)").NotNull();
        t.AddColumn("subject_id", "VARBINARY(256)").NotNull();
        t.AddColumn("subject_relation", "VARBINARY(64)").NotNull();
        t.AddColumn("created_tx_id", TxId).NotNull();
        t.AddColumn("deleted_tx_id", TxId).AllowNulls();
        t.Indexes.Add(new IndexDefinition("idx_tuples_entity") { Columns = ["entity_type", "entity_id", "relation", "subject_relation", "subject_id", "subject_type", "created_tx_id", "deleted_tx_id"] });
        t.Indexes.Add(new IndexDefinition("idx_tuples_subject") { Columns = ["entity_type", "relation", "subject_type", "subject_id", "subject_relation", "entity_id", "created_tx_id", "deleted_tx_id"] });
        return t;
    }

    private static Table Attributes(ValtuutusMySqlOptions options, string database)
    {
        var t = Configure(new AttributesTable(new MySqlObjectName(database, options.AttributesTableName)));
        t.AddColumn("id", "BIGINT").NotNull().AutoIncrement();
        t.AddColumn("entity_type", "VARBINARY(256)").NotNull();
        t.AddColumn("entity_id", "VARBINARY(256)").NotNull();
        t.AddColumn("attribute", "VARBINARY(64)").NotNull();
        t.AddColumn("value", "LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin").NotNull();
        t.AddColumn("created_tx_id", TxId).NotNull();
        t.AddColumn("deleted_tx_id", TxId).AllowNulls();
        // 1 for live rows and NULL for tombstones, so unique_attributes binds only live rows.
        t.AddColumn(new TableColumn("active", "TINYINT") { ComputedExpression = "deleted_tx_id IS NULL OR NULL", ComputedColumnIsStored = false, AllowNulls = true });
        foreach (var column in new[] { "entity_type", "attribute", "entity_id", "id" })
            t.ModifyColumn(column).AsPrimaryKey();
        // Primary key order keeps attribute reads on the clustered index; without an explicit
        // order Weasel follows column declaration order and does not report the difference.
        t.SetPrimaryKeyOrder(["entity_type", "attribute", "entity_id", "id"]);
        t.Indexes.Add(new IndexDefinition(AttributesTable.IdIndexName) { Columns = ["id"] });
        t.Indexes.Add(new IndexDefinition("unique_attributes") { Columns = ["entity_type", "attribute", "entity_id", "active"], IsUnique = true });
        return t;
    }
}
