using System.Data.Common;
using System.Globalization;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

/// <summary>
/// Implements <see cref="IRelationalBatchOps"/>'s Add* family ONCE, generically over
/// ADO.NET's provider-agnostic <see cref="DbBatch"/>/<see cref="DbBatchCommand"/>. A
/// provider supplies only its dialect: <see cref="CreateBatch"/>,
/// <see cref="ExecuteBatchAsync"/>, the SQL catalog (<see cref="GetSql"/>), and the
/// name-array parameter strategy (<see cref="WriteNameArrayParam"/> — native array
/// parameter on Postgres, a JSON-array parameter read with OPENJSON on SqlServer; a provider
/// with neither falls back to per-value parameter-list expansion). Adding a new batchable
/// primitive costs a provider one catalog entry, not a new public member. Members are
/// virtual: extending a shipped provider means overriding a named hook, not shadowing.
/// </summary>
public abstract class RelationalBatchProviderBase : IRelationalBatchOps
{
    /// <inheritdoc />
    public abstract DbBatch CreateBatch();

    /// <inheritdoc />
    public abstract Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// The provider's dialect catalog: the exact SQL text for each batchable query shape — the
    /// same text the provider's non-batch sibling method dispatches (identical text keeps
    /// server-side prepared-statement caches shared between the two paths). Members documented
    /// as array-taking on <see cref="RelationalBatchQuery"/> may contain a <c>{0}</c> placeholder,
    /// which this class fills with <see cref="WriteNameArrayParam"/>'s returned fragment.
    /// </summary>
    protected abstract string GetSql(RelationalBatchQuery query);

    /// <summary>
    /// Writes a name/id array argument onto <paramref name="cmd"/> and returns the SQL fragment
    /// that references it — the text substituted into the catalog SQL's <c>{0}</c> placeholder.
    /// Postgres adds one native array parameter and SqlServer one JSON-array parameter, under
    /// the name their catalog SQL already references, so neither catalog carries a <c>{0}</c>; a
    /// provider with neither adds one parameter per value and returns the expanded list.
    /// </summary>
    protected abstract string WriteNameArrayParam(DbBatchCommand cmd, string baseName, string[] values);

    /// <summary>
    /// Composes and adds the fused-expression command — unlike every other Add* member below, this
    /// one cannot be implemented generically here: the SQL shape varies per call (leaf
    /// count/kind/negation), so there is no single <see cref="RelationalBatchQuery"/> catalog entry
    /// to key off. Each provider builds its own text per call via
    /// <see cref="FusedExpressionSqlBuilder.BuildBooleanExpression"/> plus its own leaf-fragment
    /// templates and parameter writing (see <c>PostgresBatchOps</c>/<c>SqlServerBatchOps</c>).
    /// </summary>
    public abstract void AddHasFusedExpressionToBatch(DbBatch batch, string entityType, string entityId,
        IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, string? subjectType, string? subjectId,
        SnapToken snapToken);

    /// <summary>
    /// Generic parameter append: <see cref="DbBatchCommand.CreateParameter"/> (net8+), name+value,
    /// add. The default implementation the Write*Param hooks fall back to when a provider doesn't
    /// need provider-typed parameters.
    /// </summary>
    protected static void AddParam(DbBatchCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    /// <summary>
    /// Creates a command with <paramref name="sql"/>, appends it to <paramref name="batch"/>'s
    /// command list and returns it for parameter population.
    /// </summary>
    protected static DbBatchCommand NewCommand(DbBatch batch, string sql)
    {
        var command = batch.CreateBatchCommand();
        command.CommandText = sql;
        batch.BatchCommands.Add(command);
        return command;
    }

    /// <summary>
    /// Appends one non-null string parameter. <paramref name="size"/> is the column's declared
    /// width — providers whose parameter types carry an explicit size (fixed prepared-statement
    /// typing) override this; the generic default ignores it.
    /// </summary>
    protected virtual void WriteStringParam(DbBatchCommand cmd, string name, string value, int size)
        => AddParam(cmd, name, value);

    /// <summary>
    /// Appends one nullable string parameter, normalizing null/whitespace to database NULL —
    /// the same normalization every provider's non-batch sibling applies.
    /// </summary>
    protected virtual void WriteNullableStringParam(DbBatchCommand cmd, string name, string? value, int size)
        => AddParam(cmd, name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);

    /// <summary>
    /// Appends the snapshot-token parameter. A dedicated hook because providers type it
    /// differently from ordinary varchar columns (fixed-width char on
    /// Postgres).
    /// </summary>
    protected virtual void WriteSnapTokenParam(DbBatchCommand cmd, string name, SnapToken snapToken)
        => AddParam(cmd, name, snapToken.Value);

    /// <summary>
    /// Called once an Add* member has written every parameter of the <paramref name="query"/>
    /// catalog command <paramref name="cmd"/>, so a provider can put the parameters into the form
    /// its catalog SQL expects (Postgres binds them positionally). The default does nothing.
    /// </summary>
    protected virtual void CompleteCommand(DbBatchCommand cmd, RelationalBatchQuery query)
    {
    }

    // Only a catalog entry that splices the array reference into its text carries {0}; formatting
    // one that does not would copy the statement text on every call.
    private static void SpliceArrayFragment(DbBatchCommand cmd, string arrayFragment)
    {
        if (cmd.CommandText.Contains("{0}", StringComparison.Ordinal))
            cmd.CommandText = string.Format(CultureInfo.InvariantCulture, cmd.CommandText, arrayFragment);
    }

    /// <inheritdoc />
    public virtual IRelationalBatchOps? UsingBatchResolvedSnapToken => null;

    /// <inheritdoc />
    public virtual string? DbSystemName => null;

    /// <inheritdoc />
    public virtual void AddResolveLatestSnapTokenToBatch(DbBatch batch)
        => throw new NotSupportedException($"{GetType().Name} cannot resolve the snap token inside a batch.");

    /// <inheritdoc />
    public virtual void AddHasDirectRelationToBatch(DbBatch batch, RelationTupleFilter tupleFilter, string subjectId)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasDirectRelation));
        WriteStringParam(cmd, "entity_type", tupleFilter.EntityType, 256);
        WriteStringParam(cmd, "entity_id", tupleFilter.EntityId, 64);
        WriteStringParam(cmd, "relation", tupleFilter.Relation, 64);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
        WriteSnapTokenParam(cmd, "snap_token", tupleFilter.SnapToken);
        CompleteCommand(cmd, RelationalBatchQuery.HasDirectRelation);
    }

    /// <inheritdoc />
    public virtual void AddHasTrueBoolAttributeToBatch(DbBatch batch, string entityType, string entityId,
        string attribute, SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasTrueBoolAttribute));
        WriteStringParam(cmd, "entity_type", entityType, 256);
        WriteStringParam(cmd, "entity_id", entityId, 64);
        WriteStringParam(cmd, "attribute", attribute, 64);
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        CompleteCommand(cmd, RelationalBatchQuery.HasTrueBoolAttribute);
    }

    /// <inheritdoc />
    public virtual void AddHasTupleToUserSetRelationToBatch(DbBatch batch, string entityType, string entityId,
        string tupleSetRelation, string subEntityType, string computedRelation, string subjectType, string subjectId,
        SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasTupleToUserSetRelation));
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        WriteStringParam(cmd, "entity_type", entityType, 256);
        WriteStringParam(cmd, "entity_id", entityId, 64);
        WriteStringParam(cmd, "tuple_set_relation", tupleSetRelation, 64);
        WriteStringParam(cmd, "sub_entity_type", subEntityType, 256);
        WriteStringParam(cmd, "computed_relation", computedRelation, 64);
        WriteStringParam(cmd, "subject_type", subjectType, 256);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
        CompleteCommand(cmd, RelationalBatchQuery.HasTupleToUserSetRelation);
    }

    /// <inheritdoc />
    public virtual void AddHasUsersetJoinRelationToBatch(DbBatch batch, string entityType, string entityId,
        string relation, string subEntityType, string computedRelation, string subjectType, string subjectId,
        SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasUsersetJoinRelation));
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        WriteStringParam(cmd, "entity_type", entityType, 256);
        WriteStringParam(cmd, "entity_id", entityId, 64);
        WriteStringParam(cmd, "relation", relation, 64);
        WriteStringParam(cmd, "sub_entity_type", subEntityType, 256);
        WriteStringParam(cmd, "computed_relation", computedRelation, 64);
        WriteStringParam(cmd, "subject_type", subjectType, 256);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
        CompleteCommand(cmd, RelationalBatchQuery.HasUsersetJoinRelation);
    }

    /// <inheritdoc />
    public virtual void AddHasAnyDirectRelationToBatch(DbBatch batch, string entityType, string[] entityIds,
        string relation, string subjectId, SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasAnyDirectRelation));
        WriteStringParam(cmd, "entity_type", entityType, 256);
        var arrayFragment = WriteNameArrayParam(cmd, "entity_ids", entityIds);
        WriteStringParam(cmd, "relation", relation, 64);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        SpliceArrayFragment(cmd, arrayFragment);
        CompleteCommand(cmd, RelationalBatchQuery.HasAnyDirectRelation);
    }

    /// <inheritdoc />
    public virtual void AddHasAnyOfDirectRelationsToBatch(DbBatch batch, string entityType, string entityId,
        string[] relationNames, string subjectId, SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasAnyOfDirectRelations));
        WriteStringParam(cmd, "entity_type", entityType, 256);
        WriteStringParam(cmd, "entity_id", entityId, 64);
        var arrayFragment = WriteNameArrayParam(cmd, "relations", relationNames);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        SpliceArrayFragment(cmd, arrayFragment);
        CompleteCommand(cmd, RelationalBatchQuery.HasAnyOfDirectRelations);
    }

    /// <inheritdoc />
    public virtual void AddHasAnyOfAttributesToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasAnyOfAttributes));
        WriteStringParam(cmd, "entity_type", entityType, 256);
        WriteStringParam(cmd, "entity_id", entityId, 64);
        var arrayFragment = WriteNameArrayParam(cmd, "attributes", attributeNames);
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        SpliceArrayFragment(cmd, arrayFragment);
        CompleteCommand(cmd, RelationalBatchQuery.HasAnyOfAttributes);
    }

    /// <inheritdoc />
    public virtual void AddGetRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
    {
        // Same filter-shape branch the non-batch GetRelations takes: no subject filters means the
        // narrower no-subject SQL variant, otherwise the full variant with nullable subject params.
        var noSubjectFilters = string.IsNullOrWhiteSpace(tupleFilter.SubjectId)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectRelation)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectType);

        var query = noSubjectFilters
            ? RelationalBatchQuery.GetRelations
            : RelationalBatchQuery.GetRelationsWithSubjectFilters;
        var cmd = NewCommand(batch, GetSql(query));
        WriteStringParam(cmd, "entity_type", tupleFilter.EntityType, 256);
        WriteStringParam(cmd, "entity_id", tupleFilter.EntityId, 64);
        WriteStringParam(cmd, "relation", tupleFilter.Relation, 64);
        if (!noSubjectFilters)
        {
            WriteNullableStringParam(cmd, "subject_id", tupleFilter.SubjectId, 64);
            WriteNullableStringParam(cmd, "subject_relation", tupleFilter.SubjectRelation, 64);
            WriteNullableStringParam(cmd, "subject_type", tupleFilter.SubjectType, 256);
        }
        WriteSnapTokenParam(cmd, "snap_token", tupleFilter.SnapToken);
        CompleteCommand(cmd, query);
    }

    /// <inheritdoc />
    public virtual void AddGetIndirectRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.GetIndirectRelations));
        WriteStringParam(cmd, "entity_type", tupleFilter.EntityType, 256);
        WriteStringParam(cmd, "entity_id", tupleFilter.EntityId, 64);
        WriteStringParam(cmd, "relation", tupleFilter.Relation, 64);
        WriteSnapTokenParam(cmd, "snap_token", tupleFilter.SnapToken);
        CompleteCommand(cmd, RelationalBatchQuery.GetIndirectRelations);
    }

    /// <inheritdoc />
    public virtual void AddGetAttributesSingleEntityToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.GetAttributesSingleEntity));
        WriteStringParam(cmd, "entity_type", entityType, 256);
        WriteStringParam(cmd, "entity_id", entityId, 64);
        var arrayFragment = WriteNameArrayParam(cmd, "attributes", attributeNames);
        WriteSnapTokenParam(cmd, "snap_token", snapToken);
        SpliceArrayFragment(cmd, arrayFragment);
        CompleteCommand(cmd, RelationalBatchQuery.GetAttributesSingleEntity);
    }
}
