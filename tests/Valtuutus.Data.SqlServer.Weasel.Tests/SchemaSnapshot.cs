using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer.Weasel.Tests;

public static class SchemaSnapshot
{
    public static async Task<IReadOnlyList<string>> TakeAsync(SqlConnection connection, string schema)
    {
        var rows = new List<string>();
        await using (var c = new SqlCommand("""
            SELECT t.name, c.name, ty.name, c.max_length, c.is_nullable, c.is_identity, ISNULL(c.collation_name, '')
            FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.schema_id = SCHEMA_ID(@s) ORDER BY t.name, c.column_id
            """, connection))
        {
            c.Parameters.AddWithValue("s", schema);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add($"col {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)}({r.GetInt16(3)}) null={r.GetBoolean(4)} identity={r.GetBoolean(5)} {r.GetString(6)}");
        }
        await using (var c = new SqlCommand("""
            SELECT t.name, i.name, i.type_desc, i.is_unique, i.is_primary_key, ISNULL(i.filter_definition, ''),
              STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN col.name END, ',') WITHIN GROUP (ORDER BY ic.key_ordinal),
              STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN col.name END, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE t.schema_id = SCHEMA_ID(@s)
            GROUP BY t.name, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.filter_definition
            ORDER BY t.name, i.name
            """, connection))
        {
            c.Parameters.AddWithValue("s", schema);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                // Included columns are a set: sort them so their storage order does not matter.
                var include = r.IsDBNull(7) ? "" : string.Join(",", r.GetString(7).Split(',').Order(StringComparer.Ordinal));
                rows.Add($"idx {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)} unique={r.GetBoolean(3)} pk={r.GetBoolean(4)} keys=({r.GetString(6)}) include=({include}) {r.GetString(5)}");
            }
        }
        return rows;
    }
}
