using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Lang.SchemaReaders;

namespace Valtuutus.Core.Configuration;

public static class ConfigureSchema
{
    /// <summary>
    /// Add Core Valtuutus services
    /// </summary>
    /// <param name="services">Service colletion</param>
    /// <param name="schemaText">Text representation of the schema</param>
    /// <param name="compiledFunctions">
    /// Optional map of schema function name to a build-time-compiled delegate (e.g. the source-generated
    /// <c>SchemaFunctionsGen.All</c>). When a function name is present here, its delegate is used directly
    /// instead of building and compiling a System.Linq.Expressions tree at schema-load time — avoids the
    /// per-call interpreter cost under NativeAOT. Functions not present in the map still fall back to the
    /// existing Expression-tree path, so this also covers schemas loaded from a source not known at compile
    /// time (DB, admin API, etc).
    /// </param>
    /// <returns></returns>
    public static IServiceCollection AddValtuutusCore(
        this IServiceCollection services,
        string schemaText,
        IReadOnlyDictionary<string, Func<IDictionary<string, object?>, bool>>? compiledFunctions = null)
    {
        var builder = new SchemaReader(compiledFunctions);
        var result = builder.Parse(schemaText);
        if (result.IsT1)
        {
            throw new InvalidOperationException(string.Join(",", result.AsT1.Select(x => x.ToString())));
        }
        var schema = result.AsT0;
        services.AddSingleton(schema);
        services.AddScoped<ICheckEngine, CheckEngine>();
        services.AddScoped<ILookupEntityEngine, LookupEntityEngine>();
        services.AddScoped<ILookupSubjectEngine,LookupSubjectEngine>();

        return services;
    }

    /// <summary>
    /// Add Core Valtuutus services
    /// </summary>
    /// <param name="services">Service colletion</param>
    /// <param name="stream">Stream of the representation of the schema</param>
    /// <param name="compiledFunctions">
    /// Optional map of schema function name to a build-time-compiled delegate. See the <c>string schemaText</c>
    /// overload for details.
    /// </param>
    /// <returns></returns>
    public static IServiceCollection AddValtuutusCore(
        this IServiceCollection services,
        Stream stream,
        IReadOnlyDictionary<string, Func<IDictionary<string, object?>, bool>>? compiledFunctions = null)
    {
        var builder = new SchemaReader(compiledFunctions);
        var result = builder.Parse(stream);
        if (result.IsT1)
        {
            throw new InvalidOperationException(string.Join(",", result.AsT1.Select(x => x.ToString())));
        }
        var schema = result.AsT0;
        services.AddSingleton(schema);
        services.AddScoped<ICheckEngine, CheckEngine>();
        services.AddScoped<ILookupEntityEngine, LookupEntityEngine>();
        services.AddScoped<ILookupSubjectEngine,LookupSubjectEngine>();

        return services;
    }

}
