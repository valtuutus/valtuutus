using System.Text.Json.Nodes;
using FluentAssertions;
using Valtuutus.Core;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class AttributeTupleDataReaderSpecs
{
    private static readonly string[] ExpectedColumns =
    [
        "EntityType", "EntityId", "Attribute", "Value", "TransactionId"
    ];

    private static AttributeTupleDataReader CreateSut(params AttributeTuple[] attributes)
        => new(attributes, "01ARZ3NDEKTSV4RRFFQ69G5FAV");

    [Fact]
    public void FieldCount_ShouldBe5()
    {
        using var sut = CreateSut();

        sut.FieldCount.Should().Be(5);
    }

    [Fact]
    public void GetName_ShouldReturnColumnNamesInFixedOrder()
    {
        using var sut = CreateSut();

        for (var i = 0; i < ExpectedColumns.Length; i++)
        {
            sut.GetName(i).Should().Be(ExpectedColumns[i]);
        }
    }

    [Theory]
    [InlineData("EntityType", 0)]
    [InlineData("entitytype", 0)]
    [InlineData("EntityId", 1)]
    [InlineData("Attribute", 2)]
    [InlineData("Value", 3)]
    [InlineData("TransactionId", 4)]
    [InlineData("TRANSACTIONID", 4)]
    public void GetOrdinal_ShouldResolveNamesCaseInsensitively(string name, int expectedOrdinal)
    {
        using var sut = CreateSut();

        sut.GetOrdinal(name).Should().Be(expectedOrdinal);
    }

    [Fact]
    public void GetOrdinal_ShouldThrowIndexOutOfRangeException_ForUnknownColumn()
    {
        using var sut = CreateSut();

        var act = () => sut.GetOrdinal("not_a_column");

        act.Should().Throw<IndexOutOfRangeException>();
    }

    [Fact]
    public void Read_ShouldExposeEachTupleValueByOrdinal_AndComputeValueLazily()
    {
        var attributes = new[]
        {
            new AttributeTuple("project", "1", "name", JsonValue.Create("foo")!),
            new AttributeTuple("project", "2", "public", JsonValue.Create(true)!)
        };
        using var sut = CreateSut(attributes);

        foreach (var expected in attributes)
        {
            sut.Read().Should().BeTrue();
            sut.GetValue(0).Should().Be(expected.EntityType);
            sut.GetValue(1).Should().Be(expected.EntityId);
            sut.GetValue(2).Should().Be(expected.Attribute);
            sut.GetValue(3).Should().Be(expected.Value.ToJsonString());
            sut.GetValue(4).Should().Be("01ARZ3NDEKTSV4RRFFQ69G5FAV");
        }

        sut.Read().Should().BeFalse();
    }

    [Fact]
    public void IsDBNull_ShouldAlwaysReturnFalse()
    {
        using var sut = CreateSut(new AttributeTuple("project", "1", "name", JsonValue.Create("foo")!));
        sut.Read();

        for (var i = 0; i < sut.FieldCount; i++)
        {
            sut.IsDBNull(i).Should().BeFalse();
        }
    }

    [Fact]
    public void GetFieldType_ShouldReturnStringForAllColumns()
    {
        using var sut = CreateSut();

        for (var i = 0; i < ExpectedColumns.Length; i++)
        {
            sut.GetFieldType(i).Should().Be(typeof(string));
        }
    }

    [Fact]
    public void GetInt32_ShouldThrowNotSupportedException()
    {
        using var sut = CreateSut(new AttributeTuple("project", "1", "name", JsonValue.Create("foo")!));
        sut.Read();

        var act = () => sut.GetInt32(0);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Close_ShouldSetIsClosed()
    {
        using var sut = CreateSut();

        sut.IsClosed.Should().BeFalse();
        sut.Close();
        sut.IsClosed.Should().BeTrue();
    }
}
