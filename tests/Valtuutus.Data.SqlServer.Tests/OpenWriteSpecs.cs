using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class OpenWriteSpecs(SqlServerFixture fixture)
    : BaseDbOpenWriteSpecs(fixture), IClassFixture<SqlServerFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
