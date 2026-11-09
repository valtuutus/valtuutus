using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class SettledSnapTokenSpecs(PostgresFixture fixture)
    : BaseDbSettledSnapTokenSpecs(fixture), IClassFixture<PostgresFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
