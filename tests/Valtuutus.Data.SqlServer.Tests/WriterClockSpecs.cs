using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class WriterClockSpecs : BaseDbWriterClockSpecs
{
    public WriterClockSpecs(SqlServerFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
