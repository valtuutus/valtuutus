using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.Configuration;
using Valtuutus.Data.Tests.Shared;
using Valtuutus.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class LookupSubjectEngineSpecs : DataLookupSubjectEngineSpecs
{

    public LookupSubjectEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IServiceCollection services)
    {
        services.AddSqlServer();
    }
}