using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class DataWrittenSpecs : BaseDbDataWrittenSpecs
{
    public DataWrittenSpecs(SqlServerFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
