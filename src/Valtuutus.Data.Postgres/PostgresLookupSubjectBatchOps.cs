using System.Data.Common;
using Npgsql;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// Postgres commands for the LookupSubject V2 queries: the reader's own catalog statements and
/// parameter writers, from the batch-resolved-token catalog when the batch resolves its own token.
/// </summary>
internal sealed class PostgresLookupSubjectBatchOps(ValtuutusPostgresOptions dbOptions) : ILookupSubjectBatchOps
{
    private readonly PostgresDataReaderProvider.ReaderQueries _withParameter =
        PostgresDataReaderProvider.GetQueries(dbOptions);
    private readonly PostgresDataReaderProvider.ReaderQueries _batchResolved =
        PostgresDataReaderProvider.GetBatchResolvedSnapTokenQueries(dbOptions);

    public void AddToBatch(DbBatch batch, LookupSubjectQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from NpgsqlDataSource.CreateBatch (PostgresBatchOps), so their commands are
        // NpgsqlBatchCommands.
        var cmd = (NpgsqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        PgStatement statement;
        switch (query.Kind)
        {
            case LookupSubjectQueryKind.Relations:
                statement = q.SelectRelationsWithEntityIds;
                PostgresDataReaderProvider.AddRelationsWithEntityIdsParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.RelationsMulti:
                statement = q.GetRelationsWithEntityIdsMultiRelationSnap;
                PostgresDataReaderProvider.AddRelationsWithEntityIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.Joined:
                statement = q.GetRelationsJoinedByEntityIds;
                PostgresDataReaderProvider.AddRelationsJoinedByEntityIdsParameters(parameters, query.EntityType,
                    query.Relation, query.Ids, query.SubEntityType, query.SubjectRelation!, snapToken);
                break;
            case LookupSubjectQueryKind.Attributes:
                statement = q.SelectAttributesWithEntityIdsByEntityAttributesFilter;
                PostgresDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names, query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.Attribute:
                statement = q.SelectAttributesWithEntityIdsByAttributeFilter;
                PostgresDataReaderProvider.AddAttributeWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names[0], query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.SubjectsExcluding:
                statement = q.GetSubjectIdsExcluding;
                PostgresDataReaderProvider.AddSubjectIdsExcludingParameters(parameters, query.SubjectType, query.Ids,
                    snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        cmd.CommandText = statement.Sql;
        statement.Bind(parameters);
        batch.BatchCommands.Add(cmd);
    }
}
