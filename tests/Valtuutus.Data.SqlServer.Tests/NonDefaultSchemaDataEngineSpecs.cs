using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerAuthzSpec")]
public sealed class NonDefaultSchemaDataEngineSpecs : BaseDataEngineSpecs
{
    public NonDefaultSchemaDataEngineSpecs(NonDefaultSchemaSqlServerFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(
            _ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusSqlServerOptions(NonDefaultSchemaSqlServerFixture.Schema,
                "transactions", "relation_tuples", "attributes"));
    }

    protected override async Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        using var db = ((IWithDbConnectionFactory)Fixture).DbFactory();
        var relations = (await db.QueryAsync<RelationTuple>("""
            SELECT  entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id,
                    subject_relation from [authz].[relation_tuples] where deleted_tx_id is null
            """)).ToArray();
        var attributes =
            (await db.QueryAsync<AttributeTuple>("select entity_type, entity_id, attribute,value from [authz].[attributes] where deleted_tx_id is null")).ToArray();

        return (relations, attributes);
    }
}
