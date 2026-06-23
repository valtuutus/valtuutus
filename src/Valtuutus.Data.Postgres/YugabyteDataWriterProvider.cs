using System.Data;
using Dapper;
using Npgsql;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// YugabyteDB speaks the PostgreSQL wire protocol, so it reuses the entire Postgres reader and most of the
/// writer. It does NOT, however, support binary <c>COPY ... (FORMAT BINARY)</c> or <c>MERGE</c> — both raise
/// SQLSTATE <c>0A000 "This statement not supported yet"</c>. This provider overrides only those two write
/// steps with plain <c>INSERT</c>/<c>UPDATE</c>; everything else (transaction handling, the snap-token model,
/// soft-deletes, and all reads) is inherited unchanged, so the stored rows stay byte-identical and the
/// Postgres reader works as-is.
/// </summary>
internal sealed class YugabyteDataWriterProvider : PostgresDataWriterProvider
{
    private readonly string _insertRelation;
    private readonly string _softDeleteAttribute;
    private readonly string _insertAttribute;

    public YugabyteDataWriterProvider(
        DbConnectionFactory factory,
        IServiceProvider provider,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions)
        : base(factory, provider, options, dbOptions)
    {
        var key = DbQueryCacheKey.From(dbOptions);
        _insertRelation =
            $"INSERT INTO {key.Schema}.{key.RelationsTable} (entity_type, entity_id, relation, subject_type, subject_id, subject_relation, created_tx_id) " +
            "VALUES (@EntityType, @EntityId, @Relation, @SubjectType, @SubjectId, @SubjectRelation, @CreatedTxId)";
        // Latest-value-wins upsert without MERGE: retire the current live row (the `unique_attributes` partial
        // index permits one live row per key), then insert the new value under this transaction id.
        _softDeleteAttribute =
            $"UPDATE {key.Schema}.{key.AttributesTable} SET deleted_tx_id = @CreatedTxId " +
            "WHERE entity_type = @EntityType AND entity_id = @EntityId AND attribute = @Attribute AND deleted_tx_id IS NULL";
        _insertAttribute =
            $"INSERT INTO {key.Schema}.{key.AttributesTable} (entity_type, entity_id, attribute, value, created_tx_id) " +
            "VALUES (@EntityType, @EntityId, @Attribute, CAST(@Value AS jsonb), @CreatedTxId)";
    }

    protected override async Task WriteRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<RelationTuple> relations,
        Ulid transactId,
        CancellationToken ct)
    {
        // Fixed-length CHAR(26) so it matches the column and the reader's lexical snap-token comparisons.
        var createdTxId = new DbString { Length = 26, Value = transactId.ToString(), IsFixedLength = true };
        var rows = relations.Select(record => new
        {
            record.EntityType,
            record.EntityId,
            record.Relation,
            record.SubjectType,
            record.SubjectId,
            record.SubjectRelation,
            CreatedTxId = createdTxId,
        }).ToList();
        if (rows.Count == 0)
            return;

        // Dapper runs the parameterized command once per element of the list.
        await connection.ExecuteAsync(new CommandDefinition(
            _insertRelation, rows, transaction, cancellationToken: ct));
    }

    protected override async Task WriteAttributesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IEnumerable<AttributeTuple> attributes,
        Ulid transactId,
        CancellationToken ct)
    {
        var createdTxId = new DbString { Length = 26, Value = transactId.ToString(), IsFixedLength = true };
        var rows = attributes.Select(record => new
        {
            record.EntityType,
            record.EntityId,
            record.Attribute,
            Value = record.Value.ToJsonString(),
            CreatedTxId = createdTxId,
        }).ToList();
        if (rows.Count == 0)
            return;

        await connection.ExecuteAsync(new CommandDefinition(
            _softDeleteAttribute, rows, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            _insertAttribute, rows, transaction, cancellationToken: ct));
    }
}
