using System.Data;
using Authorizee.Core;
using Authorizee.Data.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Authorizee.Data.Postgres.Tests;

public static class InsertExtensions
{
    public static async Task InsertRelations(this DbConnectionFactory factory, RelationTuple[] tuples)
    {
        await using var db = (NpgsqlConnection) factory();
        await db.OpenAsync();
        await using var writer = await db.BeginBinaryImportAsync(
            "copy public.relation_tuples (entity_type, entity_id, relation, subject_type, subject_id, subject_relation) from STDIN (FORMAT BINARY)");
        foreach (var record in tuples)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(record.EntityType);
            await writer.WriteAsync(record.EntityId);
            await writer.WriteAsync(record.Relation);
            await writer.WriteAsync(record.SubjectType);
            await writer.WriteAsync(record.SubjectId);
            await writer.WriteAsync(record.SubjectRelation);
        }

        await writer.CompleteAsync();
    }
    
    public static async Task InsertAttributes(this DbConnectionFactory factory, AttributeTuple[] tuples)
    {
        await using var db = (NpgsqlConnection) factory();        
        await db.OpenAsync();
        await using var writer = await db.BeginBinaryImportAsync(
            "copy public.attributes (entity_type, entity_id, attribute, value) from STDIN (FORMAT BINARY)");
        foreach (var record in tuples)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync(record.EntityType);
            await writer.WriteAsync(record.EntityId);
            await writer.WriteAsync(record.Attribute);
            await writer.WriteAsync(record.Value.ToJsonString(), NpgsqlDbType.Jsonb);
        }

        await writer.CompleteAsync();
    }
}