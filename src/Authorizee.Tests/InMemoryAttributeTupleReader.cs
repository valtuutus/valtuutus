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
        return Task.FromResult(
            _attributesTuples.FirstOrDefault(x =>
                x.EntityType == filter.EntityType && x.Attribute == filter.Attribute &&
                x.EntityId == filter.EntityId)
        );
    }
}