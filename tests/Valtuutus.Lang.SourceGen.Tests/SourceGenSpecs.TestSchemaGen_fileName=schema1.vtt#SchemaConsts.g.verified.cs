//HintName: SchemaConsts.g.cs
namespace Valtuutus.Lang;

/// <summary>
/// Auto-generated class to access all schema members as consts.
/// </summary>
public static class SchemaConstsGen
{
	public static class User
	{
		public const string Name = "user";
	}
	public static class Organization
	{
		public const string Name = "organization";
		public static class Relations
		{
			public const string Admin = "admin";
			public const string Member = "member";
		}
	}
	public static class Team
	{
		public const string Name = "team";
		public static class Relations
		{
			public const string Owner = "owner";
			public const string Member = "member";
			public const string Org = "org";
		}
		public static class Permissions
		{
			public const string Edit = "edit";
			public const string Delete = "delete";
			public const string Invite = "invite";
			public const string RemoveUser = "remove_user";
		}
	}
	public static class Project
	{
		public const string Name = "project";
		public static class Attributes
		{
			public const string Public = "public";
			public const string Status = "status";
		}
		public static class Relations
		{
			public const string Org = "org";
			public const string Team = "team";
			public const string Member = "member";
		}
		public static class Permissions
		{
			public const string View = "view";
			public const string Edit = "edit";
			public const string Delete = "delete";
		}
	}
}
