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
/// parameter on Postgres, a table-valued parameter on SqlServer; a provider with neither
/// falls back to per-value parameter-list expansion). Adding a new batchable
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
    /// as array-taking on <see cref="RelationalBatchQuery"/> contain a <c>{0}</c> placeholder
    /// that this class fills with <see cref="WriteNameArrayParam"/>'s returned fragment.
    /// </summary>
    protected abstract string GetSql(RelationalBatchQuery query);

    /// <summary>
    /// Writes a name/id array argument onto <paramref name="cmd"/> and returns the SQL fragment
    /// that references it — the text substituted into the catalog SQL's <c>{0}</c> placeholder.
    /// Postgres adds one native array parameter and returns <c>@baseName</c>; SqlServer adds one
    /// table-valued parameter and returns its parameter name (the catalog SQL text already
    /// hardcodes the containing subquery, so the <c>{0}</c> substitution is a no-op there); a
    /// provider with neither adds one parameter per value and returns the expanded list.
    /// </summary>
    protected abstract string WriteNameArrayParam(DbBatchCommand cmd, string baseName, string[] values);

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
    /// Appends the snapshot-token parameter. Its own hook (not <see cref="WriteStringParam"/>)
    /// because providers type it differently from ordinary varchar columns (fixed-width char on
    /// Postgres).
    /// </summary>
    protected virtual void WriteSnapTokenParam(DbBatchCommand cmd, string name, SnapToken snapToken)
        => AddParam(cmd, name, snapToken.Value);

    /// <inheritdoc />
    public virtual void AddHasDirectRelationToBatch(DbBatch batch, RelationTupleFilter tupleFilter, string subjectId)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.HasDirectRelation));
        WriteStringParam(cmd, "entity_type", tupleFilter.EntityType, 256);
        WriteStringParam(cmd, "entity_id", tupleFilter.EntityId, 64);
        WriteStringParam(cmd, "relation", tupleFilter.Relation, 64);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
        WriteSnapTokenParam(cmd, "snap_token", tupleFilter.SnapToken);
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
        WriteStringParam(cmd, "computed_relation", computedRelation, 64);
        WriteStringParam(cmd, "subject_type", subjectType, 256);
        WriteStringParam(cmd, "subject_id", subjectId, 64);
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
        cmd.CommandText = string.Format(CultureInfo.InvariantCulture, cmd.CommandText, arrayFragment);
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
        cmd.CommandText = string.Format(CultureInfo.InvariantCulture, cmd.CommandText, arrayFragment);
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
        cmd.CommandText = string.Format(CultureInfo.InvariantCulture, cmd.CommandText, arrayFragment);
    }

    /// <inheritdoc />
    public virtual void AddGetRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
    {
        // Same filter-shape branch the non-batch GetRelations takes: no subject filters means the
        // narrower no-subject SQL variant, otherwise the full variant with nullable subject params.
        var noSubjectFilters = string.IsNullOrWhiteSpace(tupleFilter.SubjectId)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectRelation)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectType);

        var cmd = NewCommand(batch, GetSql(noSubjectFilters
            ? RelationalBatchQuery.GetRelations
            : RelationalBatchQuery.GetRelationsWithSubjectFilters));
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
    }

    /// <inheritdoc />
    public virtual void AddGetIndirectRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
    {
        var cmd = NewCommand(batch, GetSql(RelationalBatchQuery.GetIndirectRelations));
        WriteStringParam(cmd, "entity_type", tupleFilter.EntityType, 256);
        WriteStringParam(cmd, "entity_id", tupleFilter.EntityId, 64);
        WriteStringParam(cmd, "relation", tupleFilter.Relation, 64);
        WriteSnapTokenParam(cmd, "snap_token", tupleFilter.SnapToken);
    }
}
