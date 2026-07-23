using System.Collections.Immutable;
using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class FusedExpressionOpSpecs
{
    private static CheckRequestContext Ctx() => new()
    {
        SubjectType = "user", SubjectId = "u1",
        SnapToken = new SnapToken("00000000000000000000000001"),
        Context = new Dictionary<string, object>()
    };

    private static readonly ImmutableArray<FusedCheckLeaf> Leaves =
    [
        new FusedCheckLeaf(FusedLeafKind.TupleToUserSet, Negate: false, ["org"], TtuSubEntityType: "organization", TtuComputedRelation: "admin"),
        new FusedCheckLeaf(FusedLeafKind.Direct, Negate: false, ["owner"]),
    ];

    private static IDataReaderProvider RelationalReader(bool result)
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalCheckOps>();
        ((IRelationalCheckOps)reader).HasFusedExpression(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<FusedCheckLeaf>>(), Arg.Any<bool>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SnapToken>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return reader;
    }

    [Fact]
    public async Task Execute_returns_the_relational_reader_result_unchanged()
    {
        var op = new FusedExpressionOp(Leaves, requireAll: false);
        (await op.Execute(RelationalReader(true), Ctx(), "team", "1", default)).Should().BeTrue();
        (await op.Execute(RelationalReader(false), Ctx(), "team", "1", default)).Should().BeFalse();
    }

    [Fact]
    public async Task Execute_passes_constructor_args_and_context_through()
    {
        var op = new FusedExpressionOp(Leaves, requireAll: false);
        var reader = RelationalReader(true);
        var ctx = Ctx();

        await op.Execute(reader, ctx, "team", "1", default);

        ((IRelationalCheckOps)reader).Received(1).HasFusedExpression(
            "team", "1", Leaves, false, "user", "u1", ctx.SnapToken, default);
    }

    [Fact]
    public async Task Non_relational_reader_fails_with_a_pointed_message()
    {
        var op = new FusedExpressionOp(Leaves, requireAll: false);
        var reader = Substitute.For<IDataReaderProvider>(); // no IRelationalCheckOps
        var act = () => op.Execute(reader, Ctx(), "team", "1", default).AsTask();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*IRelationalCheckOps*");
    }

    [Fact]
    public void Describe_renders_every_leaf_kind_and_negation()
    {
        var leaves = ImmutableArray.Create(
            new FusedCheckLeaf(FusedLeafKind.MultiDirect, Negate: false, ["owner", "member"], RequireAll: true),
            new FusedCheckLeaf(FusedLeafKind.Direct, Negate: true, ["banned"]));
        var op = new FusedExpressionOp(leaves, requireAll: true);
        op.Describe().Should().Be("FusedExpression([MultiDirect([owner, member], all), not Direct(banned)], all)");
    }
}
