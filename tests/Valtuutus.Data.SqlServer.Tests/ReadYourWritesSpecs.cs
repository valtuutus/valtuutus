using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

// Read committed snapshot: without it, reads of the rows the open write changed would wait for it.
public sealed class ReadYourWritesSpecs(ReadCommittedSnapshotSqlServerFixture fixture)
    : BaseDbReadYourWritesSpecs(fixture), IClassFixture<ReadCommittedSnapshotSqlServerFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
