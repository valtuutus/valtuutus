using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    // Ids are limited to 64 characters, which take up to 256 UTF-8 bytes.
    public abstract class OverLongValueSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private static readonly string LongestAsciiId = new('a', 64);

        // 64 four-byte characters: the most bytes an id can hold.
        private static readonly string LongestWideId = string.Concat(Enumerable.Repeat("\U0001F600", 64));

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

        public static TheoryData<string, string> OverLongProbes => new()
        {
            { LongestAsciiId, LongestAsciiId + "x" },
            // One byte past the widest id, and two: the second is cut when it doesn't fit the
            // JSON_TABLE column.
            { LongestWideId, LongestWideId + "x" },
            { LongestWideId, LongestWideId + "xy" },
        };

        [Theory]
        [MemberData(nameof(OverLongProbes))]
        public async Task Over_long_ids_in_an_id_array_match_nothing(string storedId, string probeId)
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var token = await writer.Write(
            [
                new RelationTuple("team", "t1", "member", "user", storedId),
                new RelationTuple("team", "t2", "member", "user", "bob"),
            ],
            [
                new AttributeTuple("team", storedId, "active", JsonValue.Create(true)),
            ], default);
            var reader = _provider.GetRequiredService<IDataReaderProvider>();

            using var tuples = await reader.GetRelationsWithSubjectsIds(
                new EntityRelationFilter { EntityType = "team", Relation = "member", SnapToken = token },
                [probeId, "bob"], "user", "", null, default);
            var attributes = await reader.GetAttributesWithEntityIds(
                new AttributeFilter { EntityType = "team", Attribute = "active", SnapToken = token },
                [probeId, "t2"], default);

            tuples.Select(t => t.SubjectId).Should().Equal("bob");
            attributes.Should().BeEmpty();
        }

        [Fact]
        public async Task Over_long_subject_ids_are_not_allowed_by_check()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var token = await writer.Write([new RelationTuple("team", "t1", "member", "user", LongestAsciiId)], [], default);
            var engine = _provider.GetRequiredService<ICheckEngine>();

            var result = await engine.Check(
                new CheckRequest("team", "t1", "member", "user", LongestAsciiId + "x", snapToken: token), default);

            result.Allowed.Should().BeFalse();
        }

        [Fact]
        public async Task Writer_rejects_ids_longer_than_64_characters_before_writing_anything()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var tooLong = new string('a', 65);

            var relation = () => writer.Write(
                [new RelationTuple("team", "t1", "member", "user", "u1"), new RelationTuple("team", tooLong, "member", "user", "u1")],
                [], default);
            var subject = () => writer.Write([new RelationTuple("team", "t1", "member", "user", tooLong)], [], default);
            var attribute = () => writer.Write(
                [new RelationTuple("team", "t1", "member", "user", "u1")],
                [new AttributeTuple("team", tooLong, "active", JsonValue.Create(true))], default);

            await relation.Should().ThrowAsync<ArgumentException>().WithMessage("*entity id*64 characters*");
            await subject.Should().ThrowAsync<ArgumentException>().WithMessage("*subject id*64 characters*");
            await attribute.Should().ThrowAsync<ArgumentException>().WithMessage("*entity id*64 characters*");
            (await Count("SELECT COUNT(*) FROM transactions")).Should().Be(0);
            (await Count("SELECT COUNT(*) FROM relation_tuples")).Should().Be(0);
            (await Count("SELECT COUNT(*) FROM attributes")).Should().Be(0);
        }

        [Fact]
        public async Task Writer_rejects_names_whose_utf8_form_exceeds_their_column()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var wideRelation = string.Concat(Enumerable.Repeat("é", 33));

            var act = () => writer.Write([new RelationTuple("team", "t1", wideRelation, "user", "u1")], [], default);

            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*relation*64 UTF-8 bytes*");
            (await Count("SELECT COUNT(*) FROM transactions")).Should().Be(0);
        }

        [Fact]
        public async Task Non_ascii_ids_round_trip_through_write_check_and_lookup()
        {
            const string entityId = "tü-\U0001F389";
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var token = await writer.Write([new RelationTuple("team", entityId, "member", "user", LongestWideId)], [], default);

            var check = await _provider.GetRequiredService<ICheckEngine>()
                .Check(new CheckRequest("team", entityId, "member", "user", LongestWideId, snapToken: token), default);
            var page = await _provider.GetRequiredService<ILookupEntityEngine>()
                .LookupEntity(new LookupEntityRequest("team", "member", "user", LongestWideId) { SnapToken = token }, default);
            using var tuples = await _provider.GetRequiredService<IDataReaderProvider>().GetRelationsWithSubjectsIds(
                new EntityRelationFilter { EntityType = "team", Relation = "member", SnapToken = token },
                [LongestWideId, "bob"], "user", "", null, default);

            check.Allowed.Should().BeTrue();
            page.EntityIds.Should().Equal(entityId);
            tuples.Should().ContainSingle().Which.Should().Be(new RelationTuple("team", entityId, "member", "user", LongestWideId));
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
    public sealed class OverLongValueSpecs(MySqlFixture fixture) : OverLongValueSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class OverLongValueSpecs(MariaDbFixture fixture) : OverLongValueSpecsBase(fixture);
}
