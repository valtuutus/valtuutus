using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.LookupSubject;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// Answers with the default LookupSubject engine and checks the opt-in V2 engine returns the same
/// subjects for the same request, so every shared spec covers both while V2 is opt-in.
/// </summary>
public sealed class ComparingLookupSubjectEngine(ILookupSubjectEngine current, ILookupSubjectEngine v2)
    : ILookupSubjectEngine
{
    private const string V2Key = "lookup-subject-v2";

    public async Task<HashSet<string>> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        var expected = await current.Lookup(req, cancellationToken);
        var actual = await v2.Lookup(req, cancellationToken);
        actual.Should().BeEquivalentTo(expected, "LookupSubject V2 must agree with the default engine");
        return expected;
    }

    /// <summary>
    /// Keeps the registered engine as the answer and adds V2 next to it under a key. Call after the
    /// provider is registered.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        var current = services.Last(d => d.ServiceType == typeof(ILookupSubjectEngine) && !d.IsKeyedService);
        services.AddValtuutusLookupSubjectV2();
        var v2 = services.Last(d => d.ServiceType == typeof(ILookupSubjectEngine) && !d.IsKeyedService);

        services.Remove(v2);
        services.Add(new ServiceDescriptor(typeof(ILookupSubjectEngine), V2Key, v2.ImplementationType!, v2.Lifetime));
        services.Add(new ServiceDescriptor(typeof(ILookupSubjectEngine), sp => new ComparingLookupSubjectEngine(
                (ILookupSubjectEngine)ActivatorUtilities.CreateInstance(sp, current.ImplementationType!),
                sp.GetRequiredKeyedService<ILookupSubjectEngine>(V2Key)),
            current.Lifetime));
    }
}
