using Authorizee.Data.Configuration;

namespace Authorizee.Data.Tests.Shared;

public interface IDatabaseFixture
{
     DbConnectionFactory DbFactory { get; }

    Task ResetDatabaseAsync();
}