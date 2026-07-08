using FluentAssertions;
using Valtuutus.Core;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class RelationTupleDataReaderSpecs
{
    private static readonly string[] ExpectedColumns =
    [
        "EntityType", "EntityId", "SubjectType", "SubjectId", "Relation", "SubjectRelation", "TransactionId"
    ];

    private static RelationTupleDataReader CreateSut(params RelationTuple[] relations)
        => new(relations, "01ARZ3NDEKTSV4RRFFQ69G5FAV");

    [Fact]
    public void FieldCount_ShouldBe7()
    {
        using var sut = CreateSut();

        sut.FieldCount.Should().Be(7);
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
    [InlineData("SubjectType", 2)]
    [InlineData("SubjectId", 3)]
    [InlineData("Relation", 4)]
    [InlineData("SubjectRelation", 5)]
    [InlineData("TransactionId", 6)]
    [InlineData("TRANSACTIONID", 6)]
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
    public void Read_ShouldExposeEachTupleValueByOrdinal()
    {
        var relations = new[]
        {
            new RelationTuple("project", "1", "member", "user", "1"),
            new RelationTuple("project", "2", "owner", "user", "2", "admin")
        };
        using var sut = CreateSut(relations);

        foreach (var expected in relations)
        {
            sut.Read().Should().BeTrue();
            sut.GetValue(0).Should().Be(expected.EntityType);
            sut.GetValue(1).Should().Be(expected.EntityId);
            sut.GetValue(2).Should().Be(expected.SubjectType);
            sut.GetValue(3).Should().Be(expected.SubjectId);
            sut.GetValue(4).Should().Be(expected.Relation);
            sut.GetValue(5).Should().Be(expected.SubjectRelation);
            sut.GetValue(6).Should().Be("01ARZ3NDEKTSV4RRFFQ69G5FAV");
        }

        sut.Read().Should().BeFalse();
    }

    [Fact]
    public void IsDBNull_ShouldAlwaysReturnFalse()
    {
        using var sut = CreateSut(new RelationTuple("project", "1", "member", "user", "1"));
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
    public void GetBoolean_ShouldThrowNotSupportedException()
    {
        using var sut = CreateSut(new RelationTuple("project", "1", "member", "user", "1"));
        sut.Read();

        var act = () => sut.GetBoolean(0);

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
