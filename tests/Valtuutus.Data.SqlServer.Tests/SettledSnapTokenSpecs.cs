using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

// Read committed snapshot: without it, the specs' reads of the rows an open write changed would wait
// for that write, which the specs keep open on purpose.
public sealed class SettledSnapTokenSpecs(ReadCommittedSnapshotSqlServerFixture fixture)
    : BaseDbSettledSnapTokenSpecs(fixture), IClassFixture<ReadCommittedSnapshotSqlServerFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
