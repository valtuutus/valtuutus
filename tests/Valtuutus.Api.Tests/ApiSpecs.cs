using Valtuutus.Core;
using Valtuutus.Data;
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
}