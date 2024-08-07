﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Valtuutus.Data;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Add Valtuutus database services
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddValtuutusData(this IServiceCollection services)
    {
        var builder = new ValtuutusDataBuilder(services);
        services.TryAddSingleton(builder.Options);
        return builder;
    }

    
    /// <summary>
    /// Adds a limit to the number of concurrent queries that can be executed in a single check request
    /// </summary>
    /// <param name="builder">Valtuutus data builder</param>
    /// <param name="limit">The number of concurrent queries</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException">Throws if receives a limit smaller or equals to zero.</exception>
    public static IValtuutusDataBuilder AddConcurrentQueryLimit(this IValtuutusDataBuilder builder, int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentException("Limit should be greater than 0");
        }
        builder.Options.MaxConcurrentQueries = limit;

        return builder;
    }
}