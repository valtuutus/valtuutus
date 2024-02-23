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

    public Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct)
    {
        var res = _attributesTuples.Where(x =>
            x.EntityType == filter.EntityType && x.Attribute == filter.Attribute);

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            res = res.Where(x => x.EntityId == filter.EntityId);
        
        return Task.FromResult(res.FirstOrDefault());
    }

    public Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct)
    {
        var res = _attributesTuples.Where(x =>
            x.EntityType == filter.EntityType && x.Attribute == filter.Attribute);

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
            res = res.Where(x => x.EntityId == filter.EntityId);

        return Task.FromResult(res.ToList());
    }

    public Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct)
    {
        return Task.FromResult(_attributesTuples
            .Where(x => x.EntityType == filter.EntityType
                        && x.Attribute == filter.Attribute
                        && entitiesIds.Contains(x.EntityId))
            .ToList());
    }
}