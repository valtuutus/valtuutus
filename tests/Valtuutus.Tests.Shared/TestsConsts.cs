using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Tests.Shared;

public static class TestsConsts
{

    public const string DefaultSchema = @"
        entity user {}
        entity group {
            relation member @user;
        }
        entity workspace {
            relation owner @user;
            relation member @user;
            relation admin @user;
            attribute public bool;
            permission comment := member and public;
            permission delete := owner;
            permission view := public or owner or admin or member;
        }
        entity team {
            relation lead @user;
            relation member @user @group#member;
        }
        entity project {
            relation parent @workspace;
            relation team @team;
            relation member @user @team#member;
            relation lead @team#lead;
            attribute public bool;
            attribute status int;
            permission view := member or lead or (public and parent.view);
            permission edit := (parent.admin or team.member) and isActiveStatus(status);
        }
        entity task
        {
            relation parent @project;
            relation assignee @user @group#member;
            permission view := parent.view;
        }
        fn isActiveStatus(status int) => status == 1;
        ";

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