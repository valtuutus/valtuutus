using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.LookupEntity;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// Answers with the default LookupEntity engine and checks the opt-in V2 engine returns the same
/// page for the same request, so every shared spec covers both while V2 is opt-in.
/// </summary>
public sealed class ComparingLookupEntityEngine(ILookupEntityEngine current, ILookupEntityEngine v2)
    : ILookupEntityEngine
{
    private const string V2Key = "lookup-entity-v2";

    public async Task<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        LookupEntityPage expected;
        try
        {
            expected = await current.LookupEntity(req, cancellationToken);
        }
        catch (Exception currentException)
        {
            // A request the default engine rejects must be rejected by V2 the same way.
            var rejection = async () => await v2.LookupEntity(req, cancellationToken);
            (await rejection.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(currentException.GetType());
            throw;
        }
        var actual = await v2.LookupEntity(req, cancellationToken);
        actual.EntityIds.Should().Equal(expected.EntityIds, "LookupEntity V2 must agree with the default engine");
        actual.ContinuationToken.Should().Be(expected.ContinuationToken);
        return expected;
    }

    /// <summary>
    /// Keeps the registered engine as the answer and adds V2 next to it under a key. Call after the
    /// provider is registered.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        var current = services.Last(d => d.ServiceType == typeof(ILookupEntityEngine) && !d.IsKeyedService);
        services.AddValtuutusLookupEntityV2();
        var v2 = services.Last(d => d.ServiceType == typeof(ILookupEntityEngine) && !d.IsKeyedService);

        services.Remove(v2);
        services.Add(new ServiceDescriptor(typeof(ILookupEntityEngine), V2Key, v2.ImplementationType!, v2.Lifetime));
        services.Add(new ServiceDescriptor(typeof(ILookupEntityEngine), sp => new ComparingLookupEntityEngine(
                (ILookupEntityEngine)ActivatorUtilities.CreateInstance(sp, current.ImplementationType!),
                sp.GetRequiredKeyedService<ILookupEntityEngine>(V2Key)),
            current.Lifetime));
    }
}
