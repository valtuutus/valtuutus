using Valtuutus.Core;
using Valtuutus.Data;
using Valtuutus.Data.Db;
using Valtuutus.Data.InMemory;
using Valtuutus.Data.Postgres;
using Valtuutus.Data.SqlServer;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Api.Tests;

public class ApiSpecs
{
    [Fact]
    public Task ApproveCore()
    {
        return ApiSpecExtensions.VerifyAssembly<CheckEngine>();
    }

    [Fact]
    public Task ApproveData()
    {
        return ApiSpecExtensions.VerifyAssembly<RateLimiterExecuter>();
    }
    
    [Fact]
    public Task ApproveDataDb()
    {
        return ApiSpecExtensions.VerifyAssembly<JsonTypeHandler>();
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
    public Task ApproveInMemory()
    {
        return ApiSpecExtensions.VerifyAssembly<InMemoryProvider>();
    }
}