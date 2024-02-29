using Authorizee.Core;
using Authorizee.Core.Data;
using Authorizee.Data.Configuration;
using Dapper;
using IdGen;
using Npgsql;
using NpgsqlTypes;
using Sqids;

namespace Authorizee.Data.Postgres;

public sealed class PostgresDataWriterProvider : IDataWriterProvider
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
    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes)
    {
        await using var db = (NpgsqlConnection) _factory();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();

        var transactId = _idGenerator.CreateId();
        
        await db.ExecuteAsync("INSERT INTO public.transactions (id, created_at) VALUES (@id, @created_at)", new
        {
            id = transactId,
            created_at = DateTimeOffset.UtcNow
        }, transaction: transaction);
        
        await using var relationsWriter = await db.BeginBinaryImportAsync(
            "copy public.relation_tuples (entity_type, entity_id, relation, subject_type, subject_id, subject_relation) from STDIN (FORMAT BINARY)");
        foreach (var record in relations)
        {
            await relationsWriter.StartRowAsync();
            await relationsWriter.WriteAsync(record.EntityType);
            await relationsWriter.WriteAsync(record.EntityId);
            await relationsWriter.WriteAsync(record.Relation);
            await relationsWriter.WriteAsync(record.SubjectType);
            await relationsWriter.WriteAsync(record.SubjectId);
            await relationsWriter.WriteAsync(record.SubjectRelation);
        }

        await relationsWriter.CompleteAsync();
        await relationsWriter.CloseAsync();
        
        await using var attributesWriter = await db.BeginBinaryImportAsync(
            "copy public.attributes (entity_type, entity_id, attribute, value) from STDIN (FORMAT BINARY)");
        foreach (var record in attributes)
        {
            await attributesWriter.StartRowAsync();
            await attributesWriter.WriteAsync(record.EntityType);
            await attributesWriter.WriteAsync(record.EntityId);
            await attributesWriter.WriteAsync(record.Attribute);
            await attributesWriter.WriteAsync(record.Value.ToJsonString(), NpgsqlDbType.Jsonb);
        }

        
        await attributesWriter.CompleteAsync();
        await attributesWriter.CloseAsync();


        await transaction.CommitAsync();

        return new SnapToken(_encoder.Encode(transactId));
    }

    public Task<SnapToken> Delete(DeleteFilter filter)
    {
        throw new NotImplementedException();
    }

    public async Task<SnapToken> Delete()
    {
        var transactId = _idGenerator.CreateId();
        await using var db = (NpgsqlConnection) _factory();
        await using var transaction = await db.BeginTransactionAsync();

        await transaction.CommitAsync();
        return new SnapToken(_encoder.Encode(transactId));

    }
}