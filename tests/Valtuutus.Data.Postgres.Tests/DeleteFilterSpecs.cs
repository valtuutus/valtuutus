using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class DeleteFilterSpecs(PostgresFixture fixture) : BaseDeleteFilterSpecs(fixture)
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
