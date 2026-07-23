using FluentAssertions;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class FusedExpressionSqlBuilderSpecs
{
    [Fact]
    public void Joins_leaves_with_or_for_any_semantics()
    {
        var leaves = new[]
        {
            new FusedCheckLeaf(FusedLeafKind.Direct, Negate: false, ["owner"]),
            new FusedCheckLeaf(FusedLeafKind.TupleToUserSet, Negate: false, ["org"], TtuSubEntityType: "organization", TtuComputedRelation: "admin"),
        };
        var sql = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll: false,
            (leaf, i) => $"FRAG_{leaf.Kind}_{i}");
        sql.Should().Be("(FRAG_Direct_0 OR FRAG_TupleToUserSet_1)");
    }

    [Fact]
    public void Joins_leaves_with_and_for_all_semantics()
    {
        var leaves = new[]
        {
            new FusedCheckLeaf(FusedLeafKind.MultiDirect, Negate: false, ["owner", "member"], RequireAll: true),
            new FusedCheckLeaf(FusedLeafKind.Direct, Negate: true, ["banned"]),
        };
        var sql = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll: true,
            (leaf, i) => $"FRAG_{i}");
        sql.Should().Be("(FRAG_0 AND NOT FRAG_1)");
    }

    [Fact]
    public void Wraps_negated_leaf_regardless_of_position()
    {
        var leaves = new[]
        {
            new FusedCheckLeaf(FusedLeafKind.Direct, Negate: true, ["a"]),
            new FusedCheckLeaf(FusedLeafKind.Direct, Negate: false, ["b"]),
            new FusedCheckLeaf(FusedLeafKind.Direct, Negate: true, ["c"]),
        };
        var sql = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll: false,
            (leaf, i) => $"F{i}");
        sql.Should().Be("(NOT F0 OR F1 OR NOT F2)");
    }
}
