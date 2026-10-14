using System.Data.Common;
using Microsoft.Data.SqlClient;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

/// <summary>
/// SqlServer commands for the LookupSubject V2 queries: the reader's own SQL text and parameter
/// writers. When the batch resolves its own token, each statement runs behind
/// <see cref="SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken"/>; both statement sets
/// are built once.
/// </summary>
internal sealed class SqlServerLookupSubjectBatchOps : ILookupSubjectBatchOps
{
    private readonly Statements _withParameter;
    private readonly Statements _batchResolved;

    public SqlServerLookupSubjectBatchOps(IValtuutusDbOptions dbOptions)
    {
        var q = SqlServerDataReaderProvider.GetQueries(dbOptions);
        _withParameter = new Statements(q, "");
        _batchResolved = new Statements(q, SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken);
    }

    public void AddToBatch(DbBatch batch, LookupSubjectQuery query, SnapToken? snapToken)
    {
        var statements = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from SqlConnection.CreateBatch (SqlServerBatchOps), so their commands are
        // SqlBatchCommands.
        var cmd = (SqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        switch (query.Kind)
        {
            case LookupSubjectQueryKind.Relations:
                cmd.CommandText = statements.RelationsWithEntityIds;
                SqlServerDataReaderProvider.AddRelationsWithEntityIdsParameters(parameters,
                    query.EntityType, query.Relation, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.RelationsMulti:
                cmd.CommandText = statements.RelationsWithEntityIdsMultiRelation;
                SqlServerDataReaderProvider.AddRelationsWithEntityIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.Joined:
                cmd.CommandText = statements.RelationsJoinedByEntityIds;
                SqlServerDataReaderProvider.AddRelationsJoinedByEntityIdsParameters(parameters,
                    query.EntityType, query.Relation, query.Ids, query.SubEntityType, query.SubjectRelation!, snapToken);
                break;
            case LookupSubjectQueryKind.Attributes:
                cmd.CommandText = SqlServerDataReaderProvider.GetAttributeInQuery(
                    statements.AttributesWithEntityIdsPrefix, query.Names.Length);
                SqlServerDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters,
                    query.EntityType, query.Names, query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.Attribute:
                cmd.CommandText = statements.AttributeWithEntityIds;
                SqlServerDataReaderProvider.AddAttributeWithEntityIdsParameters(parameters,
                    query.EntityType, query.Names[0], query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.SubjectsExcluding:
                cmd.CommandText = statements.SubjectIdsExcluding;
                SqlServerDataReaderProvider.AddSubjectIdsExcludingParameters(parameters,
                    query.SubjectType, query.Ids, snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        batch.BatchCommands.Add(cmd);
    }

    // The reader's statements behind a fixed prefix: empty for the parameter-bound set.
    private sealed class Statements(SqlServerDataReaderProvider.ReaderQueries q, string prefix)
    {
        public string RelationsWithEntityIds { get; } = prefix + q.SelectRelationsWithEntityIds;
        public string RelationsWithEntityIdsMultiRelation { get; } = prefix + q.GetRelationsWithEntityIdsMultiRelationSnap;
        public string RelationsJoinedByEntityIds { get; } = prefix + q.GetRelationsJoinedByEntityIds;
        public string AttributesWithEntityIdsPrefix { get; } = prefix + q.SelectAttributesWithEntityIdsByEntityAttributesPrefix;
        public string AttributeWithEntityIds { get; } = prefix + q.SelectAttributesWithEntityIdsByAttributeFilter;
        public string SubjectIdsExcluding { get; } = prefix + q.GetSubjectIdsExcluding;
    }
}
