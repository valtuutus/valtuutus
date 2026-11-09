using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class ApplyValtuutusSchemaSpecs
{
    [Fact]
    public async Task Every_registered_applier_runs_once_in_registration_order()
    {
        var applied = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<ISchemaApplier>(new RecordingApplier("first", applied));
        services.AddSingleton<ISchemaApplier>(new RecordingApplier("second", applied));
        await using var provider = services.BuildServiceProvider();

        await provider.ApplyValtuutusSchemaAsync();

        applied.Should().Equal("first", "second");
    }

    [Fact]
    public async Task Applying_without_a_schema_package_names_the_registrations_to_add()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => provider.ApplyValtuutusSchemaAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AddPostgresWeasel(), AddSqlServerWeasel() or AddMySqlWeasel()*");
    }

    private sealed class RecordingApplier(string name, List<string> applied) : ISchemaApplier
    {
        public Task ApplyAsync(CancellationToken cancellationToken)
        {
            applied.Add(name);
            return Task.CompletedTask;
        }
    }
}
