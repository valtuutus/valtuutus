using System.Data.Common;
using MySqlConnector;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// MySQL/MariaDB dialect of <see cref="RelationalBatchProviderBase"/>, the
/// <see cref="IRelationalBatchOps"/> AddMySql registers for BatchedPhysicalExecutor. Commands carry
/// the reader's catalog statements and parameters, so a batched op sends the same text as its
/// single-op sibling. Batches run on the reader's data source as one text-protocol round trip.
/// </summary>
public sealed class MySqlBatchOps : RelationalBatchProviderBase
{
    private readonly MySqlQueries _q;
    private readonly string _dbSystem;
    private readonly MySqlDataSource _dataSource;
    private readonly BatchRateLimiter _rateLimiter;
    // True for the UsingBatchResolvedSnapToken view: built on the batch-resolved-token catalog, it
    // writes no snap-token parameter. It shares the owner's data source and rate limiter.
    private readonly bool _usesBatchResolvedSnapToken;
    private readonly MySqlBatchOps? _batchResolvedSnapTokenView;

    public MySqlBatchOps(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        ValtuutusMySqlOptions dbOptions)
    {
        ArgumentNullException.ThrowIfNull(dbOptions);
        _rateLimiter = new BatchRateLimiter(options);
        _q = MySqlQueries.Get(dbOptions);
        _dbSystem = MySqlConnections.DbSystem(dbOptions.ServerType);
        // MySqlConnector drops the password from ConnectionString once a connection opens, so the
        // string is read from a fresh connection that is never opened. The cache hands back the
        // reader's data source for the same string.
        using var probeConnection = (MySqlConnection)connectionFactory();
        _dataSource = MySqlConnections.GetOrCreateDataSource(probeConnection.ConnectionString);
        _batchResolvedSnapTokenView = new MySqlBatchOps(MySqlQueries.GetBatchResolved(dbOptions), _dbSystem,
            _dataSource, _rateLimiter);
    }

    private MySqlBatchOps(MySqlQueries batchResolvedSnapTokenQueries, string dbSystem, MySqlDataSource dataSource,
        BatchRateLimiter rateLimiter)
    {
        _q = batchResolvedSnapTokenQueries;
        _dbSystem = dbSystem;
        _dataSource = dataSource;
        _rateLimiter = rateLimiter;
        _usesBatchResolvedSnapToken = true;
    }

    /// <inheritdoc />
    public override IRelationalBatchOps UsingBatchResolvedSnapToken => _batchResolvedSnapTokenView ?? this;

    // The token goes into a session variable, which every later command of the batch reads. The
    // variable outlives the batch on the pooled connection; the next batch that reads it sets it
    // first.
    /// <inheritdoc />
    public override void AddResolveLatestSnapTokenToBatch(DbBatch batch) =>
        NewCommand(batch, _q.ResolveLatestSnapTokenForBatch);

    /// <inheritdoc />
    public override string DbSystemName => _dbSystem;

    // The batch comes from a connection of the data source's pool: the batch DbDataSource builds by
    // default can't create commands. The connection opens when the batch executes.
    /// <inheritdoc />
    public override DbBatch CreateBatch() => _dataSource.CreateConnection().CreateBatch();

    // The rate-limit slot is released as soon as the reader is obtained, as
    // IRelationalBatchOps.ExecuteBatchAsync documents. The returned reader owns the batch's
    // connection and gives it back to the pool when disposed. The batch is never prepared: one text
    // batch is one round trip, and a prepared batch executes its commands one by one.
    /// <inheritdoc />
    public override async Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken)
    {
        var connection = batch.Connection
            ?? throw new ArgumentException("The batch must come from CreateBatch.", nameof(batch));
        await _rateLimiter.Enter(cancellationToken);
        try
        {
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                return new MySqlBatchReader(reader, connection);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    // The array-taking queries pick their statement by count in their Add* overrides, so these
    // entries are the list forms, which answer any count.
    protected override string GetSql(RelationalBatchQuery query) => query switch
    {
        RelationalBatchQuery.HasDirectRelation => _q.HasDirectRelation,
        RelationalBatchQuery.HasTrueBoolAttribute => _q.HasTrueBoolAttribute,
        RelationalBatchQuery.HasTupleToUserSetRelation => _q.HasTupleToUserSetRelation,
        RelationalBatchQuery.HasUsersetJoinRelation => _q.HasUsersetJoinRelation,
        RelationalBatchQuery.HasAnyDirectRelation => _q.HasAnyDirectRelationMany,
        RelationalBatchQuery.HasAnyOfDirectRelations => _q.HasAnyOfDirectRelationsMany,
        RelationalBatchQuery.HasAnyOfAttributes => _q.HasAnyOfAttributesMany,
        RelationalBatchQuery.GetRelations => _q.SelectRelationsByTupleFilterNoSubject,
        RelationalBatchQuery.GetRelationsWithSubjectFilters => _q.SelectRelationsByTupleFilter,
        RelationalBatchQuery.GetIndirectRelations => _q.GetIndirectRelations,
        RelationalBatchQuery.GetAttributesSingleEntity => _q.AttributesSingleEntityMany,
        _ => throw new ArgumentOutOfRangeException(nameof(query), query, null),
    };

    // One JSON array parameter, under the name the catalog SQL references.
    protected override string WriteNameArrayParam(DbBatchCommand cmd, string baseName, string[] values)
    {
        var name = "@" + baseName;
        AddJsonArray(cmd.Parameters, name, values);
        return name;
    }

    protected override void WriteStringParam(DbBatchCommand cmd, string name, string value, int size) =>
        AddString(cmd.Parameters, name, value, size);

    protected override void WriteNullableStringParam(DbBatchCommand cmd, string name, string? value, int size) =>
        AddNullableString(cmd.Parameters, name, value, size);

    protected override void WriteSnapTokenParam(DbBatchCommand cmd, string name, SnapToken snapToken)
    {
        if (!_usesBatchResolvedSnapToken)
            AddSnapToken(cmd.Parameters, snapToken);
    }

    /// <inheritdoc />
    public override void AddHasAnyDirectRelationToBatch(DbBatch batch, string entityType, string[] entityIds,
        string relation, string subjectId, SnapToken snapToken)
    {
        var cmd = NewReaderCommand(batch);
        cmd.CommandText = MySqlCheckReads.HasAnyDirectRelation(cmd.Parameters, _q, entityType, entityIds, relation,
            subjectId, BoundToken(snapToken));
    }

    /// <inheritdoc />
    public override void AddHasAnyOfDirectRelationsToBatch(DbBatch batch, string entityType, string entityId,
        string[] relationNames, string subjectId, SnapToken snapToken)
    {
        var cmd = NewReaderCommand(batch);
        cmd.CommandText = MySqlCheckReads.HasAnyOfDirectRelations(cmd.Parameters, _q, entityType, entityId,
            relationNames, subjectId, BoundToken(snapToken));
    }

    /// <inheritdoc />
    public override void AddHasAnyOfAttributesToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
    {
        var cmd = NewReaderCommand(batch);
        cmd.CommandText = MySqlCheckReads.HasAnyOfAttributes(cmd.Parameters, _q, entityType, entityId,
            attributeNames, BoundToken(snapToken));
    }

    /// <inheritdoc />
    public override void AddGetAttributesSingleEntityToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
    {
        var cmd = NewReaderCommand(batch);
        cmd.CommandText = MySqlCheckReads.AttributesSingleEntity(cmd.Parameters, _q, entityType, entityId,
            attributeNames, BoundToken(snapToken));
    }

    /// <inheritdoc />
    public override void AddHasFusedExpressionToBatch(DbBatch batch, string entityType, string entityId,
        IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, string? subjectType, string? subjectId, SnapToken snapToken)
    {
        var cmd = NewReaderCommand(batch);
        cmd.CommandText = MySqlCheckReads.HasFusedExpression(cmd.Parameters, _q, entityType, entityId, leaves,
            requireAll, subjectType, subjectId, BoundToken(snapToken));
    }

    // The overrides above take their statement and parameters from MySqlCheckReads, which picks the
    // statement while it writes the parameters, so the command's text is set after it is added.
    private static DbBatchCommand NewReaderCommand(DbBatch batch)
    {
        var command = batch.CreateBatchCommand();
        batch.BatchCommands.Add(command);
        return command;
    }

    // The batch-resolved view's statements read the session variable, so it binds no token.
    private SnapToken? BoundToken(SnapToken snapToken) => _usesBatchResolvedSnapToken ? null : snapToken;

    // The reader gets these semantics by extending RateLimiterExecuter; this class's base slot is
    // taken by RelationalBatchProviderBase, so it composes one instead.
    private sealed class BatchRateLimiter(ValtuutusDataOptions options) : RateLimiterExecuter(options)
    {
        public Task Enter(CancellationToken cancellationToken) => EnterQuery(cancellationToken);

        public void Release() => Semaphore.Release();
    }
}
