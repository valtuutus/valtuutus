using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;
using Valtuutus.Data.BackgroundService;
using Valtuutus.Data.Db;
using Valtuutus.Data.InMemory;
using Valtuutus.Data.MySql;
using Valtuutus.Data.Postgres;
using Valtuutus.Data.SqlServer;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Api.Tests;

public class ApiSpecs
{
    [Fact]
    public Task ApproveCore()
    {
        return ApiSpecExtensions.VerifyAssembly<ICheckEngine>();
    }

    [Fact]
    public Task ApproveData()
    {
        return ApiSpecExtensions.VerifyAssembly<RateLimiterExecuter>();
    }
    
    [Fact]
    public Task ApproveDataDb()
    {
        return ApiSpecExtensions.VerifyAssembly<IDbDataWriterProvider>();
    }


    [Fact]
    public Task ApprovePostgres()
    {
        return ApiSpecExtensions.VerifyAssembly<PostgresDataWriterProvider>();
    }
    [Fact]
    public Task ApproveSqlServer()
    {
        return ApiSpecExtensions.VerifyAssembly<SqlServerDataWriterProvider>();
    }

    [Fact]
    public Task ApproveMySql()
    {
        return ApiSpecExtensions.VerifyAssembly<MySqlDataWriterProvider>();
    }
    
    [Fact]
    public Task ApproveInMemory()
    {
        return ApiSpecExtensions.VerifyAssembly<InMemoryProvider>();
    }

    [Fact]
    public Task ApproveDataBackgroundService()
    {
        return ApiSpecExtensions.VerifyAssembly<TombstoneReaperHostedService>();
    }

    [Fact]
    public Task ApprovePostgresWeasel()
    {
        return ApiSpecExtensions.VerifyAssembly<Valtuutus.Data.Postgres.Weasel.ValtuutusPostgresWeaselOptions>();
    }

    [Fact]
    public Task ApproveSqlServerWeasel()
    {
        return ApiSpecExtensions.VerifyAssembly<Valtuutus.Data.SqlServer.Weasel.ValtuutusSqlServerWeaselOptions>();
    }

    [Fact]
    public Task ApproveMySqlWeasel()
    {
        return ApiSpecExtensions.VerifyAssembly<Valtuutus.Data.MySql.Weasel.ValtuutusMySqlWeaselOptions>();
    }
}
