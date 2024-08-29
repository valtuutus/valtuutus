using FluentAssertions;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Lang.Tests;

public class LangSpecs
{
    [Fact]
    public void Full_Lang_v1_Specs_should_be_able_to_parse_entire_schema()
    {
        var parseResult = SchemaReader.Parse(@"
entity user {}

entity organization {
    relation member @user;
    relation admin @user;
    attribute has_credit bool;
    permission view := has_credit and member;
}

entity repository {
    relation owner @organization#admin;
    relation organization @organization;
    attribute is_public bool;
    permission view := organization.admin or (is_public and organization.view);
    permission edit := organization.view;
}
");
        parseResult.IsT0.Should().BeTrue();
    }

    [Fact]
    public void Comma_is_required_after_entity_member_declaration()
    {
        var parseResult = SchemaReader.Parse(@"entity user {
            relation test @group#member attribute nice bool
        }
");
        parseResult.AsT1.Should().NotBeEmpty();
        parseResult.AsT1.Should().BeEquivalentTo("Line 2:40 src:ValtuutusParser - extraneous input 'attribute' expecting {'@', ';'}",
            "Line 3:8 src:ValtuutusParser - missing ';' at '}'");
    }

    [Theory]
    [InlineData("entity user {}")]
    [InlineData("entity    user  {       }")]
    public void Empty_entity_different_whitespace_should_not_return_error(string schema)
    {
        // act
        var parseResult = SchemaReader.Parse(
            schema);


        // assert
        var expected = new SchemaBuilder().WithEntity("user").SchemaBuilder.Build();
        parseResult.IsT0.Should().BeTrue();
        parseResult.AsT0.Should().BeEquivalentTo(expected
            );
    }

    [Fact]
    public void Should_parse_fn()
    {
        var schema = SchemaReader.Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute balance int;

                permission withdraw := check_balance(context.amount, balance) and owner;
            }

            fn check_balance(amount int, balance int) =>
                (balance >= amount) and (amount <= 5000);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_balance"].Execute(new Dictionary<string, object?>()
            {
                ["balance"] = 5000,
                ["amount"] = 5000
            }).Should()
            .BeTrue();
    }
    
    [Fact]
    public void Should_parse_fn2()
    {
        var schema = SchemaReader.Parse(@"
            entity user {}

            entity account {

                relation owner @user;
                
                attribute balance int;

                permission withdraw := check_balance(context.amount, balance) and owner;
            }

            fn check_balance(amount int, balance int) =>
                (balance >= amount) and (amount <= 5000);
        ");

        schema.Should().NotBeNull();

        schema.AsT0.Functions["check_balance"].Execute(new Dictionary<string, object?>()
            {
                ["balance"] = null,
                ["amount"] = 500
            }).Should()
            .BeFalse();
    }
}