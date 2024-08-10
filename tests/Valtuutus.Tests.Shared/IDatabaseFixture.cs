using Valtuutus.Data.Db;

namespace Valtuutus.Tests.Shared;

public interface IDatabaseFixture
{

    Task ResetDatabaseAsync();
}
public interface IWithDbConnectionFactory
{
    DbConnectionFactory DbFactory { get; }
}