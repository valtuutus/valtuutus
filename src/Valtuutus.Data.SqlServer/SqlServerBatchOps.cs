using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Utils;

namespace Valtuutus.Data.SqlServer;

/// <summary>
/// SqlServer dialect of <see cref="RelationalBatchProviderBase"/> — the
/// <see cref="IRelationalBatchOps"/> implementation AddSqlServer registers for
/// BatchedPhysicalExecutor. Holds no batch-building logic of its own: the base class writes every
/// command generically, and this class supplies only the dialect — the SQL catalog (the same
/// cached <see cref="SqlServerDataReaderProvider.ReaderQueries"/> strings the single-op reader
/// dispatches, so both paths share one definition), the SqlParameter construction (same helpers,
/// same SqlDbType/size as the single-op path), the table-valued-parameter array strategy (unlike
/// Postgres's native array type, SqlServer has no array parameter — <see cref="TvpHelper"/>
/// streams the values as a TVP), and batch creation/execution against a fresh SqlConnection (no
/// SqlServer equivalent of NpgsqlDataSource's pooled data source object to share).
/// </summary>
public sealed class SqlServerBatchOps : RelationalBatchProviderBase, IDisposable
{
    private readonly DbConnectionFactory _connectionFactory;
    private readonly SqlServerDataReaderProvider.ReaderQueries _q;
    private readonly BatchRateLimiter _rateLimiter;

    public SqlServerBatchOps(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions)
    {
        _connectionFactory = connectionFactory;
        _rateLimiter = new BatchRateLimiter(options);
        _q = SqlServerDataReaderProvider.GetQueries(dbOptions);
    }

    // SqlServer has no NpgsqlDataSource-equivalent pooling object, so unlike Postgres, CreateBatch
    // cannot open a connection itself (it's a synchronous interface member with no
    // CancellationToken). It only constructs an unopened SqlConnection + calls
    // connection.CreateBatch() (pure object construction, no I/O — DbConnection.CreateBatch just
    // assigns batch.Connection). All I/O (open + execute) happens in ExecuteBatchAsync, which is
    // async and does carry a CancellationToken.
    public override DbBatch CreateBatch()
    {
        var connection = (SqlConnection)_connectionFactory();
        return connection.CreateBatch();
    }

    // Same body SqlServerDataReaderProvider.ExecuteBatchAsync had before the batch ops moved here,
    // including the slot semantics IRelationalBatchOps.ExecuteBatchAsync documents: the rate-limit
    // slot is released as soon as the reader is obtained, NOT after the caller drains it.
    //
    // Does NOT use CommandBehavior.CloseConnection to return the connection — see
    // CommandBehaviorCloseConnectionWorkaroundReader's doc comment for why: it's a WORKAROUND for
    // a Microsoft.Data.SqlClient bug (CommandBehavior.CloseConnection not honored for SqlBatch),
    // not this design's first choice.
    public override async Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await _rateLimiter.Enter(cancellationToken);
        try
        {
            var connection = (SqlConnection)batch.Connection!;
            try
            {
                await connection.OpenAsync(cancellationToken);
                var reader = await batch.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken)
                    .ConfigureAwait(false);
                return new CommandBehaviorCloseConnectionWorkaroundReader(reader, connection);
            }
            catch
            {
                // No reader was obtained, so nothing downstream will ever dispose the connection —
                // the workaround reader above only exists once ExecuteReaderAsync has already
                // succeeded.
                await connection.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    // These 4 boolean-scalar queries CAST their EXISTS(...) result AS BIT — preserved verbatim
    // from the reader's cached SQL text — because BatchedPhysicalExecutor's result reader calls
    // DbDataReader.GetBoolean(0), which requires an actual bit-typed column, not SqlServer's
    // implicit int.
    protected override string GetSql(RelationalBatchQuery query) => query switch
    {
        RelationalBatchQuery.HasDirectRelation => _q.HasDirectRelation,
        RelationalBatchQuery.HasTrueBoolAttribute => _q.HasTrueBoolAttribute,
        RelationalBatchQuery.HasTupleToUserSetRelation => _q.HasTupleToUserSetRelation,
        RelationalBatchQuery.HasUsersetJoinRelation => _q.HasUsersetJoinRelation,
        RelationalBatchQuery.HasAnyDirectRelation => _q.HasAnyDirectRelation,
        RelationalBatchQuery.HasAnyOfDirectRelations => _q.HasAnyOfDirectRelations,
        RelationalBatchQuery.HasAnyOfAttributes => _q.HasAnyOfAttributes,
        RelationalBatchQuery.GetRelations => _q.SelectRelationsByTupleFilterNoSubject,
        RelationalBatchQuery.GetRelationsWithSubjectFilters => _q.SelectRelationsByTupleFilter,
        RelationalBatchQuery.GetIndirectRelations => _q.GetIndirectRelations,
        _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
    };

    // Unlike Postgres's native array parameter, none of these 3 SQL strings carry a {0}
    // placeholder — they already reference the TVP parameter by its fixed name inline (e.g.
    // "entity_id IN (SELECT id FROM @EntityIds)"), the exact same table-valued-parameter strategy
    // the reader's own non-batch methods use via TvpHelper. So the base class's post-hoc
    // string.Format(cmd.CommandText, arrayFragment) call is a no-op here (no {0} to substitute):
    // the TVP parameter just needs to land on the command under the name the SQL text already
    // hardcodes, which MapParamName supplies.
    protected override string WriteNameArrayParam(DbBatchCommand cmd, string baseName, string[] values)
    {
        var paramName = MapParamName(baseName);
        TvpHelper.AsTvpParameter(values, _q.TvpListIdsTypeName).AddParameter(Parameters(cmd), paramName);
        return paramName;
    }

    protected override void WriteStringParam(DbBatchCommand cmd, string name, string value, int size)
        => SqlServerDataReaderProvider.AddStringParameter(Parameters(cmd), MapParamName(name), value, size);

    protected override void WriteNullableStringParam(DbBatchCommand cmd, string name, string? value, int size)
        => SqlServerDataReaderProvider.AddNullableStringParameter(Parameters(cmd), MapParamName(name), value, size);

    protected override void WriteSnapTokenParam(DbBatchCommand cmd, string name, SnapToken snapToken)
        => SqlServerDataReaderProvider.AddFixedCharParameter(Parameters(cmd), MapParamName(name), snapToken.Value, 26);

    // RelationalBatchProviderBase passes fixed snake_case parameter names (its own naming
    // convention, shared by every provider's Add*ToBatch override). The reader's cached SQL text
    // predates the batch effort and already uses SqlServer's own "@PascalCase" parameter-naming
    // convention (@EntityType, @SnapToken, ...) — this maps one to the other so the reader's SQL
    // strings can be reused byte-identical rather than duplicated with renamed parameters.
    private static string MapParamName(string name) => name switch
    {
        "entity_type" => "@EntityType",
        "entity_id" => "@EntityId",
        "relation" => "@Relation",
        "subject_id" => "@SubjectId",
        "subject_type" => "@SubjectType",
        "subject_relation" => "@SubjectRelation",
        "snap_token" => "@SnapToken",
        "attribute" => "@Attribute",
        "tuple_set_relation" => "@TupleSetRelation",
        "computed_relation" => "@ComputedRelation",
        "entity_ids" => "@EntityIds",
        "relations" => "@Relations",
        "attributes" => "@Attributes",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unmapped batch parameter name."),
    };

    // DbBatchCommand's declared type is the ADO-abstract one, but every batch this class hands out
    // (CreateBatch) comes from SqlConnection.CreateBatch(), so the command the base class creates
    // from it is always a genuine SqlBatchCommand (Microsoft.Data.SqlClient overrides the
    // protected factory DbBatch.CreateBatchCommand() delegates to) — verified against
    // Microsoft.Data.SqlClient 6.0.1, the package floor pinned in Directory.Packages.props. The
    // cast is what lets the reader's SqlParameterCollection-typed helpers stay reusable here
    // unmodified, preserving parameter construction byte-for-byte.
    private static SqlParameterCollection Parameters(DbBatchCommand cmd) => ((SqlBatchCommand)cmd).Parameters;

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
