using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class DeleteFilterSpecs(PostgresFixture fixture) : BaseDeleteFilterSpecs(fixture), IClassFixture<PostgresFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
