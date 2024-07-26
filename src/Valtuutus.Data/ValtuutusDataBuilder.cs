using Microsoft.Extensions.DependencyInjection;

namespace Valtuutus.Data;

public interface IValtuutusDataBuilder
{
    public IServiceCollection Services { get; }
    
    public ValtuutusDataOptions Options { get; }
}

internal class ValtuutusDataBuilder : IValtuutusDataBuilder
{
    public ValtuutusDataBuilder(IServiceCollection services)
    {
        Services = services;
        Options = new();
    }

    public IServiceCollection Services { get; }
    public ValtuutusDataOptions Options { get; }
}