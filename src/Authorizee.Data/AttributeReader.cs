using Authorizee.Core;
using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Data.Configuration;
using Authorizee.Data.Utils;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Authorizee.Data;

public class AttributeReader(DbConnectionFactory connectionFactory, ILogger<RelationTupleReader> logger)
    : IAttributeReader
{
    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterAttributes(filter)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM attributes /**where**/
                LIMIT 1");

        logger.LogDebug("Querying attributes tuples with filter: {filter}", filter);

        return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
            queryTemplate.Parameters, cancellationToken: ct));
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterAttributes(filter)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM attributes /**where**/");

        logger.LogDebug("Querying attributes tuples with filter: {filter}", filter);

        return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
    }

    public async Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        using var connection = connectionFactory();

        var queryTemplate = new SqlBuilder()
            .FilterAttributes(filter, entitiesIds)
            .AddTemplate(@"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM attributes /**where**/");

        logger.LogDebug("Querying attributes tuples with filter: {filter}", filter);

        return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: ct)))
            .ToList();
    }
}