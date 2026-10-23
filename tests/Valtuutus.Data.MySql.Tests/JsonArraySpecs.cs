using System.Text.Json;
using AwesomeAssertions;

namespace Valtuutus.Data.MySql.Tests;

/// <summary>
/// The JSON array parameter a JSON_TABLE join reads: each value once, in first-seen order, escaped
/// by the default JSON encoder. No database is needed.
/// </summary>
public sealed class JsonArraySpecs
{
    [Fact]
    public void Writes_Each_Value_Once_In_First_Seen_Order()
    {
        MySqlSql.ToJsonArray(["b", "a", "b", "c", "a"]).Should().Be("""["b","a","c"]""");
    }

    [Fact]
    public void Compares_Values_Ordinally()
    {
        MySqlSql.ToJsonArray(["a", "A", "a "]).Should().Be("""["a","A","a "]""");
    }

    [Fact]
    public void Writes_An_Empty_Array_For_No_Values()
    {
        MySqlSql.ToJsonArray([]).Should().Be("[]");
    }

    [Fact]
    public void Escapes_With_The_Default_Encoder()
    {
        MySqlSql.ToJsonArray(["\"q\"", "<&>'", "\u00E9", "\U0001F600", "a\\b", "\n"])
            .Should().Be("[\"\\u0022q\\u0022\",\"\\u003C\\u0026\\u003E\\u0027\",\"\\u00E9\",\"\\uD83D\\uDE00\",\"a\\\\b\",\"\\n\"]");
    }

    [Fact]
    public void Writes_Large_Arrays_And_Keeps_Working_After_Them()
    {
        var many = Enumerable.Range(0, 20_000).Select(i => $"id-{i}").ToArray();
        var withDuplicates = many.Concat(many).ToArray();

        var json = MySqlSql.ToJsonArray(withDuplicates);

        JsonSerializer.Deserialize(json, JsonArraySpecsContext.Default.StringArray).Should().Equal(many);
        MySqlSql.ToJsonArray(["x", "x", "y"]).Should().Be("""["x","y"]""");
        MySqlSql.ToJsonArray(["z"]).Should().Be("""["z"]""");
    }

    [Fact]
    public void Concurrent_Writers_Do_Not_Share_State()
    {
        var results = Enumerable.Range(0, 64).AsParallel().Select(i =>
        {
            var values = Enumerable.Range(0, 50).Select(j => $"{i}-{j % 25}").ToArray();
            return (Expected: Enumerable.Range(0, 25).Select(j => $"{i}-{j}").ToArray(), Json: MySqlSql.ToJsonArray(values));
        }).ToList();

        foreach (var (expected, json) in results)
            JsonSerializer.Deserialize(json, JsonArraySpecsContext.Default.StringArray).Should().Equal(expected);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class JsonArraySpecsContext : System.Text.Json.Serialization.JsonSerializerContext;
