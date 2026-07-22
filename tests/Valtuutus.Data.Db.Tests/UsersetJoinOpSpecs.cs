using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class UsersetJoinOpSpecs
{
    private static CheckRequestContext Ctx() => new()
    {
        SubjectType = "user", SubjectId = "u1",
        SnapToken = new SnapToken("00000000000000000000000001"),
        Context = new Dictionary<string, object>()
    };

    private static IDataReaderProvider RelationalReader(bool result)
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalCheckOps>();
        ((IRelationalCheckOps)reader).HasUsersetJoinRelation(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SnapToken>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return reader;
    }

    [Fact]
    public async Task Execute_returns_the_relational_reader_result_unchanged()
    {
        var op = new UsersetJoinOp("owner", "group", "member");
        (await op.Execute(RelationalReader(true), Ctx(), "folder", "1", default)).Should().BeTrue();
        (await op.Execute(RelationalReader(false), Ctx(), "folder", "1", default)).Should().BeFalse();
    }

    [Fact]
    public async Task Execute_passes_constructor_args_and_context_through()
    {
        var op = new UsersetJoinOp("owner", "group", "member");
        var reader = RelationalReader(true);
        var ctx = Ctx();

        await op.Execute(reader, ctx, "folder", "1", default);

        ((IRelationalCheckOps)reader).Received(1).HasUsersetJoinRelation(
            "folder", "1", "owner", "group", "member", "user", "u1", ctx.SnapToken, default);
    }

    [Fact]
    public async Task Non_relational_reader_fails_with_a_pointed_message()
    {
        var op = new UsersetJoinOp("owner", "group", "member");
        var reader = Substitute.For<IDataReaderProvider>(); // no IRelationalCheckOps
        var act = () => op.Execute(reader, Ctx(), "folder", "1", default).AsTask();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*IRelationalCheckOps*");
    }
}
