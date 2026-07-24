using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class HasAnyOfAttributesOpSpecs
{
    private static CheckRequestContext Ctx() => new()
    {
        SubjectType = "user", SubjectId = "u1",
        SnapToken = new SnapToken("00000000000000000000000001"),
        Context = new Dictionary<string, object>()
    };

    private static IDataReaderProvider RelationalReader(HashSet<string> matched)
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalCheckOps>();
        ((IRelationalCheckOps)reader).HasAnyOfAttributes(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>(),
                Arg.Any<SnapToken>(), Arg.Any<CancellationToken>())
            .Returns(matched);
        return reader;
    }

    [Fact]
    public async Task Any_mode_is_true_when_the_set_is_non_empty()
    {
        var op = new HasAnyOfAttributesOp(["a0", "a1"], requireAll: false);
        (await op.Execute(RelationalReader(["a1"]), Ctx(), "doc", "1", default)).Should().BeTrue();
        (await op.Execute(RelationalReader([]), Ctx(), "doc", "1", default)).Should().BeFalse();
    }

    [Fact]
    public async Task All_mode_requires_every_attribute()
    {
        var op = new HasAnyOfAttributesOp(["a0", "a1"], requireAll: true);
        (await op.Execute(RelationalReader(["a0", "a1"]), Ctx(), "doc", "1", default)).Should().BeTrue();
        (await op.Execute(RelationalReader(["a0"]), Ctx(), "doc", "1", default)).Should().BeFalse();
    }

    [Fact]
    public async Task Non_relational_reader_fails_with_a_pointed_message()
    {
        var op = new HasAnyOfAttributesOp(["a0", "a1"], requireAll: false);
        var reader = Substitute.For<IDataReaderProvider>(); // no IRelationalCheckOps
        var act = () => op.Execute(reader, Ctx(), "doc", "1", default).AsTask();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*IRelationalCheckOps*");
    }
}
