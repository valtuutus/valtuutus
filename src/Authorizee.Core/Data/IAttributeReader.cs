namespace Authorizee.Core.Data;

public interface IAttributeReader
{
    Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter);
    Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter);
    Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds);
}