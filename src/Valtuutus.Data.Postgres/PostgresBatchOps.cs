using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// Postgres dialect of <see cref="RelationalBatchProviderBase"/> — the
/// <see cref="IRelationalBatchOps"/> implementation AddPostgres registers for
/// BatchedPhysicalExecutor. Holds no batch-building logic of its own: the base class writes every
/// command generically, and this class supplies only the dialect — the SQL catalog (the same
/// cached <see cref="PostgresDataReaderProvider.ReaderQueries"/> strings the single-op reader
/// dispatches, so both paths share one definition and one server-side prepared statement), the
/// NpgsqlParameter construction (same helpers, same NpgsqlDbType/Size as the single-op path), the
/// native <c>text[]</c>-style array parameter strategy, and batch creation/execution against the
/// same cached <see cref="NpgsqlDataSource"/> the reader uses.
/// </summary>
public sealed class PostgresBatchOps : RelationalBatchProviderBase, IDisposable
{
    private readonly PostgresDataReaderProvider.ReaderQueries _q;
    private readonly NpgsqlDataSource _dataSource;
    private readonly BatchRateLimiter _rateLimiter;

    public PostgresBatchOps(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        ValtuutusPostgresOptions dbOptions)
    {
        _rateLimiter = new BatchRateLimiter(options);
        _q = PostgresDataReaderProvider.GetQueries(dbOptions);
        // Same probe-then-cache pattern as PostgresDataReaderProvider's constructor: the factory
        // yields the connection string, the shared cache yields the (auto-prepare-configured)
        // NpgsqlDataSource — the same instance the reader's hot path uses.
        using var probeConnection = (NpgsqlConnection)connectionFactory();
        _dataSource = PostgresDataReaderProvider.GetOrCreateDataSource(probeConnection.ConnectionString, dbOptions);
    }

    public override DbBatch CreateBatch() => _dataSource.CreateBatch();

    // Same body PostgresDataReaderProvider.ExecuteBatchAsync had before the batch ops moved here,
    // including the slot semantics IRelationalBatchOps.ExecuteBatchAsync documents: the rate-limit
    // slot is released as soon as the reader is obtained, NOT after the caller drains it.
    public override async Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await _rateLimiter.Enter(cancellationToken);
        try
        {
            return await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    protected override string GetSql(RelationalBatchQuery query) => query switch
    {
        RelationalBatchQuery.HasDirectRelation => _q.HasDirectRelation,
        RelationalBatchQuery.HasTrueBoolAttribute => _q.HasTrueBoolAttribute,
        RelationalBatchQuery.HasTupleToUserSetRelation => _q.HasTupleToUserSetRelation,
        RelationalBatchQuery.HasAnyDirectRelation => _q.HasAnyDirectRelationBatchTemplate,
        RelationalBatchQuery.HasAnyOfDirectRelations => _q.HasAnyOfDirectRelationsBatchTemplate,
        RelationalBatchQuery.HasAnyOfAttributes => _q.HasAnyOfAttributesBatchTemplate,
        RelationalBatchQuery.GetRelations => _q.SelectRelationsByTupleFilterNoSubject,
        RelationalBatchQuery.GetRelationsWithSubjectFilters => _q.SelectRelationsByTupleFilter,
        RelationalBatchQuery.GetIndirectRelations => _q.GetIndirectRelations,
        _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
    };

    // One native array parameter, referenced as "@name" — the fragment lands in the catalog
    // template's {0}, producing the exact SQL text the single-op sibling dispatches.
    protected override string WriteNameArrayParam(DbBatchCommand cmd, string baseName, string[] values)
    {
        Parameters(cmd).Add(new NpgsqlParameter<string[]>(baseName, NpgsqlDbType.Array | NpgsqlDbType.Varchar)
        {
            TypedValue = values
        });
        return "@" + baseName;
    }

    protected override void WriteStringParam(DbBatchCommand cmd, string name, string value, int size)
        => PostgresDataReaderProvider.AddStringParameter(Parameters(cmd), name, value, size);

    protected override void WriteNullableStringParam(DbBatchCommand cmd, string name, string? value, int size)
        => PostgresDataReaderProvider.AddNullableStringParameter(Parameters(cmd), name, value, size);

    protected override void WriteSnapTokenParam(DbBatchCommand cmd, string name, SnapToken snapToken)
        => PostgresDataReaderProvider.AddFixedCharParameter(Parameters(cmd), name, snapToken.Value, 26);

    // DbBatchCommand's declared type is the ADO-abstract one, but every batch this class hands out
    // (CreateBatch) comes from NpgsqlDataSource.CreateBatch(), so the command the base class
    // creates from it is always a genuine NpgsqlBatchCommand (Npgsql overrides the protected
    // factory DbBatch.CreateBatchCommand() delegates to) — verified against the real Npgsql 9.0.3
    // API. The cast is what lets the reader's NpgsqlParameterCollection-typed helpers stay
    // reusable here unmodified, preserving parameter construction byte-for-byte.
    private static NpgsqlParameterCollection Parameters(DbBatchCommand cmd) => ((NpgsqlBatchCommand)cmd).Parameters;

    public void Dispose() => _rateLimiter.Dispose();

    // The reader provider gets these semantics by extending RateLimiterExecuter directly; this
    // class's base slot is taken by RelationalBatchProviderBase, so the same mechanism is composed
    // instead — a nested RateLimiterExecuter whose protected members are surfaced to the owner.
    private sealed class BatchRateLimiter(ValtuutusDataOptions options) : RateLimiterExecuter(options)
    {
        public Task Enter(CancellationToken cancellationToken) => EnterQuery(cancellationToken);

        public void Release() => Semaphore.Release();
    }
}
