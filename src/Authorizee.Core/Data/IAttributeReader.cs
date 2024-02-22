namespace Authorizee.Core.Data;

public interface IAttributeReader
{
    Task<AttributeTuple?> GetAttribute(AttributeFilter filter);
    Task<IList<AttributeTuple>> GetAttributes(AttributeFilter filter);
}