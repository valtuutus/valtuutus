using Weasel.Core;
using Weasel.MySql.Tables;

namespace Valtuutus.Data.MySql.Weasel;

/// <summary>
/// The attributes table, whose AUTO_INCREMENT id is last in the primary key. MySQL accepts that
/// only when a key leading with id is declared inside CREATE TABLE, and Weasel declares indexes
/// after it; Weasel also emits no CHECK constraints on MySQL. This writes both inline and leaves
/// the rest of the indexes, and every delta, to Weasel.
/// </summary>
internal sealed class AttributesTable(DbObjectName name) : Table(name)
{
    public const string IdIndexName = "idx_id";
    public const string JsonCheckName = "attributes_value_json";

    public override void WriteCreateStatement(Migrator migrator, TextWriter writer)
    {
        writer.WriteLine($"CREATE TABLE IF NOT EXISTS {Identifier.QualifiedName} (");
        var lines = Columns.Select(c => "    " + Declare(c)).ToList();
        lines.Add($"    PRIMARY KEY ({string.Join(", ", PrimaryKeyColumns.Select(Quote))})");
        var idIndex = Indexes.Single(i => i.Name == IdIndexName);
        lines.Add($"    {(idIndex.IsUnique ? "UNIQUE KEY" : "KEY")} {Quote(IdIndexName)} ({string.Join(", ", idIndex.Columns.Select(Quote))})");
        lines.Add($"    CONSTRAINT {Quote(JsonCheckName)} CHECK (JSON_VALID({Quote("value")}))");
        writer.WriteLine(string.Join("," + Environment.NewLine, lines));
        writer.WriteLine($") ENGINE={Engine} DEFAULT CHARSET={Charset} COLLATE={Collation};");

        foreach (var index in Indexes.Where(i => i.Name != IdIndexName))
        {
            writer.WriteLine();
            writer.WriteLine(index.ToDDL(this));
        }
    }

    // A generated column takes no nullability clause on MariaDB, so it is declared from the model
    // without one; every other column is declared by Weasel.
    private static string Declare(TableColumn column) => column.ComputedExpression is { } expression
        ? $"{Quote(column.Name)} {column.Type} GENERATED ALWAYS AS ({expression}) {(column.ComputedColumnIsStored ? "STORED" : "VIRTUAL")}"
        : column.ToDeclaration();

    private static string Quote(string identifier) => $"`{identifier}`";
}
