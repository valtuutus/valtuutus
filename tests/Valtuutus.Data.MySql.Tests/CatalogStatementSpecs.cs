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
    /// server, which proves each one parses and executes. Each <c>@name</c> the statement text reads
    /// is bound to a dummy value, on a connection with user variables off so a name the dummies miss
    /// fails instead of reading as NULL. The dummies come from the text itself, so this says nothing
    /// about the provider's binders: <see cref="ParameterBindingSpecs"/> checks that they bind
    /// exactly the names their statements read.
    /// </summary>
    public abstract partial class CatalogStatementSpecsBase(MySqlFixtureBase fixture)
    {
        // String members of the catalog that hold names rather than statements, and the resolver,
        // which assigns the session variable and so runs only with user variables allowed.
        private static readonly HashSet<string> NonStatements =
        [
            nameof(MySqlQueries.Snap), nameof(MySqlQueries.RelationsTable), nameof(MySqlQueries.AttributesTable),
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
                .Where(p => p.PropertyType == typeof(string) && !NonStatements.Contains(p.Name));

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

        private static string[] LevelsStatementsOf(LevelsKind kind, ValtuutusMySqlOptions options, bool batchResolved)
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
            var sql = LevelsStatementsOf(kind, fixture.Options, batchResolved)[levels];

            sql.Should().Contain(batchResolved ? MySqlQueries.BatchResolvedSnapToken : MySqlQueries.SnapTokenParameter);
            await ExecuteWithDummyParameters(sql);
        }

        [Fact]
        public void Batch_resolved_statements_read_the_session_variable_instead_of_the_parameter()
        {
            var queries = MySqlQueries.GetBatchResolved(fixture.Options);
            var texts = StatementProperties().Select(p => (string)p.GetValue(queries)!)
                .Append(queries.ResolveLatestSnapTokenForBatch)
                .Append(FusedExpressionSql.BuildStatement(EveryLeafKind(), false, queries))
                .Append(FusedExpressionSql.BuildStatement(EveryLeafKind(), true, queries))
                .Concat(Enum.GetValues<LevelsKind>()
                    .SelectMany(kind => LevelsStatementsOf(kind, fixture.Options, batchResolved: true))
                    .OfType<string>())
                .ToList();

            texts.Should().NotContain(t => t.Contains("@snap_token"));
            texts.Should().Contain(t => t.Contains(MySqlQueries.BatchResolvedSnapToken));
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

            var sql = FusedExpressionSql.BuildStatement(leaves, requireAll, queries);

            await ExecuteWithDummyParameters(sql);
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

        private static string StatementOf(MySqlQueries queries, string statement) =>
            (string)typeof(MySqlQueries).GetProperty(statement)!.GetValue(queries)!;

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

        // Lists of ids and names go in as JSON arrays; the snap token, bound or batch-resolved, is a
        // ULID; everything else is a key. Any value works: the statements only have to run.
        private static MySqlParameter DummyParameter(string name)
        {
            if (JsonListPattern().IsMatch(name))
                return new MySqlParameter(name, MySqlDbType.JSON) { Value = """["x","y"]""" };
            if (name is "@snap_token" or MySqlQueries.BatchResolvedSnapToken)
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
