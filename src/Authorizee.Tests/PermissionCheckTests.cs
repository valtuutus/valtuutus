using Authorizee.Core.Schemas;
using NSubstitute;

namespace Authorizee.Tests;

public class PermissionCheckTests
{
    public PermissionCheckTests()
    {
        var schemaBuilder = new SchemaBuilder();
        schemaBuilder
            .WithEntity("user")
            .WithEntity("group")
                .WithRelation("member", rc =>
                    rc.WithEntityType("user")
                )
            .WithEntity("workspace")
                .WithRelation("owner", rc =>
                    rc.WithEntityType("user"))
                .WithRelation("admin", rc =>
                    rc.WithEntityType("user"))
                .WithRelation("member", rc =>
                    rc.WithEntityType("user"))
                .WithAttribute("public", typeof(bool))
                .WithPermission("view", PermissionNode.Union(
                    PermissionNode.Leaf("public"), PermissionNode.Leaf("owner"), 
                    PermissionNode.Leaf("member"), PermissionNode.Leaf("admin"))
                )
            .WithEntity("team")
                .WithRelation("lead", rc => rc.WithEntityType("user"))
                .WithRelation("member", rc =>
                    rc.WithEntityType("user")
                        .WithEntityType("group", "member"))
            .WithEntity("project")
                .WithRelation("parent", rc => rc.WithEntityType("workspace"))
                .WithRelation("team", rc => rc.WithEntityType("team"))
                .WithRelation("member", rc =>
                    rc.WithEntityType("user")
                        .WithEntityType("team", "member"))
                .WithRelation("lead", rc => rc.WithEntityType("team", "lead"))
                .WithAttribute("public", typeof(bool))
                .WithPermission("view", PermissionNode.Union(
                    PermissionNode.Leaf("member"), PermissionNode.Leaf("lead"), PermissionNode.Intersect("public", "parent.view"))
                )
            .WithEntity("task")
                .WithRelation("parent", rc => rc.WithEntityType("project"))
                .WithRelation("assignee", rc =>
                    rc.WithEntityType("user")
                        .WithEntityType("group", "member"))
                .WithPermission("view", PermissionNode.Leaf("parent.view"));
    }
    
    [Fact]
    public void Test1()
    {
    }
}