public record CheckRequest
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Relation { get; init; }
    public required string UserId { get; init; }
}