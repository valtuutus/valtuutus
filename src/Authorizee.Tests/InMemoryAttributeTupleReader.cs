using Authorizee.Core;
using Authorizee.Core.Data;

namespace Authorizee.Tests;

public class InMemoryAttributeTupleReader : IAttributeReader
{
    private readonly AttributeTuple[] _attributesTuples;

    public InMemoryAttributeTupleReader(AttributeTuple[] attributesTuples)
    {
        _attributesTuples = attributesTuples;
    }

    public Task<AttributeTuple?> GetAttribute(AttributeFilter filter)
    {
        var res = _attributesTuples.Where(x =>
            x.EntityType == filter.EntityType && x.Attribute == filter.Attribute);

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            res = res.Where(x => x.EntityId == filter.EntityId);
        
        return Task.FromResult(res.FirstOrDefault());
    }

    public Task<IList<AttributeTuple>> GetAttributes(AttributeFilter filter)
    {
        var res = _attributesTuples.Where(x =>
            x.EntityType == filter.EntityType && x.Attribute == filter.Attribute);

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            res = res.Where(x => x.EntityId == filter.EntityId);

        return Task.FromResult((IList<AttributeTuple>)res.ToArray());
    }
}