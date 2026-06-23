using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

// Drives the shared data-engine suite (relation/attribute write, upsert, delete, snap-token) against a real
// YugabyteDB node via AddYugabyte — proving the INSERT/UPDATE writer round-trips through the Postgres reader
// where the base COPY/MERGE writer cannot run at all (SQLSTATE 0A000).
[Collection("YugabyteSpec")]
public class YugabyteDataEngineSpecs : BaseDataEngineSpecs
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddYugabyte(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
    }

    protected override async Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        using var db = ((IWithDbConnectionFactory)Fixture).DbFactory();
        var relations = (await db.QueryAsync<RelationTuple>(
            """
            SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
            FROM public.relation_tuples WHERE deleted_tx_id IS NULL
            """)).ToArray();
        var attributes = (await db.QueryAsync<AttributeTuple>(
            "SELECT entity_type, entity_id, attribute, value FROM public.attributes WHERE deleted_tx_id IS NULL")).ToArray();

        return (relations, attributes);
    }

    public YugabyteDataEngineSpecs(YugabyteFixture fixture) : base(fixture) { }
}
