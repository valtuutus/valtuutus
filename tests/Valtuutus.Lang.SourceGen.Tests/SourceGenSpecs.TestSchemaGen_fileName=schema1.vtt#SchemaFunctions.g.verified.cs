//HintName: SchemaFunctions.g.cs
#nullable enable
using System;
using System.Collections.Generic;

namespace Valtuutus.Lang;

/// <summary>
/// Auto-generated class containing schema DSL functions compiled to native C# at build time.
/// </summary>
public static class SchemaFunctionsGen
{
	public static bool IsActiveStatus(IDictionary<string, object?> args)
	{
		var @status = (int?)args["status"];
		return (@status) == (1);
	}

	public static readonly IReadOnlyDictionary<string, Func<IDictionary<string, object?>, bool>> All = new Dictionary<string, Func<IDictionary<string, object?>, bool>>
	{
		["isActiveStatus"] = IsActiveStatus,
	};
}
