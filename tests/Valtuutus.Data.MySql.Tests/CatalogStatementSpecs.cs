using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// Runs every catalog statement, in both the bound-token and the batch-resolved catalog, on the
    /// server, which proves each compiled text parses and executes. Each name the statement reads is
    /// added as a dummy value and bound to the placeholders by <see cref="MySqlStatement.Bind"/>, on a
    /// connection with user variables off, so the session variable gets a dummy of its own and any
    /// other named reference left in the text fails instead of reading as NULL. The dummies come from
    /// the statement itself, so this says nothing about the provider's binders:
    /// <see cref="ParameterBindingSpecs"/> checks that they bind exactly the names their statements
    /// read.
    /// </summary>
    public abstract partial class CatalogStatementSpecsBase(MySqlFixtureBase fixture)
    {
        // The resolver assigns the session variable, so it runs only with user variables allowed.
        private static readonly HashSet<string> NonStatements =
        [
            nameof(MySqlQueries.ResolveLatestSnapTokenForBatch),
        ];

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
                .Where(p => p.PropertyType == typeof(MySqlStatement) && !NonStatements.Contains(p.Name));

        [Theory]
        [MemberData(nameof(Statements))]
        public async Task Catalog_statement_runs(string statement)
        {
            await ExecuteWithDummyParameters(StatementOf(MySqlQueries.Get(fixture.Options), statement));
        }

        [Theory]
        [MemberData(nameof(Statements))]
        public async Task Batch_resolved_catalog_statement_runs(string statement)
        {
            await ExecuteWithDummyParameters(StatementOf(MySqlQueries.GetBatchResolved(fixture.Options), statement));
        }

        public enum LevelsKind
        {
            RelationsLevels,
            RelationsLevelsFromSubject,
            UsersetLevels,
        }

        // Every level count each levels kind is built for, under the default hops per query.
        public static TheoryData<LevelsKind, int, bool> LevelsStatements()
        {
            var data = new TheoryData<LevelsKind, int, bool>();
            foreach (var kind in Enum.GetValues<LevelsKind>())
            {
                var statements = LevelsStatementsOf(kind, new ValtuutusMySqlOptions(MySqlServerType.MySql), false);
                for (var levels = 0; levels < statements.Length; levels++)
                {
                    if (statements[levels] is null)
                        continue;
                    data.Add(kind, levels, false);
                    data.Add(kind, levels, true);
                }
            }
            return data;
        }

        private static MySqlStatement?[] LevelsStatementsOf(LevelsKind kind, ValtuutusMySqlOptions options,
            bool batchResolved)
        {
            var snap = batchResolved ? MySqlQueries.BatchResolvedSnapToken : MySqlQueries.SnapTokenParameter;
            return kind switch
            {
                LevelsKind.RelationsLevels => MySqlLookupEntityBatchOps.RelationsLevelsStatements(options, snap, fromSubject: false),
                LevelsKind.RelationsLevelsFromSubject => MySqlLookupEntityBatchOps.RelationsLevelsStatements(options, snap, fromSubject: true),
                LevelsKind.UsersetLevels => MySqlLookupSubjectBatchOps.UsersetLevelsStatements(options, snap),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }

        [Theory]
        [MemberData(nameof(LevelsStatements))]
        public async Task Levels_statement_runs(LevelsKind kind, int levels, bool batchResolved)
        {
            var statement = LevelsStatementsOf(kind, fixture.Options, batchResolved)[levels]!;

            if (batchResolved)
                statement.Sql.Should().Contain(MySqlQueries.BatchResolvedSnapToken);
            else
                statement.ParameterNames.Should().Contain(SnapTokenName);
            await ExecuteWithDummyParameters(statement);
        }

        [Fact]
        public void Batch_resolved_statements_read_the_session_variable_instead_of_the_parameter()
        {
            var queries = MySqlQueries.GetBatchResolved(fixture.Options);
            var statements = StatementProperties().Select(p => (MySqlStatement)p.GetValue(queries)!)
                .Append(queries.ResolveLatestSnapTokenForBatch)
                .Append(FusedExpressionSql.BuildStatement(EveryLeafKind(), false, queries))
                .Append(FusedExpressionSql.BuildStatement(EveryLeafKind(), true, queries))
                .Concat(Enum.GetValues<LevelsKind>()
                    .SelectMany(kind => LevelsStatementsOf(kind, fixture.Options, batchResolved: true))
                    .OfType<MySqlStatement>())
                .ToList();

            statements.Should().NotContain(s => s.ParameterNames.Contains(SnapTokenName));
            statements.Should().Contain(s => s.Sql.Contains(MySqlQueries.BatchResolvedSnapToken));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Fused_statement_over_every_leaf_kind_runs(bool requireAll,
            bool batchResolved)
        {
            var leaves = EveryLeafKind();
            leaves.Select(l => l.Kind).Distinct().Should().BeEquivalentTo(Enum.GetValues<FusedLeafKind>());
            var queries = batchResolved
                ? MySqlQueries.GetBatchResolved(fixture.Options)
                : MySqlQueries.Get(fixture.Options);

            var statement = FusedExpressionSql.BuildStatement(leaves, requireAll, queries);

            await ExecuteWithDummyParameters(statement);
        }

        [Fact]
        public async Task Resolver_returns_the_latest_token_and_parks_it_for_the_batch()
        {
            var writer = new MySqlDataWriterProvider(fixture.DbFactory, new ServiceCollection().BuildServiceProvider(),
                new ValtuutusDataOptions(), fixture.Options);
            var written = await writer.Write([new RelationTuple("doc", "resolver", "owner", "user", "alice")], [], default);
            var ops = new MySqlBatchOps(fixture.DbFactory, new ValtuutusDataOptions(), fixture.Options);
            var batch = ops.CreateBatch();
            ops.AddResolveLatestSnapTokenToBatch(batch);
            ops.UsingBatchResolvedSnapToken.AddHasDirectRelationToBatch(batch, new RelationTupleFilter
            {
                EntityType = "doc", EntityId = "resolver", Relation = "owner", SnapToken = SnapToken.MinValue
            }, "alice");

            await using var reader = await ops.ExecuteBatchAsync(batch, default);

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be(written.Value);
            (await reader.ReadAsync()).Should().BeFalse();
            (await reader.NextResultAsync()).Should().BeTrue();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetBoolean(0).Should().BeTrue("the check reads the token the resolver parked");
            (await reader.NextResultAsync()).Should().BeFalse();
        }

        private static MySqlStatement StatementOf(MySqlQueries queries, string statement) =>
            (MySqlStatement)typeof(MySqlQueries).GetProperty(statement)!.GetValue(queries)!;

        private static FusedCheckLeaf[] EveryLeafKind() =>
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

        [Fact]
        public async Task An_unbound_parameter_fails_on_this_connection()
        {
            await using var connection = await OpenWithoutUserVariables();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM relation_tuples r WHERE r.entity_id = @entity_id";

            var act = () => command.ExecuteScalarAsync();

            await act.Should().ThrowAsync<MySqlException>();
        }

        [Fact]
        public async Task A_placeholder_without_a_parameter_fails()
        {
            await using var connection = await OpenWithoutUserVariables();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM relation_tuples r WHERE r.entity_id = ?";

            var act = () => command.ExecuteScalarAsync();

            await act.Should().ThrowAsync<MySqlException>();
        }

        // The session variable stays a named reference next to the placeholders; this connection
        // allows no user variables, so it gets a named dummy after the positional ones.
        private async Task ExecuteWithDummyParameters(MySqlStatement statement)
        {
            await using var connection = await OpenWithoutUserVariables();
            await using var command = connection.CreateCommand();
            foreach (var name in statement.ParameterNames)
                command.Parameters.Add(DummyParameter(name));
            statement.ApplyTo(command);
            if (statement.Sql.Contains(MySqlQueries.BatchResolvedSnapToken))
                command.Parameters.Add(new MySqlParameter(MySqlQueries.BatchResolvedSnapToken, MySqlDbType.String)
                {
                    Value = Ulid.NewUlid().ToString()
                });

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

        private static readonly string SnapTokenName = MySqlQueries.SnapTokenParameter[1..];

        // Lists of ids and names go in as JSON arrays; the snap token is a ULID; everything else is a
        // key. Any value works: the statements only have to run.
        private static MySqlParameter DummyParameter(string name)
        {
            if (JsonListPattern().IsMatch(name))
                return new MySqlParameter(name, MySqlDbType.JSON) { Value = """["x","y"]""" };
            if (name == SnapTokenName)
                return new MySqlParameter(name, MySqlDbType.String) { Value = Ulid.NewUlid().ToString() };
            return new MySqlParameter(name, MySqlDbType.VarChar) { Value = "x" };
        }

        [GeneratedRegex(@"^(\w+_ids|relations|attributes)(_\d+)?$")]
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
