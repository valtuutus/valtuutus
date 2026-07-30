//HintName: SchemaFunctions.g.cs
#nullable enable
using System;
using System.Collections.Generic;
using Valtuutus.Core.Lang;

namespace Valtuutus.Lang;

/// <summary>
/// Auto-generated class containing schema DSL functions compiled to native C# at build time.
/// </summary>
public static class SchemaFunctionsGen
{
	public static bool IsActiveStatus(ReadOnlySpan<LiteralValueUnion> args)
	{
		var @status = args[0].IntValue;
		return (@status) == (1);
	}

	public static readonly IReadOnlyDictionary<string, FunctionExecutor> All = new Dictionary<string, FunctionExecutor>
	{
		["isActiveStatus"] = IsActiveStatus,
	};
}
