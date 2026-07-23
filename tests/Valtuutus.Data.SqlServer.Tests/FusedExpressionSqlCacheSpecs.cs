using FluentAssertions;
using Valtuutus.Data.Db;
using Xunit;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// Proves FusedExpressionSql.BuildCommandSql's cache actually hits for a stable leaves reference —
/// not just that it returns equal-content strings, but the literal same string object — which is
/// what makes repeated Check() calls against the same compiled plan skip re-interpolating the
/// full command text on every call. No database is involved: BuildCommandSql is a pure function of
/// its inputs.
/// </summary>
public class FusedExpressionSqlCacheSpecs
{
    [Fact]
    public void BuildCommandSql_SameLeavesInstance_ReturnsReferenceIdenticalStringOnSecondCall()
    {
        IReadOnlyList<FusedCheckLeaf> leaves =
        [
            new FusedCheckLeaf(FusedLeafKind.Direct, false, ["owner"]),
            new FusedCheckLeaf(FusedLeafKind.TupleToUserSet, false, ["org"], TtuSubEntityType: "organization", TtuComputedRelation: "admin"),
        ];

        var first = FusedExpressionSql.BuildCommandSql(leaves, requireAll: false, "relations", "attributes");
        var second = FusedExpressionSql.BuildCommandSql(leaves, requireAll: false, "relations", "attributes");

        ReferenceEquals(first, second).Should().BeTrue(
            "the second call for the same leaves reference must hit the cache instead of rebuilding the SQL text");
    }

    [Fact]
    public void BuildCommandSql_DifferentLeavesInstance_ReturnsIndependentEntry()
    {
        IReadOnlyList<FusedCheckLeaf> leavesA = [new FusedCheckLeaf(FusedLeafKind.Direct, false, ["owner"])];
        IReadOnlyList<FusedCheckLeaf> leavesB = [new FusedCheckLeaf(FusedLeafKind.Direct, false, ["owner"])];

        var exprA = FusedExpressionSql.BuildCommandSql(leavesA, requireAll: false, "relations", "attributes");
        var exprB = FusedExpressionSql.BuildCommandSql(leavesB, requireAll: false, "relations", "attributes");

        // Equal-content leaves that are NOT the same list reference get their own cache entry
        // (identity-keyed, not value-keyed) — the text still comes out equal, just not the same
        // object, confirming the key really is leaves' reference identity and not some incidental
        // value-based memoization.
        exprA.Should().Be(exprB);
        ReferenceEquals(exprA, exprB).Should().BeFalse();
    }
}
