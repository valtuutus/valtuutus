using System.Runtime.CompilerServices;

namespace Valtuutus.Api.Tests;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifierSettings.ScrubLinesContaining("[assembly: ReleaseDateAttribute(");
        VerifierSettings.ScrubLinesContaining("[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(");
        UseProjectRelativeDirectory("verify");
        VerifierSettings.UniqueForTargetFrameworkAndVersion();
        VerifierSettings.InitializePlugins();    
    }
    
}