using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Lang.SchemaReaders;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Lang.Tests;

public class LangSpecs
{
    [Fact]
    public void Full_Lang_v1_Specs_should_be_able_to_parse_entire_schema()
    {
        var parseResult = new SchemaReader().Parse(@"
entity user {}

entity organization {
    relation member @user;
    relation admin @user;
    attribute has_credit bool;
    permission view := has_credit and member;
}

entity repository {
    relation parent @repository;
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
        var parseResult = new SchemaReader().Parse(@"entity user {
            relation test @group#member attribute nice bool
        }
");
        parseResult.AsT1.Should().NotBeEmpty();
        parseResult.AsT1.Should().BeEquivalentTo([
            new { Message = "src:ValtuutusParser - extraneous input 'attribute' expecting {'@', ';'}" },
            new { Message = "src:ValtuutusParser - missing ';' at '}'" }
        ]);
    }

    [Theory]
    [InlineData("entity user {}")]
    [InlineData("entity    user  {       }")]
    public void Empty_entity_different_whitespace_should_not_return_error(string schema)
    {
        // act
        var parseResult = new SchemaReader().Parse(
            schema);


        // assert
        var expected = new SchemaBuilder().WithEntity("user").SchemaBuilder.Build();
        parseResult.IsT0.Should().BeTrue();
        parseResult.AsT0.Should().BeEquivalentTo(expected
        );
    }

    // startups:id:     relation       usuarioId
    
    [Fact]
    public void Should_parse_relation_with_multiple_referenced_entities_relations()
    {
        var schema = new SchemaReader().Parse(@"
            entity user {}

            entity organization {
                relation admin @user;
                relation member @user;
            }

            entity team {

                relation owner @user;
                relation member @user;
                relation org @organization;

                permission edit := org.admin or owner;
                permission delete := org.admin or owner;
                permission invite := org.admin and (owner or member);
                permission remove_user :=  owner;
            }

            entity project {

                relation members @team#member @team#owner @organization#member;
                relation team @team;
                relation org @organization;

                permission view := org.admin or team.member;
                permission edit := org.admin or team.member;
                permission delete := team.member;
            }
        ");

        schema.AsT0.Should().NotBeNull();

        schema.AsT0.Entities["project"].Relations["members"]
            .Entities
            .Should()
            .BeEquivalentTo([
                new RelationEntity() { Type = "team", Relation = "member", },
                new RelationEntity() { Type = "team", Relation = "owner", },
                new RelationEntity() { Type = "organization", Relation = "member", },
            ]);
    }
}