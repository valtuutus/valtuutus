using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Data.Db;
using System.Data;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Valtuutus.Data.Postgres;

public class PostgresDataWriterProvider : IDbDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly IServiceProvider _provider;
    private readonly ValtuutusDataOptions _options;
    private readonly TimeProvider _timeProvider;

    private sealed record WriterCommands
    {
        public required string CopyRelations { get; init; }
        public required string InsertTransaction { get; init; }
        public required string[] DeleteRelations { get; init; }
        public required string DeleteAttributes { get; init; }
        public required string MergeAttributes { get; init; }
        public required string Settle { get; init; }
        public required string Renumber { get; init; }
        public required string WaitForOlderWrites { get; init; }
        public required string LatestSettled { get; init; }
    }

    private static readonly ConcurrentDictionary<DbQueryCacheKey, WriterCommands> CommandCache = new();
    private readonly WriterCommands _c;
    private readonly PostgresPendingRegistrar _registrar;
    private readonly NpgsqlDataSource _dataSource;

    public PostgresDataWriterProvider(DbConnectionFactory factory,
        IServiceProvider provider,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions)
    {
        _factory = factory;
        _provider = provider;
        _options = options;
        _timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;
        var key = DbQueryCacheKey.From(dbOptions);
        var pgOptions = dbOptions as ValtuutusPostgresOptions ?? new ValtuutusPostgresOptions();
        _c = CommandCache.GetOrAdd(key, key => BuildCommands(key, PostgresDataReaderProvider.GetQueries(pgOptions).GetLatestSnapToken));
        // Same probe-then-cache pattern as the reader: the factory yields the connection string, the
        // shared cache yields the data source, and with it this database's registrar.
        using var probe = (NpgsqlConnection)factory();
        _dataSource = PostgresDataReaderProvider.GetOrCreateDataSource(probe.ConnectionString, pgOptions);
        _registrar = PostgresPendingRegistrar.For(_dataSource, key);
    }

    private static WriterCommands BuildCommands(DbQueryCacheKey key, string latestSettled) => new()
    {
        CopyRelations =
            $"copy {key.Schema}.{key.RelationsTable} (entity_type, entity_id, relation, subject_type, subject_id, subject_relation, created_tx_id) from STDIN (FORMAT BINARY)",
        InsertTransaction =
            // The write holds a lock keyed by its id until it ends, so a committed write can wait for
            // the older writes still open instead of polling them.
            $"INSERT INTO {key.Schema}.{key.TransactionsTable} (id, created_at) VALUES (@id, @created_at) RETURNING pg_current_xact_id()::text, pg_advisory_xact_lock(hashtextextended(@id, 0))",
        WaitForOlderWrites = $"""
            SELECT count(pg_advisory_xact_lock_shared(hashtextextended(p.lower_bound, 0)))
            FROM {PostgresPendingRegistrar.PendingTable(key)} p
            WHERE p.lower_bound < @snap_token AND pg_xact_status(p.xid) = 'in progress'
            """,
        LatestSettled = latestSettled,
        Settle = $"DELETE FROM {PostgresPendingRegistrar.PendingTable(key)} WHERE lower_bound = @lower_bound",
        Renumber = $"""
            UPDATE {key.Schema}.{key.TransactionsTable} SET id = @renumbered WHERE id = @id;
            UPDATE {key.Schema}.{key.RelationsTable} SET created_tx_id = @renumbered WHERE created_tx_id = @id;
            UPDATE {key.Schema}.{key.RelationsTable} SET deleted_tx_id = @renumbered WHERE deleted_tx_id = @id;
            UPDATE {key.Schema}.{key.AttributesTable} SET created_tx_id = @renumbered WHERE created_tx_id = @id;
            UPDATE {key.Schema}.{key.AttributesTable} SET deleted_tx_id = @renumbered WHERE deleted_tx_id = @id;
            """,
        DeleteRelations = Enumerable.Range(0, RelationFilterShape.Count)
            .Select(shape => BuildDeleteRelations(key, shape)).ToArray(),
        DeleteAttributes = $"""
            UPDATE {key.Schema}.{key.AttributesTable} a
            SET deleted_tx_id = @SnapToken
            FROM jsonb_to_recordset(@Filters::jsonb) AS f(entity_type text, entity_id text, attribute text)
            WHERE a.deleted_tx_id IS NULL
              AND a.entity_type = f.entity_type
              AND a.entity_id = f.entity_id
              AND (f.attribute IS NULL OR a.attribute = f.attribute)
            """,
        MergeAttributes = $""""
                           MERGE INTO {key.Schema}.{key.AttributesTable} AS target
                           USING temp_attributes AS source
                           ON (target.entity_type = source.entity_type
                               AND target.entity_id = source.entity_id
                               AND target.attribute = source.attribute)
                           WHEN MATCHED AND target.deleted_tx_id IS NULL THEN
                               UPDATE SET deleted_tx_id = source.created_tx_id;

                           INSERT INTO {key.Schema}.{key.AttributesTable} (entity_type, entity_id, attribute, value, created_tx_id)
                           SELECT entity_type, entity_id, attribute, value, created_tx_id
                           FROM temp_attributes;

                           DROP TABLE temp_attributes;
                           """"
    };

    // Only the set fields of a shape are compared, each by equality, so the statement can use an index.
    // A shape with no field set matches every live tuple and reads no filters.
    private static string BuildDeleteRelations(DbQueryCacheKey key, int shape)
    {
        var head = $"UPDATE {key.Schema}.{key.RelationsTable} r SET deleted_tx_id = @SnapToken";
        if (shape == 0)
            return head + " WHERE r.deleted_tx_id IS NULL";

        var columns = RelationFilterShape.SetColumns(shape).ToArray();
        return $"""
            {head}
            FROM jsonb_to_recordset(@Filters::jsonb) AS f({string.Join(", ", columns.Select(c => $"{c} text"))})
            WHERE r.deleted_tx_id IS NULL
              AND {string.Join("\n  AND ", columns.Select(c => $"r.{c} = f.{c}"))}
            """;
    }

    public async Task<SnapToken> Write(
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        await using var db = (NpgsqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Write(db, relations, attributes, ct);
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        using var span = ValtuutusTelemetry.StartWrite(PostgresDataReaderProvider.DbSystem);
        try
        {
#if !NETCOREAPP3_0_OR_GREATER
            await using var transaction = ((NpgsqlConnection)connection).BeginTransaction();
#else
            await using var transaction = await ((NpgsqlConnection)connection).BeginTransactionAsync(ct);
#endif
            var snapToken = await WriteCore(connection, transaction, span.CountRelations(relations), span.CountAttributes(attributes), ct);
            await transaction.CommitAsync(ct);
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
        using var span = ValtuutusTelemetry.StartWrite(PostgresDataReaderProvider.DbSystem);
        try
        {
            var snapToken = await WriteCore(connection, transaction, span.CountRelations(relations), span.CountAttributes(attributes), ct);
            span.SetSnapToken(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private async Task<SnapToken> WriteCore(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        var now = _timeProvider.GetUtcNow();
        var transactId = MonotonicUlid.Next(_timeProvider);
        var db = (NpgsqlConnection)connection;
        var tx = (NpgsqlTransaction)transaction;

        var xid = await InsertTransaction(db, transactId, now, tx, ct);
        var registration = _registrar.RegisterAsync(transactId.ToString(), xid, ct);
        try
        {
            await WriteRelationsAsync(db, tx, transactId, relations, ct);
            await WriteAttributesAsync(db, tx, transactId, attributes, ct);
        }
        catch
        {
            await AwaitQuietly(registration);
            throw;
        }

        return new SnapToken((await Settle(db, tx, transactId, await registration, ct)).ToString());
    }

    protected virtual async Task WriteRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Ulid transactId, IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        await using var relationsWriter = await connection.BeginBinaryImportAsync(_c.CopyRelations, ct);
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

    protected virtual async Task WriteAttributesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Ulid transactId, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        await using (var createTempTableCommand = connection.CreateCommand())
        {
            createTempTableCommand.Transaction = transaction;
            createTempTableCommand.CommandText =
                "CREATE TEMPORARY TABLE temp_attributes (entity_type VARCHAR(256), entity_id VARCHAR(64), attribute VARCHAR(64), value JSONB, created_tx_id CHAR(26))";
            await createTempTableCommand.ExecuteNonQueryAsync(ct);
        }

        await using var attributesWriter = await connection.BeginBinaryImportAsync(
            "copy temp_attributes (entity_type, entity_id, attribute, value, created_tx_id) from STDIN (FORMAT BINARY)",
            ct);

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

        await using var mergeCommand = connection.CreateCommand();
        mergeCommand.Transaction = transaction;
        mergeCommand.CommandText = _c.MergeAttributes;
        await mergeCommand.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> InsertTransaction(NpgsqlConnection db, Ulid transactId, DateTimeOffset createdAt,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _c.InsertTransaction;
        command.Parameters.Add(new NpgsqlParameter<string>("id", NpgsqlDbType.Char) { Size = 26, TypedValue = transactId.ToString() });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", NpgsqlDbType.TimestampTz) { TypedValue = createdAt });
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    // Removes the write's pending row inside the write's own transaction, so it disappears exactly
    // when the write commits, whoever commits it; a rollback brings it back for the registrar to
    // collect. A write whose id isn't above the newest id committed when it registered takes a new
    // one first: a reader may already have settled at that newest id.
    private async Task<Ulid> Settle(NpgsqlConnection db, NpgsqlTransaction transaction, Ulid transactId,
        string? newestCommitted, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        AddFixedChar(command, "lower_bound", transactId.ToString());

        if (newestCommitted is null || string.CompareOrdinal(newestCommitted, transactId.ToString()) < 0)
        {
            command.CommandText = _c.Settle;
            await command.ExecuteNonQueryAsync(ct);
            return transactId;
        }

        var renumbered = MonotonicUlid.NextAfter(_timeProvider, Ulid.Parse(newestCommitted));
        command.CommandText = _c.Settle + ";\n" + _c.Renumber;
        AddFixedChar(command, "id", transactId.ToString());
        AddFixedChar(command, "renumbered", renumbered.ToString());
        await command.ExecuteNonQueryAsync(ct);
        return renumbered;
    }

    private static void AddFixedChar(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Char) { Size = 26, TypedValue = value });

    // A write that fails before it settles still waits for its registration, so nothing it started
    // outlives it; the registration's own outcome no longer matters.
    private static async Task AwaitQuietly(Task registration)
    {
        try
        {
            await registration;
        }
        catch
        {
            // The write's own exception is the one that propagates.
        }
    }

    public async Task<SnapToken> Delete(
        DeleteFilter filter,
        CancellationToken ct
    ) {
        await using var db = (NpgsqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Delete(db, filter, ct);
    }

    public async Task<SnapToken> Delete(
        IDbConnection connection,
        DeleteFilter filter,
        CancellationToken ct
    ) {
        using var span = ValtuutusTelemetry.StartWrite(PostgresDataReaderProvider.DbSystem);
        try
        {
#if !NETCOREAPP3_0_OR_GREATER
            await using var transaction = ((NpgsqlConnection)connection).BeginTransaction();
#else
            await using var transaction = await ((NpgsqlConnection)connection).BeginTransactionAsync(ct);
#endif
            var snapToken = await DeleteCore(connection, transaction, filter, ct);
            await transaction.CommitAsync(ct);
            span.SetSnapToken(snapToken);
            await NotifyCommitted(snapToken);

            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    public async Task<SnapToken> Delete(
        IDbConnection connection,
        IDbTransaction transaction,
        DeleteFilter filter,
        CancellationToken ct
    )
    {
        using var span = ValtuutusTelemetry.StartWrite(PostgresDataReaderProvider.DbSystem);
        try
        {
            var snapToken = await DeleteCore(connection, transaction, filter, ct);
            span.SetSnapToken(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private async Task<SnapToken> DeleteCore(
        IDbConnection connection,
        IDbTransaction transaction,
        DeleteFilter filter,
        CancellationToken ct
    ) {
        var now = _timeProvider.GetUtcNow();
        var transactId = MonotonicUlid.Next(_timeProvider);
        var npgsqlConnection = (NpgsqlConnection)connection;
        var npgsqlTransaction = (NpgsqlTransaction)transaction;
        var snapTokenValue = transactId.ToString();

        var xid = await InsertTransaction(npgsqlConnection, transactId, now, npgsqlTransaction, ct);
        var registration = _registrar.RegisterAsync(snapTokenValue, xid, ct);
        try
        {

        foreach (var group in filter.Relations.GroupBy(static f => RelationFilterShape.Of(f)))
        {
            var filtersJson = JsonSerializer.Serialize(group.ToArray(), DeleteFilterJsonContext.Default.DeleteRelationsFilterArray);
            await ExecuteDeleteBatch(npgsqlConnection, npgsqlTransaction, _c.DeleteRelations[group.Key], snapTokenValue, filtersJson, ct);
        }

        if (filter.Attributes.Length > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filter.Attributes, DeleteFilterJsonContext.Default.DeleteAttributesFilterArray);
            await ExecuteDeleteBatch(npgsqlConnection, npgsqlTransaction, _c.DeleteAttributes, snapTokenValue, filtersJson, ct);
        }
        }
        catch
        {
            await AwaitQuietly(registration);
            throw;
        }

        return new SnapToken((await Settle(npgsqlConnection, npgsqlTransaction, transactId, await registration, ct)).ToString());
    }

    /// <inheritdoc />
    public async Task NotifyCommitted(SnapToken snapToken)
    {
        await WaitUntilSettled(snapToken);
        await _options.NotifyDataWritten(_provider, snapToken);
    }

    // A committed write is visible to requests without a token only once the latest settled token
    // reaches it, which needs every older write still open to end. Each round waits on the locks of
    // the older writes registered so far, then reads the latest settled token; a write registered
    // later with an older id (a node whose clock is behind) is caught by the next round. Past the
    // timeout the write returns anyway, and requests without a token see it once it settles.
    private async Task WaitUntilSettled(SnapToken snapToken)
    {
        var deadline = _timeProvider.GetTimestamp() + (long)(_options.SettleTimeout.TotalSeconds * _timeProvider.TimestampFrequency);
        var pause = TimeSpan.FromMilliseconds(1);
        while (true)
        {
            var remaining = deadline - _timeProvider.GetTimestamp();
            if (remaining <= 0) return;
            var remainingMs = Math.Max(1, (long)(remaining * 1000.0 / _timeProvider.TimestampFrequency));

            string? settled = null;
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await using var batch = connection.CreateBatch();
                batch.BatchCommands.Add(new NpgsqlBatchCommand("BEGIN"));
                batch.BatchCommands.Add(new NpgsqlBatchCommand($"SET LOCAL lock_timeout = {remainingMs}"));
                var wait = new NpgsqlBatchCommand(_c.WaitForOlderWrites);
                wait.Parameters.Add(new NpgsqlParameter<string>("snap_token", NpgsqlDbType.Char) { Size = 26, TypedValue = snapToken.Value });
                batch.BatchCommands.Add(wait);
                batch.BatchCommands.Add(new NpgsqlBatchCommand("COMMIT"));
                batch.BatchCommands.Add(new NpgsqlBatchCommand(_c.LatestSettled));
                await using var reader = await batch.ExecuteReaderAsync();
                do
                {
                    if (reader.GetName(0) == "id" && await reader.ReadAsync() && !reader.IsDBNull(0))
                        settled = reader.GetString(0);
                } while (await reader.NextResultAsync());
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return;
            }

            if (settled is not null && string.CompareOrdinal(settled, snapToken.Value) >= 0) return;
            await Task.Delay(pause, _timeProvider);
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, TimeSpan.FromMilliseconds(20).Ticks));
        }
    }

    private static async Task ExecuteDeleteBatch(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, string snapToken, string filtersJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter<string>("SnapToken", NpgsqlDbType.Char) { Size = 26, TypedValue = snapToken });
        command.Parameters.Add(new NpgsqlParameter<string>("Filters", NpgsqlDbType.Text) { TypedValue = filtersJson });
        await command.ExecuteNonQueryAsync(ct);
    }
}
