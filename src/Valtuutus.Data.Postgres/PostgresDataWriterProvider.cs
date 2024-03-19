using System.Diagnostics;
using System.Text.Json;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Configuration;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using IdGen;
using Npgsql;
using NpgsqlTypes;
using Sqids;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataWriterProvider : IDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly IIdGenerator<long> _idGenerator;
    private readonly SqidsEncoder<long> _encoder;

    public PostgresDataWriterProvider(DbConnectionFactory factory, IIdGenerator<long> idGenerator, SqidsEncoder<long> encoder)
    {
        _factory = factory;
        _idGenerator = idGenerator;
        _encoder = encoder;
    }
    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        await using var db = (NpgsqlConnection) _factory();
        await db.OpenAsync(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);

        var transactId = _idGenerator.CreateId();
        
        await InsertTransaction(db, transactId, transaction, ct);
        
        await using var relationsWriter = await db.BeginBinaryImportAsync(
            "copy public.relation_tuples (entity_type, entity_id, relation, subject_type, subject_id, subject_relation, created_tx_id) from STDIN (FORMAT BINARY)", ct);
        foreach (var record in relations)
        {
            await relationsWriter.StartRowAsync(ct);
            await relationsWriter.WriteAsync(record.EntityType, ct);
            await relationsWriter.WriteAsync(record.EntityId, ct);
            await relationsWriter.WriteAsync(record.Relation, ct);
            await relationsWriter.WriteAsync(record.SubjectType, ct);
            await relationsWriter.WriteAsync(record.SubjectId, ct);
            await relationsWriter.WriteAsync(record.SubjectRelation, ct);
            await relationsWriter.WriteAsync(transactId, ct);
        }

        await relationsWriter.CompleteAsync(ct);
        await relationsWriter.CloseAsync(ct);
        
        await using var attributesWriter = await db.BeginBinaryImportAsync(
            "copy public.attributes (entity_type, entity_id, attribute, value, created_tx_id) from STDIN (FORMAT BINARY)", ct);
        foreach (var record in attributes)
        {
            await attributesWriter.StartRowAsync(ct);
            await attributesWriter.WriteAsync(record.EntityType, ct);
            await attributesWriter.WriteAsync(record.EntityId, ct);
            await attributesWriter.WriteAsync(record.Attribute, ct);
            await attributesWriter.WriteAsync(record.Value.ToJsonString(), NpgsqlDbType.Jsonb, ct);
            await attributesWriter.WriteAsync(transactId, ct);
        }

        
        await attributesWriter.CompleteAsync(ct);
        await attributesWriter.CloseAsync(ct);


        await transaction.CommitAsync(ct);

        return new SnapToken(_encoder.Encode(transactId));
    }

    private static async Task InsertTransaction(NpgsqlConnection db, long transactId,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO public.transactions (id, created_at) VALUES (@id, @created_at)", new
        {
            id = transactId,
            created_at = DateTimeOffset.UtcNow
        }, transaction: transaction, cancellationToken: ct));
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        var transactId = _idGenerator.CreateId();
        await using var db = (NpgsqlConnection) _factory();
        await db.OpenAsync(ct);
        await using var transaction = await db.BeginTransactionAsync(ct);

        if (filter.Relations.Length > 0)
        {
            var relationsBuilder = new SqlBuilder();
            relationsBuilder = relationsBuilder.FilterDeleteRelations(filter.Relations);
            var queryTemplate = relationsBuilder.AddTemplate(@"DELETE FROM public.relation_tuples /**where**/");

            await db.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
            
        }

        if (filter.Attributes.Length > 0)
        {
            var attributesBuilder = new SqlBuilder();
            attributesBuilder = attributesBuilder.FilterDeleteAttributes(filter.Attributes);
            var queryTemplate = attributesBuilder.AddTemplate(@"DELETE FROM public.attributes /**where**/");

            await db.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
            
        }

        await InsertTransaction(db, transactId, transaction, ct);
        await transaction.CommitAsync(ct);
        return new SnapToken(_encoder.Encode(transactId));
    }
}