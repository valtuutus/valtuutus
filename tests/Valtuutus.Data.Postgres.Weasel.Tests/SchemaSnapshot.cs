using Npgsql;

namespace Valtuutus.Data.Postgres.Weasel.Tests;

public static class SchemaSnapshot
{
    public static async Task<IReadOnlyList<string>> TakeAsync(NpgsqlConnection connection, string schema)
    {
        var rows = new List<string>();
        await using (var c = new NpgsqlCommand("""
            select table_name, column_name, data_type, coalesce(character_maximum_length, -1), is_nullable,
                   is_identity, coalesce(identity_generation, ''), coalesce(column_default, '')
            from information_schema.columns where table_schema = @s order by table_name, ordinal_position
            """, connection))
        {
            c.Parameters.AddWithValue("s", schema);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add($"col {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)}({r.GetInt32(3)}) null={r.GetString(4)} identity={r.GetString(5)}/{r.GetString(6)} default={r.GetString(7)}");
        }
        await using (var c = new NpgsqlCommand("""
            select tablename, indexname, replace(indexdef, schemaname || '.', '') from pg_indexes
            where schemaname = @s order by tablename, indexname
            """, connection))
        {
            c.Parameters.AddWithValue("s", schema);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add($"idx {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)}");
        }
        await using (var c = new NpgsqlCommand("""
            select tc.table_name, tc.constraint_name, tc.constraint_type from information_schema.table_constraints tc
            where tc.table_schema = @s and tc.constraint_type in ('PRIMARY KEY', 'UNIQUE', 'CHECK') and tc.constraint_name not like '%_not_null'
            order by 1, 2
            """, connection))
        {
            c.Parameters.AddWithValue("s", schema);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add($"con {r.GetString(0)}.{r.GetString(1)} {r.GetString(2)}");
        }
        return rows;
    }
}
