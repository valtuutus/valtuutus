using System.Data;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataWriterProvider : IDataWriterProvider, IDbDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly IServiceProvider _provider;
    private readonly ValtuutusDataOptions _options;

    public PostgresDataWriterProvider(DbConnectionFactory factory, IServiceProvider provider, ValtuutusDataOptions options)
    {
        _factory = factory;
        _provider = provider;
        _options = options;
    }
    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        await using var db = (NpgsqlConnection) _factory();
        await db.OpenAsync(ct);
#if !NETCOREAPP3_0_OR_GREATER
        await using var transaction = db.BeginTransaction();
#else
        await using var transaction = await db.BeginTransactionAsync(ct);
#endif
        
        var snapToken =  await Write(db, transaction, relations, attributes, ct);
        
        await transaction.CommitAsync(ct);
        return snapToken;
    }

    public Task<SnapToken> WriteRelations(IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SnapToken> WriteAttributes(IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    private static async Task InsertTransaction(IDbConnection db, Ulid transactId,
        IDbTransaction transaction, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO public.transactions (id, created_at) VALUES (@id, @created_at)", new
        {
            id = transactId,
            created_at = DateTimeOffset.UtcNow
        }, transaction: transaction, cancellationToken: ct));
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();
        await using var db = (NpgsqlConnection) _factory();
        await db.OpenAsync(ct);
        
        
#if !NETCOREAPP3_0_OR_GREATER
        await using var transaction = db.BeginTransaction();
#else
        await using var transaction = await db.BeginTransactionAsync(ct);
        
#endif
        
        var snapTokenParam = new
        {
            SnapToken = new DbString
            {
                Length = 26,
                Value = transactId.ToString(),
                IsFixedLength = true
            }

        };
        if (filter.Relations.Length > 0)
        {
            var relationsBuilder = new SqlBuilder();
            relationsBuilder = relationsBuilder.FilterDeleteRelations(filter.Relations);
            var queryTemplate = relationsBuilder.AddTemplate(@"UPDATE public.relation_tuples set deleted_tx_id = @SnapToken /**where**/", snapTokenParam);

            await db.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
            
        }

        if (filter.Attributes.Length > 0)
        {
            var attributesBuilder = new SqlBuilder();
            attributesBuilder = attributesBuilder.FilterDeleteAttributes(filter.Attributes);
            var queryTemplate = attributesBuilder.AddTemplate(@"UPDATE public.attributes set deleted_tx_id = @SnapToken /**where**/",snapTokenParam);

            await db.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
            
        }

        await InsertTransaction(db, transactId, transaction, ct);
        await transaction.CommitAsync(ct);
        var snapToken = new SnapToken(transactId.ToString());    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public Task<SnapToken> Write(IDbConnection connection, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SnapToken> WriteRelations(IDbConnection connection, IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SnapToken> WriteAttributes(IDbConnection connection, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<SnapToken> Delete(IDbConnection connection, DeleteFilter filter, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public async Task<SnapToken> Write(IDbConnection connection, IDbTransaction transaction, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes,
        CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();
        
        await InsertTransaction(connection, transactId, transaction, ct);
        
        await WriteRelationsInternal(connection, relations, transactId, ct);

        await WriteAttributesInternal(connection, attributes, transactId, ct);
        

        var snapToken = new SnapToken(transactId.ToString());    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    private static async Task WriteAttributesInternal(IDbConnection connection, IEnumerable<AttributeTuple> attributes, Ulid transactId,
        CancellationToken ct)
    {
        await using var attributesWriter = await ((NpgsqlConnection)connection).BeginBinaryImportAsync(
            "copy public.attributes (entity_type, entity_id, attribute, value, created_tx_id) from STDIN (FORMAT BINARY)", ct);
        foreach (var record in attributes)
        {
            await attributesWriter.StartRowAsync(ct);
            await attributesWriter.WriteAsync(record.EntityType, ct);
            await attributesWriter.WriteAsync(record.EntityId, ct);
            await attributesWriter.WriteAsync(record.Attribute, ct);
            await attributesWriter.WriteAsync(record.Value.ToJsonString(), NpgsqlDbType.Jsonb, ct);
            await attributesWriter.WriteAsync(transactId.ToString(), NpgsqlDbType.Char, ct);
        }

        
        await attributesWriter.CompleteAsync(ct);
        await attributesWriter.CloseAsync(ct);
    }

    private static async Task WriteRelationsInternal(IDbConnection connection, IEnumerable<RelationTuple> relations, Ulid transactId,
        CancellationToken ct)
    {
        await using var relationsWriter = await ((NpgsqlConnection)connection).BeginBinaryImportAsync(
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
            await relationsWriter.WriteAsync(transactId.ToString(), NpgsqlDbType.Char, ct);
        }

        await relationsWriter.CompleteAsync(ct);
        await relationsWriter.CloseAsync(ct);
    }

    public async Task<SnapToken> WriteRelations(IDbConnection connection, IDbTransaction transaction, IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();
        
        await InsertTransaction(connection, transactId, transaction, ct);
        await WriteRelationsInternal(connection, relations, transactId, ct);
        
        var snapToken = new SnapToken(transactId.ToString());    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public async Task<SnapToken> WriteAttributes(IDbConnection connection, IDbTransaction transaction, IEnumerable<AttributeTuple> attributes,
        CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();
        
        await InsertTransaction(connection, transactId, transaction, ct);
        await WriteAttributesInternal(connection, attributes, transactId, ct);

        var snapToken = new SnapToken(transactId.ToString());    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public async Task<SnapToken> Delete(IDbConnection connection, IDbTransaction transaction, DeleteFilter filter, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();
        
        var snapToken = new SnapToken(transactId.ToString());    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }
}