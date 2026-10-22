using System.Runtime.CompilerServices;

namespace Valtuutus.Data.MySql.Tests;

public static class ModuleInit
{
    // The MySQL and MariaDB copies of a shared suite carry the same class name, so both compare
    // against the one shared snapshot file of that suite.
    [ModuleInitializer]
    public static void Initialize() => VerifierSettings.DisableRequireUniquePrefix();
}
