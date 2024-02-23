namespace Authorizee.Core.Data;

public interface IAttributeReader
{
    Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct);
    Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct);
    Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct);
}