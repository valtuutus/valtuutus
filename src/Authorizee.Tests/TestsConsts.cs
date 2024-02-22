using Authorizee.Core.Schemas;

namespace Authorizee.Tests;

public static class TestsConsts
{
    public static readonly Schema Schemas = new SchemaBuilder()
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
            .WithPermission("comment", PermissionNode.Intersect("member", PermissionNode.Leaf("public")))
            .WithPermission("delete", PermissionNode.Leaf("owner"))
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
            PermissionNode.Leaf("member"), PermissionNode.Leaf("lead"),
            PermissionNode.Intersect("public", "parent.view"))
        )
        .WithEntity("task")
        .WithRelation("parent", rc => rc.WithEntityType("project"))
        .WithRelation("assignee", rc =>
            rc.WithEntityType("user")
                .WithEntityType("group", "member"))
        .WithPermission("view", PermissionNode.Leaf("parent.view")).SchemaBuilder.Build();


    public static class Users
    {
        public const string Identifier = "user";
        public const string Alice = "alice";
        public const string Bob = "bob";
        public const string Charlie = "charlie";
        public const string Dan = "dan";
        public const string Eve = "eve";
    }

    public static class Groups
    {
        public const string Identifier = "group";
        public const string Admins = "admins";
        public const string Developers = "developers";
        public const string Designers = "designers";
    }

    public static class Workspaces
    {
        public const string Identifier = "workspace";
        public const string PublicWorkspace = "1";
        public const string PrivateWorkspace = "2";
    }

    public static class Teams
    {
        public const string Identifier = "team";
        public const string OsBrabos = "osbrabos";
        public const string OsMaisBrabos = "osmaisbrabos";
    }
}