using Valtuutus.Data.Configuration;

namespace Valtuutus.Data.Tests.Shared;

public interface IDatabaseFixture
{
     DbConnectionFactory DbFactory { get; }

    Task ResetDatabaseAsync();
}