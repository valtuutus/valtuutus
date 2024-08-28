using FluentAssertions;
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
        parseResult.AsT1.Should().BeEmpty();
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
        var parseResult = SchemaReader.Parse(
            schema);

        parseResult.IsT0.Should().BeTrue();
        parseResult.AsT0.Should().BeEquivalentTo(
            new SchemaBuilder().WithEntity("user").Build());
    }
}