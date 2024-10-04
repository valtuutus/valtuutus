using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Data.SqlServer.Utils;
using Dapper;
using Microsoft.Data.SqlClient;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;
internal sealed class SqlServerDataReaderProvider : RateLimiterExecuter, IDataReaderProvider
{
    private const string UnformattedSelectAttributes = @"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM [{0}].[{1}] /**where**/";

    private const string UnformattedSelectRelations = @"SELECT 
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation 
                FROM [{0}].[{1}] /**where**/";

    private readonly DbConnectionFactory _connectionFactory;
    private static string? _formattedSelect1Attribute;
    private static string? _formattedGetLatestSnapTokenQuery;
    private static string? _formattedSelectAttributes;
    private static string? _formattedSelectRelations;
    private static readonly object Lock = new();

    public SqlServerDataReaderProvider(DbConnectionFactory connectionFactory, 
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        InitializeQueries(dbOptions);
    }

    private static void InitializeQueries(IValtuutusDbOptions dbOptions)
    {
        if (_formattedSelectAttributes == null || _formattedSelectRelations == null ||
            _formattedGetLatestSnapTokenQuery == null || _formattedSelect1Attribute == null)
        {
            lock (Lock)
            {
                if (_formattedSelectAttributes == null)
                {
                    _formattedSelectAttributes ??= string.Format(UnformattedSelectAttributes, dbOptions.Schema,
                        dbOptions.AttributesTableName);
                    _formattedSelectRelations ??= string.Format(UnformattedSelectRelations, dbOptions.Schema,
                        dbOptions.RelationsTableName);
                    _formattedGetLatestSnapTokenQuery ??= string.Format(
                        "SELECT TOP 1 id FROM [{0}].[{1}] ORDER BY created_at DESC",
                        dbOptions.Schema, dbOptions.TransactionsTableName);
                    _formattedSelect1Attribute ??= $"""
                                                        SELECT TOP 1
                                                        entity_type,
                                                        entity_id,
                                                        attribute,
                                                        value
                                                    FROM [{dbOptions.Schema}].[{dbOptions.AttributesTableName}] /**where**/
                                                    """;
                }
            }
        }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) => { 
#if NETSTANDARD2_0
            using var connection = (SqlConnection) _connectionFactory();
#else
            await using var connection = (SqlConnection) _connectionFactory();
#endif
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_formattedSelect1Attribute);
            
            return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(
                queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: ct));
        }, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) => { 

#if NETSTANDARD2_0
            using var connection = (SqlConnection) _connectionFactory();
#else
            await using var connection = (SqlConnection) _connectionFactory();
#endif
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_formattedSelectAttributes!);
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
        }, cancellationToken);
    
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_formattedSelectAttributes!);
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();


        return await ExecuteWithRateLimit(async (ct) => { 

#if NETSTANDARD2_0
            using var connection = (SqlConnection) _connectionFactory();
#else
            await using var connection = (SqlConnection) _connectionFactory();
#endif
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(_formattedSelectAttributes!);


            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();
        }, cancellationToken);
        
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();

            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(_formattedSelectAttributes!);
            
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }, cancellationToken);
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {
            using var connection = _connectionFactory();
            
            var res = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(_formattedGetLatestSnapTokenQuery!, cancellationToken: ct));
            return res != null ? new SnapToken(res) : (SnapToken?)null;
        }, cancellationToken);
    }

    public async Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) => { 

#if NETSTANDARD2_0
            using var connection = (SqlConnection) _connectionFactory();
#else
            await using var connection = (SqlConnection) _connectionFactory();
#endif
            var queryTemplate = new SqlBuilder()
                .FilterRelations(tupleFilter)
                .AddTemplate(_formattedSelectRelations!);
            
            var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();

            return res;
        }, cancellationToken);
       
    }
    
    public async Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) =>
        {

#if NETSTANDARD2_0
            using var connection = (SqlConnection) _connectionFactory();
#else
            await using var connection = (SqlConnection) _connectionFactory();
#endif
            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityRelationFilter, subjectType, entityIds, subjectRelation)
                .AddTemplate(_formattedSelectRelations!);
            
            var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();

            return res;
        }, cancellationToken);

    }
    
    public async Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,  IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit(async (ct) => { 

#if NETSTANDARD2_0
            using var connection = (SqlConnection) _connectionFactory();
#else
            await using var connection = (SqlConnection) _connectionFactory();
#endif
            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityFilter, subjectsIds, subjectType)
                .AddTemplate(_formattedSelectRelations!);

            var res = (await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                    queryTemplate.Parameters, cancellationToken: ct)))
                .ToList();

            return res;
        }, cancellationToken);
        
    }
}