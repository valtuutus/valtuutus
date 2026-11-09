using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Valtuutus.Core;

namespace Valtuutus.Data.Db.Tests;

/// <summary>
/// Keys a decoder reads equal the column's text, and a key the previous row of the column had comes
/// back as the same string. Attribute values parsed through it equal what JsonNode.Parse gives, with
/// a node of their own for every row. No database is needed.
/// </summary>
public sealed class KeyDecoderSpecs
{
    private static DataTable Utf8Rows(params string[][] rows)
    {
        var table = new DataTable();
        for (var c = 0; c < rows[0].Length; c++)
            table.Columns.Add($"c{c}", typeof(byte[]));
        foreach (var row in rows)
            table.Rows.Add(row.Select(v => (object)Encoding.UTF8.GetBytes(v)).ToArray());
        return table;
    }

    private static DataTable StringRows(params string[][] rows)
    {
        var table = new DataTable();
        for (var c = 0; c < rows[0].Length; c++)
            table.Columns.Add($"c{c}", typeof(string));
        foreach (var row in rows)
            table.Rows.Add(row.Cast<object>().ToArray());
        return table;
    }

    private static List<string[]> ReadAll(DbDataReader reader, KeyDecoder decoder)
    {
        var rows = new List<string[]>();
        while (reader.Read())
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => decoder.Read(reader, i)).ToArray());
        return rows;
    }

    [Fact]
    public void A_key_the_previous_row_of_its_column_had_is_the_same_string()
    {
        using var decoder = KeyDecoder.Rent(keysAsUtf8: true);
        using var readerTable = Utf8Rows(["project", "p1"], ["project", "p2"], ["team", "p2"]);
        using var reader = readerTable.CreateDataReader();

        var rows = ReadAll(reader, decoder);

        rows.Should().BeEquivalentTo(new[] { new[] { "project", "p1" }, ["project", "p2"], ["team", "p2"] },
            o => o.WithStrictOrdering());
        rows[1][0].Should().BeSameAs(rows[0][0]);
        rows[2][1].Should().BeSameAs(rows[1][1]);
        rows[2][0].Should().NotBeSameAs(rows[1][0]);
    }

    [Fact]
    public void Keys_are_decoded_as_utf8()
    {
        using var decoder = KeyDecoder.Rent(keysAsUtf8: true);
        var longKey = new string('é', 300);
        using var readerTable = Utf8Rows(["ação", "", longKey], ["ação", "", longKey]);
        using var reader = readerTable.CreateDataReader();

        var rows = ReadAll(reader, decoder);

        rows.Should().AllSatisfy(r => r.Should().Equal("ação", "", longKey));
    }

    [Fact]
    public void A_returned_decoder_keeps_no_key_of_the_read_that_used_it()
    {
        string first;
        using (var decoder = KeyDecoder.Rent(keysAsUtf8: true))
        using (var firstTable = Utf8Rows(["project"]))
        using (var reader = firstTable.CreateDataReader())
            first = ReadAll(reader, decoder)[0][0];

        using var again = KeyDecoder.Rent(keysAsUtf8: true);
        using var nextTable = Utf8Rows(["project"]);
        using var next = nextTable.CreateDataReader();
        var second = ReadAll(next, again)[0][0];

        second.Should().Be("project").And.NotBeSameAs(first);
    }

    [Fact]
    public void Without_utf8_keys_the_columns_are_read_as_strings()
    {
        using var decoder = KeyDecoder.Rent(keysAsUtf8: false);
        using var readerTable = StringRows(["project", "p1", "owner", "user", "u1", ""]);
        using var reader = readerTable.CreateDataReader();
        reader.Read();

        decoder.ReadRelationTuple(reader).Should().Be(new RelationTuple("project", "p1", "owner", "user", "u1", ""));
    }

    public static TheoryData<string> Scalars() => new() { "true", "false", "1", "2.5", "\"active\"", "-7" };

    [Theory]
    [MemberData(nameof(Scalars))]
    public void Repeated_values_parse_like_JsonNode_Parse(string raw)
    {
        using var decoder = KeyDecoder.Rent(keysAsUtf8: true);
        var expected = (JsonValue)JsonNode.Parse(raw)!;
        var first = decoder.ParseValue(raw);
        var second = decoder.ParseValue(raw);

        second.Should().NotBeSameAs(first);
        foreach (var value in new[] { first, second })
        {
            value.GetType().Should().Be(expected.GetType());
            value.GetValueKind().Should().Be(expected.GetValueKind());
            value.ToJsonString().Should().Be(expected.ToJsonString());
            JsonNode.DeepEquals(value, expected).Should().BeTrue();
            value.Parent.Should().BeNull();
        }

        // Each node can take its own parent.
        _ = new JsonObject { ["a"] = first };
        _ = new JsonObject { ["a"] = second };
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Non_scalar_values_fail(string raw)
    {
        using var decoder = KeyDecoder.Rent(keysAsUtf8: true);
        decoder.Invoking(d => d.ParseValue(raw)).Should().Throw<InvalidOperationException>();
        decoder.Invoking(d => d.ParseValue(raw)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Typed_reads_match_JsonNode_Parse()
    {
        using var decoder = KeyDecoder.Rent(keysAsUtf8: false);
        decoder.ParseValue("1");
        decoder.ParseValue("1").GetValue<int>().Should().Be(1);
        decoder.ParseValue("true");
        decoder.ParseValue("true").GetValue<bool>().Should().BeTrue();
        decoder.ParseValue("2.5");
        decoder.ParseValue("2.5").GetValue<double>().Should().Be(2.5);
    }
}
