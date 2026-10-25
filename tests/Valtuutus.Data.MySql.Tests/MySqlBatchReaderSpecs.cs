using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using MySqlConnector;
using NSubstitute;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class MySqlBatchReaderSpecsBase(MySqlFixtureBase fixture)
    {
        private MySqlBatchOps NewBatchOps(string? connectionString = null) =>
            new(connectionString is null ? fixture.DbFactory : () => new MySqlConnection(connectionString),
                new ValtuutusDataOptions(), fixture.Options);

        private static void AddCommand(DbBatch batch, string sql, params (string Name, string Value)[] parameters)
        {
            var command = batch.CreateBatchCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.Add(new MySqlParameter(name, MySqlDbType.VarChar) { Value = value });
            batch.BatchCommands.Add(command);
        }

        [Fact]
        public async Task Reads_binary_columns_as_strings_and_passes_other_columns_through()
        {
            var ops = NewBatchOps();
            var batch = ops.CreateBatch();
            AddCommand(batch, "SELECT CAST('team' AS BINARY), 'text', 42, TRUE, JSON_OBJECT('a', 1)");

            await using var reader = await ops.ExecuteBatchAsync(batch, default);

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetFieldType(0).Should().Be(typeof(byte[]));
            reader.GetString(0).Should().Be("team");
            reader.GetFieldType(1).Should().Be(typeof(string));
            reader.GetString(1).Should().Be("text");
            reader.GetInt64(2).Should().Be(42);
            reader.GetBoolean(3).Should().BeTrue();
            reader.GetString(4).Replace(" ", "").Should().Be("""{"a":1}""");
            reader.GetFieldValue<byte[]>(0).Should().Equal("team"u8.ToArray());
            reader.IsDBNull(0).Should().BeFalse();
        }

        // EXISTS and boolean expressions come back as integers on both servers; the shared batch
        // readers read them with GetBoolean.
        [Fact]
        public async Task Reads_integer_boolean_results_with_GetBoolean()
        {
            var ops = NewBatchOps();
            var batch = ops.CreateBatch();
            AddCommand(batch, """
                SELECT EXISTS(SELECT 1), EXISTS(SELECT 1 FROM DUAL WHERE 1 = 0),
                       EXISTS(SELECT 1) OR EXISTS(SELECT 1 FROM DUAL WHERE 1 = 0),
                       EXISTS(SELECT 1) AND EXISTS(SELECT 1 FROM DUAL WHERE 1 = 0),
                       (SELECT COUNT(*) FROM DUAL) = 1
                """);

            await using var reader = await ops.ExecuteBatchAsync(batch, default);

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetFieldType(0).Should().NotBe(typeof(bool));
            Enumerable.Range(0, 5).Select(reader.GetBoolean).Should().Equal(true, false, true, false, true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Next_result_set_works_out_its_own_binary_columns(bool async)
        {
            var ops = NewBatchOps();
            var batch = ops.CreateBatch();
            AddCommand(batch, "SELECT CAST('a' AS BINARY), 'b'");
            AddCommand(batch, "SELECT 'c', CAST('d' AS BINARY)");

            await using var reader = await ops.ExecuteBatchAsync(batch, default);

            (await reader.ReadAsync()).Should().BeTrue();
            (reader.GetString(0), reader.GetString(1)).Should().Be(("a", "b"));
            (async ? await reader.NextResultAsync() : reader.NextResult()).Should().BeTrue();
            (await reader.ReadAsync()).Should().BeTrue();
            (reader.GetString(0), reader.GetString(1)).Should().Be(("c", "d"));
        }

        [Fact]
        public async Task Multi_byte_utf8_round_trips_through_binary_columns()
        {
            // Longer than the reader's initial buffer, so decoding grows it.
            var longValue = string.Concat(Enumerable.Repeat("文字", 100));
            string[] values = ["ação", "日本語", "🙂👍", longValue, "", "plain"];
            var ops = NewBatchOps();
            var batch = ops.CreateBatch();
            foreach (var value in values)
                AddCommand(batch, "SELECT CAST(@v AS BINARY)", ("@v", value));

            await using var reader = await ops.ExecuteBatchAsync(batch, default);

            var read = new List<string>();
            do
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetFieldType(0).Should().Be(typeof(byte[]));
                read.Add(reader.GetString(0));
            } while (await reader.NextResultAsync());
            read.Should().Equal(values);
        }

        [Fact]
        public async Task Disposing_the_reader_returns_its_connection_to_the_pool()
        {
            var connectionString = new MySqlConnectionStringBuilder(fixture.ConnectionString)
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = 5,
                ApplicationName = $"batch-reader-pool-{Guid.NewGuid():N}",
            }.ConnectionString;
            var ops = NewBatchOps(connectionString);

            for (var i = 0; i < 5; i++)
            {
                var batch = ops.CreateBatch();
                AddCommand(batch, "SELECT 1");
                AddCommand(batch, "SELECT 2");
                await using var reader = await ops.ExecuteBatchAsync(batch, default);
                (await reader.ReadAsync()).Should().BeTrue();
            }
        }

        [Fact]
        public async Task Closing_and_disposing_again_releases_the_connection_once()
        {
            var connectionString = new MySqlConnectionStringBuilder(fixture.ConnectionString)
            {
                MaximumPoolSize = 1,
                ConnectionTimeout = 5,
                ApplicationName = $"batch-reader-redispose-{Guid.NewGuid():N}",
            }.ConnectionString;
            var ops = NewBatchOps(connectionString);

            for (var i = 0; i < 3; i++)
            {
                var batch = ops.CreateBatch();
                AddCommand(batch, "SELECT CAST('a' AS BINARY)");
                AddCommand(batch, "SELECT 2");
                var reader = await ops.ExecuteBatchAsync(batch, default);
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("a");

                reader.Close();
                reader.Close();
                reader.Dispose();
                reader.Dispose();
                await reader.DisposeAsync();
            }
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Each test closes or disposes the reader itself, as the behavior under test.")]
    public sealed class MySqlBatchReaderDisposalSpecs
    {
        // An inner reader whose close and dispose fail, as draining the rest of a batch does when
        // the network drops.
        public abstract class DrainFailingReader : DbDataReader
        {
            public override void Close() => throw new IOException("drain failed");

            protected override void Dispose(bool disposing) => throw new IOException("drain failed");

            public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("drain failed"));
        }

        private sealed class CountingConnection : DbConnection
        {
            public int Closes { get; private set; }
            public int Disposals { get; private set; }

            [AllowNull]
            public override string ConnectionString { get; set; } = "";
            public override string Database => "";
            public override string DataSource => "";
            public override string ServerVersion => "";
            public override ConnectionState State => ConnectionState.Closed;
            public override void ChangeDatabase(string databaseName) { }
            public override void Close() => Closes++;
            public override void Open() { }
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
                throw new NotSupportedException();
            protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    Disposals++;
                base.Dispose(disposing);
            }
        }

        [Fact]
        public void Close_closes_the_connection_when_the_inner_reader_fails()
        {
            using var connection = new CountingConnection();
            var reader = new MySqlBatchReader(Substitute.ForPartsOf<DrainFailingReader>(), connection);

            var act = () => reader.Close();

            act.Should().Throw<IOException>();
            connection.Closes.Should().Be(1);
            act.Should().NotThrow();
            connection.Closes.Should().Be(1);
        }

        [Fact]
        public void Dispose_disposes_the_connection_when_the_inner_reader_fails()
        {
            using var connection = new CountingConnection();
            var reader = new MySqlBatchReader(Substitute.ForPartsOf<DrainFailingReader>(), connection);

            var act = () => reader.Dispose();

            act.Should().Throw<IOException>();
            connection.Disposals.Should().Be(1);
            act.Should().NotThrow();
            connection.Disposals.Should().Be(1);
        }

        [Fact]
        public async Task DisposeAsync_disposes_the_connection_when_the_inner_reader_fails()
        {
            using var connection = new CountingConnection();
            var reader = new MySqlBatchReader(Substitute.ForPartsOf<DrainFailingReader>(), connection);

            var act = async () => await reader.DisposeAsync();

            await act.Should().ThrowAsync<IOException>();
            connection.Disposals.Should().Be(1);
            await act.Should().NotThrowAsync();
            reader.Dispose();
            connection.Disposals.Should().Be(1);
        }

        [Fact]
        public void Dispose_after_close_disposes_the_connection_once()
        {
            using var connection = new CountingConnection();
            var reader = new MySqlBatchReader(Substitute.For<DbDataReader>(), connection);

            reader.Close();
            reader.Dispose();
            reader.Dispose();

            connection.Closes.Should().Be(1);
            connection.Disposals.Should().Be(1);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class MySqlBatchReaderSpecs(MySqlFixture fixture) : MySqlBatchReaderSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class MySqlBatchReaderSpecs(MariaDbFixture fixture) : MySqlBatchReaderSpecsBase(fixture);
}
