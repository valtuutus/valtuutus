using System.Text.Json.Nodes;
using AwesomeAssertions;

namespace Valtuutus.Data.MySql.Tests;

/// <summary>
/// Attribute values parsed through a decoder: a repeated raw value gives a node of its own each
/// time, equal to and of the same kind as the node JsonNode.Parse gives. No database is needed.
/// </summary>
public sealed class KeyDecoderSpecs
{
    public static TheoryData<string> Scalars() => new() { "true", "false", "1", "2.5", "\"active\"", "-7" };

    [Theory]
    [MemberData(nameof(Scalars))]
    public void Repeated_Values_Parse_Like_JsonNode_Parse(string raw)
    {
        var decoder = KeyDecoder.Rent();
        try
        {
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
        finally
        {
            KeyDecoder.Return(decoder);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Non_Scalar_Values_Fail(string raw)
    {
        var decoder = KeyDecoder.Rent();
        try
        {
            decoder.Invoking(d => d.ParseValue(raw)).Should().Throw<InvalidOperationException>();
            decoder.Invoking(d => d.ParseValue(raw)).Should().Throw<InvalidOperationException>();
        }
        finally
        {
            KeyDecoder.Return(decoder);
        }
    }

    [Fact]
    public void Typed_Reads_Match_JsonNode_Parse()
    {
        var decoder = KeyDecoder.Rent();
        try
        {
            decoder.ParseValue("1");
            decoder.ParseValue("1").GetValue<int>().Should().Be(1);
            decoder.ParseValue("true");
            decoder.ParseValue("true").GetValue<bool>().Should().BeTrue();
            decoder.ParseValue("2.5");
            decoder.ParseValue("2.5").GetValue<double>().Should().Be(2.5);
        }
        finally
        {
            KeyDecoder.Return(decoder);
        }
    }
}
