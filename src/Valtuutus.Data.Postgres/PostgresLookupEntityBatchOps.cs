using System.Data.Common;
using Npgsql;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// Postgres commands for the LookupEntity V2 queries: the reader's own catalog statements and
/// parameter writers, from the batch-resolved-token catalog when the batch resolves its own token.
/// </summary>
internal sealed class PostgresLookupEntityBatchOps(ValtuutusPostgresOptions dbOptions) : ILookupEntityBatchOps
{
    private readonly PostgresDataReaderProvider.ReaderQueries _withParameter =
        PostgresDataReaderProvider.GetQueries(dbOptions);
    private readonly PostgresDataReaderProvider.ReaderQueries _batchResolved =
        PostgresDataReaderProvider.GetBatchResolvedSnapTokenQueries(dbOptions);

    public void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from NpgsqlDataSource.CreateBatch (PostgresBatchOps), so their commands are
        // NpgsqlBatchCommands.
        var cmd = (NpgsqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        var scoped = query.Scope is not null;
        PgStatement statement;
        switch (query.Kind)
        {
            case LookupEntityQueryKind.Relations:
            {
                var ids = AsArray(query.Ids);
                statement = PostgresDataReaderProvider.RelationsWithSubjectIdsStatement(q, scoped, ids.Length == 1);
                PostgresDataReaderProvider.AddRelationsWithSubjectIdsParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.RelationsMulti:
            {
                var ids = AsArray(query.Ids);
                statement = PostgresDataReaderProvider.RelationsWithSubjectIdsMultiRelationStatement(q, scoped, ids.Length == 1);
                PostgresDataReaderProvider.AddRelationsWithSubjectIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.Joined:
                statement = PostgresDataReaderProvider.RelationsJoinedStatement(q, scoped);
                PostgresDataReaderProvider.AddRelationsJoinedParameters(parameters, query.EntityType, query.Relation,
                    query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType,
                    query.Ids.First(), query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.Attributes:
                statement = PostgresDataReaderProvider.AttributesStatement(q, scoped);
                PostgresDataReaderProvider.AddAttributesParameters(parameters, query.EntityType, query.Names,
                    entityId: null, query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.AttributesWithIds:
                statement = q.SelectAttributesWithEntityIdsByEntityAttributesFilter;
                PostgresDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names, query.Ids, snapToken);
                break;
            case LookupEntityQueryKind.Attribute:
                statement = q.SelectAttributesByEntityAttributeFilter;
                PostgresDataReaderProvider.AddAttributeParameters(parameters, query.EntityType, query.Names[0],
                    entityId: null, snapToken);
                break;
            case LookupEntityQueryKind.EntitiesExcluding:
                statement = q.GetEntityIdsExcluding;
                PostgresDataReaderProvider.AddEntityIdsExcludingParameters(parameters, query.EntityType, query.Ids,
                    snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        cmd.CommandText = statement.Sql;
        statement.Bind(parameters);
        batch.BatchCommands.Add(cmd);
    }

    private static string[] AsArray(IReadOnlyCollection<string> ids) => ids as string[] ?? ids.ToArray();
}
