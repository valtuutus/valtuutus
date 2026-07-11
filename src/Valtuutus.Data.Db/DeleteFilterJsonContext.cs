using System.Text.Json.Serialization;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DeleteRelationsFilter[]))]
[JsonSerializable(typeof(DeleteAttributesFilter[]))]
public partial class DeleteFilterJsonContext : JsonSerializerContext
{
}
