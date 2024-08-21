using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds InMemory data reader and writer to the service collection
    /// </summary>
    /// <param name="builder">Valtuutus data builder</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddCaching(this IValtuutusDataBuilder builder)
    {
        builder.Services.RemoveAll(typeof(ICheckEngine));
        builder.Services.RemoveAll(typeof(ILookupEntityEngine));
        builder.Services.RemoveAll(typeof(ILookupSubjectEngine));
        builder.Services.AddScoped<CheckEngine>();
        builder.Services.AddScoped<LookupEntityEngine>();
        builder.Services.AddScoped<LookupSubjectEngine>();
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
}