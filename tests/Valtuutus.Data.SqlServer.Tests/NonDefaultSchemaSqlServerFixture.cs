using Xunit;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// Provisions the Valtuutus tables under a non-default SQL schema ("authz") so every query's
/// schema qualification is exercised end-to-end.
///
/// The <see cref="DbQueryCacheKey"/>-keyed ConcurrentDictionary cache lets this fixture share a process with the default-schema (dbo) tests.
/// </summary>
public class NonDefaultSchemaSqlServerFixture : SqlServerFixtureBase
{
    public override ValtuutusSqlServerOptions Options { get; } =
        new("authz", "transactions", "relation_tuples", "attributes");
}
