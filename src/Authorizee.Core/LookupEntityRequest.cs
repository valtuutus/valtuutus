namespace Authorizee.Core;


public record LookupEntityRequest
{
    //public required string TenantId { get; init; }
    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public string? SubjectRelation { get; init; }
}