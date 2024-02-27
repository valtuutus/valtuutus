using Authorizee.Core.Schemas;
using Authorizee.Tests.Shared;

namespace Authorizee.Core.Tests;

public sealed class LookupEntityEngineSpecs : BaseLookupEntityEngineSpecs
{
    protected override ValueTask<LookupEntityEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        return ValueTask.FromResult<LookupEntityEngine>(new LookupEntityEngine(schema ?? TestsConsts.Schemas, relationTupleReader, attributeReader));
    }
}