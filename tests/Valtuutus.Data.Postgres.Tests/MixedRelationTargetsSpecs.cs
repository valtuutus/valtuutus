using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class MixedRelationTargetsSpecs(PostgresFixture fixture)
    : BaseMixedRelationTargetsSpecs(fixture), IClassFixture<PostgresFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
