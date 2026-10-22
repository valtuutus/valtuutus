using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using MySqlConnector;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// Runs every reader statement with each parameter it names bound. An <c>@name</c> left unbound
    /// must fail loudly: with user variables allowed it would read as a NULL user variable and the
    /// statement would quietly match nothing. The connection here turns user variables off
    /// explicitly, whatever the fixture's connection string says, so an unbound name fails and a
    /// misspelt parameter name can't ship.
    /// </summary>
    public abstract partial class CatalogStatementSpecsBase(MySqlFixtureBase fixture)
    {
        // String members of the catalog that hold names rather than statements.
        private static readonly HashSet<string> NonStatements =
            [nameof(MySqlQueries.Snap), nameof(MySqlQueries.RelationsTable), nameof(MySqlQueries.AttributesTable)];

        public static TheoryData<string> Statements()
        {
            var data = new TheoryData<string>();
            foreach (var property in StatementProperties())
                data.Add(property.Name);
            return data;
        }

        private static IEnumerable<PropertyInfo> StatementProperties() =>
            typeof(MySqlQueries)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && !NonStatements.Contains(p.Name));

        [Theory]
        [MemberData(nameof(Statements))]
        public async Task Catalog_statement_binds_every_parameter_it_names(string statement)
        {
            var queries = MySqlQueries.Get(fixture.Options);
            var sql = (string)typeof(MySqlQueries).GetProperty(statement)!.GetValue(queries)!;

            await ExecuteWithDummyParameters(sql);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Fused_statement_over_every_leaf_kind_binds_every_parameter_it_names(bool requireAll)
        {
            FusedCheckLeaf[] leaves =
            [
                new(FusedLeafKind.Direct, Negate: true, ["owner"]),
                new(FusedLeafKind.Direct, Negate: false, ["viewer"]),
                new(FusedLeafKind.MultiDirect, Negate: false, ["editor", "admin"], RequireAll: false),
                new(FusedLeafKind.MultiDirect, Negate: true, ["editor", "admin", "editor"], RequireAll: true),
                new(FusedLeafKind.Attribute, Negate: false, ["public"]),
                new(FusedLeafKind.MultiAttribute, Negate: true, ["a0", "a1"], RequireAll: false),
                new(FusedLeafKind.MultiAttribute, Negate: false, ["a0", "a1"], RequireAll: true),
                new(FusedLeafKind.TupleToUserSet, Negate: false, ["parent"],
                    TtuSubEntityType: "organization", TtuComputedRelation: "admin"),
                new(FusedLeafKind.TupleToUserSet, Negate: true, ["parent"],
                    TtuSubEntityType: "organization", TtuComputedRelation: "member"),
            ];
            leaves.Select(l => l.Kind).Distinct().Should().BeEquivalentTo(Enum.GetValues<FusedLeafKind>());

            var sql = FusedExpressionSql.BuildStatement(leaves, requireAll, MySqlQueries.Get(fixture.Options));

            await ExecuteWithDummyParameters(sql);
        }

        [Fact]
        public async Task An_unbound_parameter_fails_on_this_connection()
        {
            var act = () => ExecuteWithDummyParameters("SELECT 1 FROM relation_tuples r WHERE r.entity_id = @entity_id", bind: false);

            await act.Should().ThrowAsync<MySqlException>();
        }

        private async Task ExecuteWithDummyParameters(string sql, bool bind = true)
        {
            await using var connection = await OpenWithoutUserVariables();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (bind)
            {
                foreach (var name in ParameterNames(sql))
                    command.Parameters.Add(DummyParameter(name));
            }

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
            }
        }

        private async Task<MySqlConnection> OpenWithoutUserVariables()
        {
            // A connection that has never been opened still carries the password in its string.
            await using var probe = (MySqlConnection)fixture.DbFactory();
            var connectionString = new MySqlConnectionStringBuilder(probe.ConnectionString)
            {
                AllowUserVariables = false
            }.ConnectionString;
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }

        private static IEnumerable<string> ParameterNames(string sql) =>
            ParameterPattern().Matches(sql).Select(m => m.Value).Distinct(StringComparer.Ordinal);

        // Lists of ids and names go in as JSON arrays; the snap token is a ULID; everything else is a
        // key. Any value works: the statements only have to run.
        private static MySqlParameter DummyParameter(string name)
        {
            if (JsonListPattern().IsMatch(name))
                return new MySqlParameter(name, MySqlDbType.JSON) { Value = """["x","y"]""" };
            if (name == "@snap_token")
                return new MySqlParameter(name, MySqlDbType.String) { Value = Ulid.NewUlid().ToString() };
            return new MySqlParameter(name, MySqlDbType.VarChar) { Value = "x" };
        }

        [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*")]
        private static partial Regex ParameterPattern();

        [GeneratedRegex(@"^@(\w+_ids|relations|attributes)(_\d+)?$")]
        private static partial Regex JsonListPattern();
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class CatalogStatementSpecs(MySqlFixture fixture) : CatalogStatementSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class CatalogStatementSpecs(MariaDbFixture fixture) : CatalogStatementSpecsBase(fixture);
}
