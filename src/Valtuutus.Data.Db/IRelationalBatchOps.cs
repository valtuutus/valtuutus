using System.Data.Common;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

/// <summary>
/// Opt-in capability for relational providers that support ADO.NET DbBatch: package multiple
/// statements into one physical round trip. Separate from <see cref="IRelationalCheckOps"/>
/// deliberately — that interface's members are mandatory for every relational provider;
/// this one is optional, so a provider without batch support (not yet ported) simply doesn't
/// register an implementation, and the consumer (BatchedPhysicalExecutor, which receives it by
/// injection — or null when absent) degrades gracefully to individual dispatch rather than this
/// being a breaking interface addition. <see cref="RelationalBatchProviderBase"/> implements the
/// Add* family once, generically; a provider supplies only its dialect hooks.
/// </summary>
public interface IRelationalBatchOps
{
    /// <summary>
    /// Creates an empty batch bound to this provider's connection/data source. Caller adds
    /// DbBatchCommands, then calls <see cref="ExecuteBatchAsync"/> with the same instance.
    /// </summary>
    DbBatch CreateBatch();

    /// <summary>
    /// Executes a batch created by <see cref="CreateBatch"/> as one physical round trip.
    /// Callers never acquire their own concurrency slot or touch a connection directly — but
    /// unlike every other member on <see cref="IRelationalCheckOps"/>/
    /// <see cref="Valtuutus.Core.Data.IDataReaderProvider"/>, the rate-limit slot this method
    /// takes is released as soon as the reader is obtained, NOT after the caller finishes
    /// draining it (there is no read loop inside this method — draining happens in the caller,
    /// after this returns). So <c>MaxConcurrentQueries</c> bounds concurrent round-trip
    /// dispatch here, not the full lifetime of an open, undrained batch reader — a caller that
    /// holds the returned reader open for a long time is not counted against the concurrency
    /// limit for that duration. Returns the reader positioned before the first command's result
    /// set; callers advance through each command's result via <c>NextResultAsync</c>, in the
    /// same order commands were added to the batch.
    /// </summary>
    Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a command answering <see cref="IRelationalCheckOps.HasAnyOfDirectRelations"/>'s question to
    /// <paramref name="batch"/> (obtained from <see cref="CreateBatch"/>), instead of running it as its own
    /// round trip. Same SQL text and parameter shape as <see cref="IRelationalCheckOps.HasAnyOfDirectRelations"/>
    /// — this is the batched sibling of that method, not a re-derived query. Lives here (not on
    /// <see cref="IRelationalCheckOps"/>) because it's only meaningful for a provider that also implements
    /// this optional batch capability — adding it to the mandatory interface would force every relational
    /// provider (including ones without batch support) to implement it. The result set this command produces
    /// is read back later via <c>IBatchableCheckOp.ReadResultAsync</c> (Valtuutus.Data.Db), in the order this
    /// method was called relative to other commands added to the same batch.
    /// </summary>
    void AddHasAnyOfDirectRelationsToBatch(DbBatch batch, string entityType, string entityId,
        string[] relationNames, string subjectId, SnapToken snapToken);

    /// <summary>
    /// Adds a command answering <see cref="IRelationalCheckOps.HasAnyOfAttributes"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, symmetric to
    /// <see cref="AddHasAnyOfDirectRelationsToBatch"/>.
    /// </summary>
    void AddHasAnyOfAttributesToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken);

    /// <summary>
    /// Adds a command answering <see cref="IDataReaderProvider.HasDirectRelation"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, same SQL text/params, read back
    /// as a single boolean row instead of its own round trip.
    /// </summary>
    void AddHasDirectRelationToBatch(DbBatch batch, RelationTupleFilter tupleFilter, string subjectId);

    /// <summary>
    /// Adds a command answering <see cref="IDataReaderProvider.HasTrueBoolAttribute"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, same SQL text/params.
    /// </summary>
    void AddHasTrueBoolAttributeToBatch(DbBatch batch, string entityType, string entityId, string attribute,
        SnapToken snapToken);

    /// <summary>
    /// Adds a command answering <see cref="IDataReaderProvider.HasTupleToUserSetRelation"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, same SQL text/params.
    /// </summary>
    void AddHasTupleToUserSetRelationToBatch(DbBatch batch, string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken);

    /// <summary>
    /// Adds a command answering <see cref="IRelationalCheckOps.HasUsersetJoinRelation"/>'s
    /// question to <paramref name="batch"/> — the batched sibling of that method, same SQL
    /// text/params.
    /// </summary>
    void AddHasUsersetJoinRelationToBatch(DbBatch batch, string entityType, string entityId, string relation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken);

    /// <summary>
    /// Adds a command answering <see cref="IDataReaderProvider.HasAnyDirectRelation"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, same SQL text/params.
    /// </summary>
    void AddHasAnyDirectRelationToBatch(DbBatch batch, string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken);

    /// <summary>
    /// Adds a command answering <see cref="IDataReaderProvider.GetRelations"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, same SQL text/params (the SQL
    /// template still branches on whether <paramref name="tupleFilter"/> carries subject filters, same
    /// as the non-batch method).
    /// </summary>
    void AddGetRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter);

    /// <summary>
    /// Adds a command answering <see cref="IDataReaderProvider.GetIndirectRelations"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method, same SQL text/params.
    /// </summary>
    void AddGetIndirectRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter);

    /// <summary>
    /// Adds a command answering <see cref="IRelationalCheckOps.HasFusedExpression"/>'s question to
    /// <paramref name="batch"/> — the batched sibling of that method. Unlike every other Add* member
    /// on this interface, there is no shared fixed SQL text this mirrors byte-for-byte: the compound
    /// expression's shape varies per call (leaf count/kind/negation), so each provider composes its
    /// own text per call via <see cref="RelationalBatchProviderBase.AddHasFusedExpressionToBatch"/>
    /// (declared abstract there, alongside <see cref="RelationalBatchProviderBase.CreateBatch"/> —
    /// the class's other genuinely dialect-specific hooks).
    /// </summary>
    void AddHasFusedExpressionToBatch(DbBatch batch, string entityType, string entityId,
        IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, string? subjectType, string? subjectId,
        SnapToken snapToken);
}
