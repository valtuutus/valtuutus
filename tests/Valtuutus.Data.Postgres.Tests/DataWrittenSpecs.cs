using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class DataWrittenSpecs : BaseDbDataWrittenSpecs, IClassFixture<PostgresFixture>
{
    public DataWrittenSpecs(PostgresFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
