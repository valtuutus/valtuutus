using Valtuutus.Data.Db;

namespace Valtuutus.Data.Tests.Shared;

public interface IDatabaseFixture
{

    Task ResetDatabaseAsync();
}
public interface IWithDbConnectionFactory
{
    DbConnectionFactory DbFactory { get; }
}