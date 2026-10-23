using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class AttributeRewriteSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private ServiceProvider _provider = default!;

        public Task InitializeAsync()
        {
            var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options);
            _provider = services.BuildServiceProvider();
            return fixture.ResetDatabaseAsync();
        }

        public async Task DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        [Fact]
        public async Task Rewriting_an_attribute_tombstones_the_old_row_and_keeps_it_readable_at_the_old_token()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var first = await writer.Write([], [new AttributeTuple("team", "t1", "active", JsonValue.Create(true))], default);
            var second = await writer.Write([], [new AttributeTuple("team", "t1", "active", JsonValue.Create(false))], default);

            var rows = await Rows();
            rows.Should().BeEquivalentTo(new[]
            {
                (Value: "true", CreatedTxId: first.Value, DeletedTxId: (string?)second.Value),
                (Value: "false", CreatedTxId: second.Value, DeletedTxId: (string?)null),
            });

            var reader = _provider.GetRequiredService<IDataReaderProvider>();
            (await ReadActive(reader, first)).Should().BeTrue();
            (await ReadActive(reader, second)).Should().BeFalse();
        }

        [Fact]
        public async Task Writing_the_same_attribute_twice_in_one_write_is_rejected_before_anything_is_written()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();

            var act = () => writer.Write([new RelationTuple("team", "t1", "member", "user", "u1")],
            [
                new AttributeTuple("team", "t1", "active", JsonValue.Create(true)),
                new AttributeTuple("team", "t1", "active", JsonValue.Create(false)),
            ], default);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*active*team:t1*");
            (await Count("SELECT COUNT(*) FROM transactions")).Should().Be(0);
            (await Count("SELECT COUNT(*) FROM relation_tuples")).Should().Be(0);
        }

        [Fact]
        public async Task Writes_larger_than_one_statement_are_written_whole()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var relations = Enumerable.Range(0, 2001)
                .Select(i => new RelationTuple("team", $"t{i}", "member", "user", "u1"))
                .ToArray();
            var attributes = Enumerable.Range(0, 1001)
                .Select(i => new AttributeTuple("team", $"t{i}", "active", JsonValue.Create(i % 2 == 0)))
                .ToArray();

            var token = await writer.Write(relations, attributes, default);

            (await Count($"SELECT COUNT(*) FROM relation_tuples WHERE created_tx_id = '{token.Value}'")).Should().Be(2001);
            (await Count($"SELECT COUNT(*) FROM attributes WHERE created_tx_id = '{token.Value}'")).Should().Be(1001);
        }

        [Fact]
        public async Task Attribute_values_larger_than_one_packet_in_total_are_written_whole()
        {
            // Together the values exceed the smallest default max_allowed_packet of the supported
            // servers, so they only fit when split across statements.
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var big = new string('x', 3 * 1024 * 1024);
            var attributes = Enumerable.Range(0, 6)
                .Select(i => new AttributeTuple("team", $"t{i}", "bio", JsonValue.Create(big)))
                .ToArray();

            var token = await writer.Write([], attributes, default);

            (await Count($"SELECT COUNT(*) FROM attributes WHERE created_tx_id = '{token.Value}' AND CHAR_LENGTH(value) = {big.Length + 2}"))
                .Should().Be(6);
        }

        [Fact]
        public async Task Attribute_values_that_escaping_doubles_are_written_whole()
        {
            // Each value's text is backslashes, which escaping doubles, and just short of the value
            // size at which an attribute INSERT closes. Two of them in one statement would exceed the
            // smallest default max_allowed_packet of the supported servers once escaped.
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var backslashes = new string('\\', 2 * 1024 * 1024 - 2);
            var attributes = Enumerable.Range(0, 3)
                .Select(i => new AttributeTuple("team", $"t{i}", "bio", JsonValue.Create(backslashes)))
                .ToArray();

            var token = await writer.Write([], attributes, default);

            (await Count($"SELECT COUNT(*) FROM attributes WHERE created_tx_id = '{token.Value}' AND CHAR_LENGTH(value) = {2 * backslashes.Length + 2}"))
                .Should().Be(3);
        }

        private static async Task<bool> ReadActive(IDataReaderProvider reader, SnapToken token)
        {
            var attributes = await reader.GetAttributes(new EntityAttributeFilter
            {
                EntityType = "team", EntityId = "t1", Attribute = "active", SnapToken = token
            }, default);
            return attributes.Single().Value.GetValue<bool>();
        }

        private async Task<List<(string Value, string CreatedTxId, string? DeletedTxId)>> Rows()
        {
            await using var db = (MySqlConnection)fixture.DbFactory();
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText =
                "SELECT value, created_tx_id, deleted_tx_id FROM attributes WHERE entity_type = 'team' AND entity_id = 't1' AND attribute = 'active'";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string, string, string?)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            return rows;
        }

        private async Task<long> Count(string sql)
        {
            await using var db = (MySqlConnection)fixture.DbFactory();
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class AttributeRewriteSpecs(MySqlFixture fixture) : AttributeRewriteSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class AttributeRewriteSpecs(MariaDbFixture fixture) : AttributeRewriteSpecsBase(fixture);
}
