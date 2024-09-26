using FluentAssertions;

namespace Valtuutus.Lang.SourceGen.IntegrationTests;

public class GeneratedSchemaConstsSpecs
{
    [Fact]
    public void ShouldRegisterEntitiesNames()
    {
        SchemaConstsGen.Organization.Name.Should().Be("organization");
        SchemaConstsGen.Project.Name.Should().Be("project");
        SchemaConstsGen.Team.Name.Should().Be("team");
        SchemaConstsGen.User.Name.Should().Be("user");
    }

    [Fact]
    public void ShouldRegisterEntitiesRelationsNames()
    {
        SchemaConstsGen.Organization.Relations.Admin.Should().Be("admin");
        SchemaConstsGen.Organization.Relations.Member.Should().Be("member");
        SchemaConstsGen.Project.Relations.Member.Should().Be("member");
        SchemaConstsGen.Project.Relations.Team.Should().Be("team");
        SchemaConstsGen.Project.Relations.Org.Should().Be("org");
        SchemaConstsGen.Team.Relations.Member.Should().Be("member");
        SchemaConstsGen.Team.Relations.Org.Should().Be("org");
        SchemaConstsGen.Team.Relations.Owner.Should().Be("owner");
    }

    [Fact]
    public void ShouldRegisterEntitiesAttributesNames()
    {
        SchemaConstsGen.Project.Attributes.Public.Should().Be("public");
        SchemaConstsGen.Project.Attributes.Status.Should().Be("status");
    }

    [Fact]
    public void ShouldRegisterEntitiesPermissionsNames()
    {
        SchemaConstsGen.Project.Permissions.Delete.Should().Be("delete");
        SchemaConstsGen.Project.Permissions.Edit.Should().Be("edit");
        SchemaConstsGen.Project.Permissions.View.Should().Be("view");
        SchemaConstsGen.Team.Permissions.Delete.Should().Be("delete");
        SchemaConstsGen.Team.Permissions.Edit.Should().Be("edit");
        SchemaConstsGen.Team.Permissions.Invite.Should().Be("invite");
        SchemaConstsGen.Team.Permissions.RemoveUser.Should().Be("remove_user");
    }
}