using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
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
        await using var db = (SqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();

        var relations = new List<RelationTuple>();
        await using (var command = db.CreateCommand())
        {
            command.CommandText = """
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM [authz].[relation_tuples] WHERE deleted_tx_id IS NULL
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                relations.Add(new RelationTuple(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
        }

        var attributes = new List<AttributeTuple>();
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT entity_type, entity_id, attribute, value FROM [authz].[attributes] WHERE deleted_tx_id IS NULL";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                attributes.Add(new AttributeTuple(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    JsonNode.Parse(reader.GetString(3))!.AsValue()));
            }
        }

        return (relations.ToArray(), attributes.ToArray());
    }
}
