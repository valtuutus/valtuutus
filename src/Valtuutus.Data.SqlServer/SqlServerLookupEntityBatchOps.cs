using System.Data.Common;
using Microsoft.Data.SqlClient;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

/// <summary>
/// SqlServer commands for the LookupEntity V2 queries: the reader's own SQL text and parameter
/// writers. When the batch resolves its own token, each statement runs behind
/// <see cref="SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken"/>; both statement sets
/// are built once.
/// </summary>
internal sealed class SqlServerLookupEntityBatchOps : ILookupEntityBatchOps
{
    private readonly SqlServerDataReaderProvider.ReaderQueries _withParameter;
    private readonly SqlServerDataReaderProvider.ReaderQueries _batchResolved;

    public SqlServerLookupEntityBatchOps(IValtuutusDbOptions dbOptions)
    {
        _withParameter = SqlServerDataReaderProvider.GetQueries(dbOptions);
        _batchResolved = WithPrefix(_withParameter, SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken);
    }

    public void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from SqlConnection.CreateBatch (SqlServerBatchOps), so their commands are
        // SqlBatchCommands.
        var cmd = (SqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        var scoped = query.Scope is not null;
        switch (query.Kind)
        {
            case LookupEntityQueryKind.Relations:
            {
                var ids = AsArray(query.Ids);
                cmd.CommandText = SqlServerDataReaderProvider.RelationsWithSubjectIdsStatement(q, scoped, ids.Length == 1);
                SqlServerDataReaderProvider.AddRelationsWithSubjectIdsParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.RelationsMulti:
            {
                var ids = AsArray(query.Ids);
                cmd.CommandText = SqlServerDataReaderProvider.RelationsWithSubjectIdsMultiRelationStatement(q, scoped, ids.Length == 1);
                SqlServerDataReaderProvider.AddRelationsWithSubjectIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.Joined:
                cmd.CommandText = SqlServerDataReaderProvider.RelationsJoinedStatement(q, scoped);
                SqlServerDataReaderProvider.AddRelationsJoinedParameters(parameters, query.EntityType, query.Relation,
                    query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType,
                    query.Ids.First(), query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.Attributes:
                cmd.CommandText = SqlServerDataReaderProvider.GetAttributeInQuery(
                    SqlServerDataReaderProvider.AttributesStatementPrefix(q, scoped, hasEntityId: false),
                    query.Names.Length);
                SqlServerDataReaderProvider.AddAttributesParameters(parameters, query.EntityType, query.Names,
                    entityId: null, query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.AttributesWithIds:
                cmd.CommandText = SqlServerDataReaderProvider.GetAttributeInQuery(
                    q.SelectAttributesWithEntityIdsByEntityAttributesPrefix, query.Names.Length);
                SqlServerDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names, query.Ids, snapToken);
                break;
            case LookupEntityQueryKind.Attribute:
                cmd.CommandText = SqlServerDataReaderProvider.AttributeStatement(q, hasEntityId: false);
                SqlServerDataReaderProvider.AddAttributeParameters(parameters, query.EntityType, query.Names[0],
                    entityId: null, snapToken);
                break;
            case LookupEntityQueryKind.EntitiesExcluding:
                cmd.CommandText = q.GetEntityIdsExcluding;
                SqlServerDataReaderProvider.AddEntityIdsExcludingParameters(parameters, query.EntityType, query.Ids,
                    snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        batch.BatchCommands.Add(cmd);
    }

    private static string[] AsArray(IReadOnlyCollection<string> ids) => ids as string[] ?? ids.ToArray();

    // Every statement the LookupEntity choosers can return, behind a fixed prefix.
    private static SqlServerDataReaderProvider.ReaderQueries WithPrefix(
        SqlServerDataReaderProvider.ReaderQueries q, string prefix) => q with
    {
        GetRelationsWithSingleSubjectSnap = prefix + q.GetRelationsWithSingleSubjectSnap,
        GetRelationsWithMultiSubjectSnap = prefix + q.GetRelationsWithMultiSubjectSnap,
        GetRelationsWithSingleSubjectScoped = prefix + q.GetRelationsWithSingleSubjectScoped,
        GetRelationsWithMultiSubjectScoped = prefix + q.GetRelationsWithMultiSubjectScoped,
        GetRelationsWithSingleSubjectMultiRelationSnap = prefix + q.GetRelationsWithSingleSubjectMultiRelationSnap,
        GetRelationsWithMultiSubjectMultiRelationSnap = prefix + q.GetRelationsWithMultiSubjectMultiRelationSnap,
        GetRelationsWithSingleSubjectMultiRelationScoped = prefix + q.GetRelationsWithSingleSubjectMultiRelationScoped,
        GetRelationsWithMultiSubjectMultiRelationScoped = prefix + q.GetRelationsWithMultiSubjectMultiRelationScoped,
        GetRelationsJoined = prefix + q.GetRelationsJoined,
        GetRelationsJoinedScoped = prefix + q.GetRelationsJoinedScoped,
        GetAttributesDictScopedPrefix = prefix + q.GetAttributesDictScopedPrefix,
        SelectAttributesByEntityAttributesPrefix = prefix + q.SelectAttributesByEntityAttributesPrefix,
        SelectAttributesWithEntityIdsByEntityAttributesPrefix = prefix + q.SelectAttributesWithEntityIdsByEntityAttributesPrefix,
        SelectAttributesByEntityAttributeFilter = prefix + q.SelectAttributesByEntityAttributeFilter,
        SelectAttributesByEntityAttributeFilterWithEntityId = prefix + q.SelectAttributesByEntityAttributeFilterWithEntityId,
        SelectAttributesByEntityAttributesWithEntityIdPrefix = prefix + q.SelectAttributesByEntityAttributesWithEntityIdPrefix,
        GetEntityIdsExcluding = prefix + q.GetEntityIdsExcluding,
    };
}
