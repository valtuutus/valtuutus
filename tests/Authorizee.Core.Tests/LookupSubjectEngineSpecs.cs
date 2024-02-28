using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Authorizee.Core.Schemas;
using Authorizee.Tests.Shared;
using FluentAssertions;

namespace Authorizee.Core.Tests;

public sealed class LookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs
{
    protected override ValueTask<LookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        return ValueTask.FromResult<LookupSubjectEngine>(new LookupSubjectEngine(schema ?? TestsConsts.Schemas, relationTupleReader, attributeReader));
    }
}