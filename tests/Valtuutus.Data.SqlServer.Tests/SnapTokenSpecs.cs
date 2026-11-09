using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

public class SnapTokenSpecs : BaseSnapTokenSpecs, IClassFixture<SqlServerFixture>
{
    public SnapTokenSpecs(SqlServerFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}