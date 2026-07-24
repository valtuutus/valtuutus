using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Wraps whichever <c>ICheckEngine</c>/<c>ILookupEntityEngine</c>/<c>ILookupSubjectEngine</c> is
    /// currently registered (V1 or the opt-in V2 check engine) in a FusionCache-backed decorator.
    /// Call this after any engine-selection call (e.g. <c>AddValtuutusCheckV2</c>) so it decorates
    /// the engine actually in effect, rather than the other way around.
    /// </summary>
    /// <param name="builder">Valtuutus data builder</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddCaching(this IValtuutusDataBuilder builder)
    {
        RedirectToKeyedInner<ICheckEngine>(builder.Services, Consts.InnerCheckEngineKey);
        RedirectToKeyedInner<ILookupEntityEngine>(builder.Services, Consts.InnerLookupEntityEngineKey);
        RedirectToKeyedInner<ILookupSubjectEngine>(builder.Services, Consts.InnerLookupSubjectEngineKey);

        builder.Services.AddScoped<ICheckEngine, CachedCheckEngine>();
        builder.Services.AddScoped<ILookupEntityEngine, CachedLookupEntityEngine>();
        builder.Services.AddScoped<ILookupSubjectEngine, CachedLookupSubjectEngine>();

        builder.Options.OnDataWritten = async (sp, st) =>
        {
            var cache = sp.GetRequiredService<IFusionCache>();
            await cache.SetAsync(Consts.LatestSnapTokenKey, st, options =>
            {
                options.AllowBackgroundBackplaneOperations = false;
            });
        };

        return builder;
    }

    /// <summary>
    /// Takes whatever implementation is currently registered as <typeparamref name="TService"/> and
    /// re-registers it under a keyed slot, so a decorator can be registered as the plain
    /// <typeparamref name="TService"/> without needing to know which concrete engine it is wrapping.
    /// </summary>
    private static void RedirectToKeyedInner<TService>(IServiceCollection services, string key)
        where TService : class
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor?.ImplementationType is null)
        {
            throw new InvalidOperationException(
                $"AddCaching requires {typeof(TService).Name} to already be registered with a concrete " +
                "implementation type (e.g. via AddValtuutusCore, or AddValtuutusCheckV2 for ICheckEngine).");
        }

        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(typeof(TService), key, descriptor.ImplementationType, descriptor.Lifetime));
    }
}
