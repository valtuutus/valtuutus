using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class WriterClockSpecs : BaseDbWriterClockSpecs, IClassFixture<PostgresFixture>
{
    public WriterClockSpecs(PostgresFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
