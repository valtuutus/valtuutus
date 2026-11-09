using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Microsoft.Data.SqlClient;
using Valtuutus.Data.Db;
using System.Data;
using System.Text.Json;

namespace Valtuutus.Data.SqlServer;

public class SqlServerDataWriterProvider : IDbDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly ValtuutusDataOptions _options;
    private readonly IServiceProvider _provider;
    private readonly TimeProvider _timeProvider;

    private static readonly ConcurrentDictionary<DbQueryCacheKey, WriterCommands> CommandCache = new();
    private readonly WriterCommands _c;

    public SqlServerDataWriterProvider(DbConnectionFactory factory,
        ValtuutusDataOptions options,
        IServiceProvider provider,
        IValtuutusDbOptions dbOptions)
    {
        _factory = factory;
        _options = options;
        _provider = provider;
        _timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;
        _c = CommandCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildCommands(key));
    }

    private static WriterCommands BuildCommands(DbQueryCacheKey key)
    {
        return new WriterCommands
        {
            InsertTransaction =
                $"INSERT INTO [{key.Schema}].[{key.TransactionsTable}] (id, created_at) VALUES (@id, @created_at)",
            // A write frees its slot once it committed; a caller-owned write never does, and the next
            // claim reuses its slot once its lock is free.
            ReleaseSlot = $"UPDATE [{key.Schema}].[{key.TransactionsTable}_pending] SET lower_bound = NULL WHERE slot = @slot AND lower_bound = @lower_bound OPTION (KEEPFIXED PLAN)",
            // Claims a free slot, or one whose write ended (its lock is free), or adds one, each in
            // its own committed statement, then reads the newest committed id: the write's lock was
            // taken before, so a reader that saw the slot unheld settled no higher than this.
            Register = $"""
                DECLARE @claimed TABLE (slot int);
                UPDATE TOP (1) s SET lower_bound = @lower_bound, claimed_at = SYSUTCDATETIME()
                OUTPUT inserted.slot INTO @claimed
                FROM [{key.Schema}].[{key.TransactionsTable}_pending] s WITH (READPAST, ROWLOCK)
                WHERE s.lower_bound IS NULL
                   OR APPLOCK_TEST('public', N'vt_pending_' + s.lower_bound, 'Exclusive', 'Session') = 1
                OPTION (KEEPFIXED PLAN);
                IF NOT EXISTS (SELECT 1 FROM @claimed)
                    INSERT INTO [{key.Schema}].[{key.TransactionsTable}_pending] (lower_bound, claimed_at) OUTPUT inserted.slot INTO @claimed VALUES (@lower_bound, SYSUTCDATETIME());
                SELECT slot FROM @claimed;
                SELECT TOP 1 id FROM [{key.Schema}].[{key.TransactionsTable}] WITH (READCOMMITTEDLOCK, READPAST) ORDER BY id DESC;
                """,
            HoldPending = $"""
                DECLARE @held int;
                EXEC @held = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
                IF @held < 0 THROW 50000, 'The pending transaction lock was not granted.', 1;
                """,
            RelationsDestinationTableName = $"[{key.Schema}].[{key.RelationsTable}]",
            MergeAttributes = $"""
                               MERGE INTO [{key.Schema}].[{key.AttributesTable}] AS target
                               USING #temp_attributes AS source
                               ON (target.entity_type = source.entity_type
                                   AND target.entity_id = source.entity_id
                                   AND target.attribute = source.attribute)
                               WHEN MATCHED AND target.deleted_tx_id IS NULL THEN
                                   UPDATE SET target.deleted_tx_id = source.created_tx_id;

                               INSERT INTO [{key.Schema}].[{key.AttributesTable}] (entity_type, entity_id, attribute, value, created_tx_id)
                               SELECT source.entity_type, source.entity_id, source.attribute, source.value, source.created_tx_id
                               FROM #temp_attributes AS source;

                               DROP TABLE #temp_attributes;
                               """,
            DeleteRelations = Enumerable.Range(0, RelationFilterShape.Count)
                .Select(shape => BuildDeleteRelations(key, shape)).ToArray(),
            DeleteAttributes = $"""
                UPDATE a
                SET deleted_tx_id = @SnapToken
                FROM [{key.Schema}].[{key.AttributesTable}] a
                CROSS APPLY OPENJSON(@Filters) WITH (
                    entity_type NVARCHAR(256) '$.entity_type',
                    entity_id NVARCHAR(64) '$.entity_id',
                    attribute NVARCHAR(64) '$.attribute'
                ) AS f
                WHERE a.deleted_tx_id IS NULL
                  AND a.entity_type = f.entity_type
                  AND a.entity_id = f.entity_id
                  AND (f.attribute IS NULL OR a.attribute = f.attribute)
                """,
        };
    }

    private static readonly Dictionary<string, string> RelationFilterTypes = new()
    {
        ["entity_type"] = "NVARCHAR(256)", ["entity_id"] = "NVARCHAR(64)", ["relation"] = "NVARCHAR(64)",
        ["subject_type"] = "NVARCHAR(256)", ["subject_id"] = "NVARCHAR(64)", ["subject_relation"] = "NVARCHAR(64)",
    };

    // Only the set fields of a shape are compared, each by equality, so the statement can use an index.
    // A shape with no field set matches every live tuple and reads no filters.
    private static string BuildDeleteRelations(DbQueryCacheKey key, int shape)
    {
        if (shape == 0)
            return $"UPDATE [{key.Schema}].[{key.RelationsTable}] SET deleted_tx_id = @SnapToken WHERE deleted_tx_id IS NULL";

        var columns = RelationFilterShape.SetColumns(shape).ToArray();
        return $"""
            UPDATE r
            SET deleted_tx_id = @SnapToken
            FROM [{key.Schema}].[{key.RelationsTable}] r
            CROSS APPLY OPENJSON(@Filters) WITH (
                {string.Join(",\n    ", columns.Select(c => $"{c} {RelationFilterTypes[c]} '$.{c}'"))}
            ) AS f
            WHERE r.deleted_tx_id IS NULL
              AND {string.Join("\n  AND ", columns.Select(c => $"r.{c} = f.{c}"))}
            """;
    }

    private sealed record WriterCommands
    {
        public required string InsertTransaction { get; init; }
        public required string Register { get; init; }
        public required string HoldPending { get; init; }
        public required string ReleaseSlot { get; init; }
        public required string RelationsDestinationTableName { get; init; }
        public required string MergeAttributes { get; init; }
        public required string[] DeleteRelations { get; init; }
        public required string DeleteAttributes { get; init; }
    }

    public async Task<SnapToken> Write(
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        await using var db = (SqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Write(db, relations, attributes, ct);
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        using var span = ValtuutusTelemetry.StartWrite(SqlServerDataReaderProvider.DbSystem);
        try
        {
            var transaction = (SqlTransaction)await ((SqlConnection)connection).BeginTransactionAsync(ct);
            var (snapToken, slot) = await WriteCore(connection, transaction, span.CountRelations(relations), span.CountAttributes(attributes), ct);

            await transaction.CommitAsync(ct);
            await ReleaseSlot((SqlConnection)connection, slot, ct);
            span.SetSnapToken(snapToken);
            await NotifyCommitted(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    )
    {
        using var span = ValtuutusTelemetry.StartWrite(SqlServerDataReaderProvider.DbSystem);
        try
        {
            var (snapToken, _) = await WriteCore(connection, transaction, span.CountRelations(relations), span.CountAttributes(attributes), ct);
            span.SetSnapToken(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private async Task<(SnapToken, (int, string))> WriteCore(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        var now = _timeProvider.GetUtcNow();
        var (transactionId, slot, lowerBound) = await Register((SqlConnection)connection, (SqlTransaction)transaction, ct);
        await InsertTransaction((SqlConnection)connection, transactionId, now, (SqlTransaction)transaction, ct);

        await WriteRelationsAsync((SqlConnection)connection, (SqlTransaction)transaction, transactionId, relations, ct);
        await WriteAttributesAsync((SqlConnection)connection, (SqlTransaction)transaction, transactionId, attributes, ct);

        return (new SnapToken(transactionId.ToString()), (slot, lowerBound));
    }

    protected virtual async Task WriteRelationsAsync(SqlConnection connection, SqlTransaction transaction, Ulid transactId, IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        using var relationsBulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
        relationsBulkCopy.DestinationTableName = _c.RelationsDestinationTableName;

        await using var relationsReader = new RelationTupleDataReader(relations, transactId.ToString());
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Relation", "relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectType", "subject_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectId", "subject_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectRelation", "subject_relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));

        await relationsBulkCopy.WriteToServerAsync(relationsReader, ct);
    }

    protected virtual async Task WriteAttributesAsync(SqlConnection connection, SqlTransaction transaction, Ulid transactId, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        await using (var createTempTableCommand = connection.CreateCommand())
        {
            createTempTableCommand.Transaction = transaction;
            createTempTableCommand.CommandText =
                "CREATE TABLE #temp_attributes (entity_type NVARCHAR(256), entity_id NVARCHAR(64), attribute NVARCHAR(64), value NVARCHAR(256), created_tx_id CHAR(26))";
            await createTempTableCommand.ExecuteNonQueryAsync(ct);
        }

        using var attributesBulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
        attributesBulkCopy.DestinationTableName = "#temp_attributes";

        await using var attributesReader = new AttributeTupleDataReader(attributes, transactId.ToString());
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Attribute", "attribute"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Value", "value"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));

        await attributesBulkCopy.WriteToServerAsync(attributesReader, ct);

        await using var mergeCommand = connection.CreateCommand();
        mergeCommand.Transaction = transaction;
        mergeCommand.CommandText = _c.MergeAttributes;
        await mergeCommand.ExecuteNonQueryAsync(ct);
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        await using var db = (SqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Delete(db, filter, ct);
    }

    public async Task<SnapToken> Delete(IDbConnection connection, DeleteFilter filter, CancellationToken ct)
    {
        using var span = ValtuutusTelemetry.StartWrite(SqlServerDataReaderProvider.DbSystem);
        try
        {
            var transaction = (SqlTransaction)await ((SqlConnection)connection).BeginTransactionAsync(ct);
            var (snapToken, slot) = await DeleteCore(connection, transaction, filter, ct);

            await transaction.CommitAsync(ct);
            await ReleaseSlot((SqlConnection)connection, slot, ct);
            span.SetSnapToken(snapToken);
            await NotifyCommitted(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    public async Task<SnapToken> Delete(IDbConnection connection, IDbTransaction transaction, DeleteFilter filter, CancellationToken ct)
    {
        using var span = ValtuutusTelemetry.StartWrite(SqlServerDataReaderProvider.DbSystem);
        try
        {
            var (snapToken, _) = await DeleteCore(connection, transaction, filter, ct);
            span.SetSnapToken(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private async Task<(SnapToken, (int, string))> DeleteCore(IDbConnection connection, IDbTransaction transaction, DeleteFilter filter, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var (transactId, slot, lowerBound) = await Register((SqlConnection)connection, (SqlTransaction)transaction, ct);
        var sqlConnection = (SqlConnection)connection;
        var sqlTransaction = (SqlTransaction)transaction;
        await InsertTransaction(sqlConnection, transactId, now, sqlTransaction, ct);

        var snapTokenValue = transactId.ToString();

        foreach (var group in filter.Relations.GroupBy(static f => RelationFilterShape.Of(f)))
        {
            var filtersJson = JsonSerializer.Serialize(group.ToArray(), DeleteFilterJsonContext.Default.DeleteRelationsFilterArray);
            await ExecuteDeleteBatch(sqlConnection, sqlTransaction, _c.DeleteRelations[group.Key], snapTokenValue, filtersJson, ct);
        }

        if (filter.Attributes.Length > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filter.Attributes, DeleteFilterJsonContext.Default.DeleteAttributesFilterArray);
            await ExecuteDeleteBatch(sqlConnection, sqlTransaction, _c.DeleteAttributes, snapTokenValue, filtersJson, ct);
        }

        return (new SnapToken(snapTokenValue), (slot, lowerBound));
    }

    /// <inheritdoc />
    public Task NotifyCommitted(SnapToken snapToken)
        => _options.NotifyDataWritten(_provider, snapToken);

    private static async Task ExecuteDeleteBatch(SqlConnection connection, SqlTransaction transaction,
        string sql, string snapToken, string filtersJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@SnapToken", SqlDbType.NChar, 26) { Value = snapToken });
        command.Parameters.Add(new SqlParameter("@Filters", SqlDbType.NVarChar, -1) { Value = filtersJson });
        await command.ExecuteNonQueryAsync(ct);
    }

    // Registers the write as in flight from its id up: the write's transaction takes an app lock named
    // after the id, then a slot holding the id is claimed on a connection of its own, committed at
    // once so readers see it. Readers count a slot only while its lock is held (or it was just
    // claimed). The newest committed id is read after the claim; a write whose id isn't above it
    // takes one that is, since a reader may already have settled there.
    private async Task<(Ulid Id, int Slot, string LowerBound)> Register(SqlConnection db, SqlTransaction transaction, CancellationToken ct)
    {
        var lowerBound = MonotonicUlid.Next(_timeProvider);
        var lowerBoundValue = lowerBound.ToString();

        // The app lock is held before the slot is claimed, so a claimed slot whose lock is free
        // belongs to a write that already ended and can be reused at once.
        await using (var hold = db.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = _c.HoldPending;
            hold.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = "vt_pending_" + lowerBoundValue });
            await hold.ExecuteNonQueryAsync(ct);
        }

        int slot;
        string? newest = null;
        await using (var side = (SqlConnection)_factory())
        {
            await side.OpenAsync(ct);
            await using var register = side.CreateCommand();
            register.CommandText = _c.Register;
            register.Parameters.Add(new SqlParameter("@lower_bound", SqlDbType.NChar, 26) { Value = lowerBoundValue });
            await using var reader = await register.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            slot = reader.GetInt32(0);
            if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct) && !reader.IsDBNull(0)) newest = reader.GetString(0);
        }

        var id = newest is not null && string.CompareOrdinal(newest, lowerBoundValue) >= 0
            ? MonotonicUlid.NextAfter(_timeProvider, Ulid.Parse(newest))
            : lowerBound;
        return (id, slot, lowerBoundValue);
    }

    // Releases the slot in its own short statement once the write committed. A caller-owned write
    // never releases its slot: once the caller commits, its app lock is gone, readers ignore the slot,
    // and the next claim reuses it.
    private async Task ReleaseSlot(SqlConnection db, (int Slot, string LowerBound) slot, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = _c.ReleaseSlot;
        command.Parameters.Add(new SqlParameter("@slot", SqlDbType.Int) { Value = slot.Slot });
        command.Parameters.Add(new SqlParameter("@lower_bound", SqlDbType.NChar, 26) { Value = slot.LowerBound });
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertTransaction(SqlConnection db, Ulid transactId, DateTimeOffset createdAt,
        SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _c.InsertTransaction;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NChar, 26) { Value = transactId.ToString() });
        command.Parameters.Add(new SqlParameter("@created_at", SqlDbType.DateTimeOffset) { Value = createdAt });
        await command.ExecuteNonQueryAsync(ct);
    }
}
