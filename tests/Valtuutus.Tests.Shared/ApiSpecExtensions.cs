using static PublicApiGenerator.ApiGenerator;
namespace Valtuutus.Tests.Shared;

public static class ApiSpecExtensions
{
    public static Task VerifyAssembly<T>()
    {
        return Verify(typeof(T).Assembly.GeneratePublicApi());
    }
}