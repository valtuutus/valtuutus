using System.Runtime.CompilerServices;

namespace Valtuutus.Lang.SourceGen.Tests;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifySourceGenerators.Initialize();
    }
}