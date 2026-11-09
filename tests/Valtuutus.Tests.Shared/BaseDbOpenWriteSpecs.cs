using System.Data.Common;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A write in the caller's transaction can stay open for as long as the caller likes. A check that
/// resolves the latest token, over data the write doesn't touch, answers without waiting for it.
/// </summary>
public abstract class BaseDbOpenWriteSpecs : IAsyncLifetime
{
    // A read the open write blocks would never finish; the bound turns that into a failure.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    protected BaseDbOpenWriteSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_check_without_a_token_does_not_wait_for_an_open_write()
    {
        var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
        AddSpecificProvider(services);
        await using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<IDbDataWriterProvider>();
        await writer.Write([new RelationTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace,
            "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob)], [], default);
        await using var connection = (DbConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await connection.OpenAsync();
        await using var open = await connection.BeginTransactionAsync();
        await writer.Write(connection, open, [new RelationTuple(TestsConsts.Workspaces.Identifier,
            TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)], [], default);

        using var timeout = new CancellationTokenSource(ReadTimeout);
        var result = await provider.GetRequiredService<ICheckEngine>().Check(new CheckRequest(
            TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "delete",
            TestsConsts.Users.Identifier, TestsConsts.Users.Bob), timeout.Token);

        result.Allowed.Should().BeTrue();
        await open.RollbackAsync();
    }
}
