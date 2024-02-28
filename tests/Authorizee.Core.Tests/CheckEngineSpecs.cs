using Authorizee.Core.Schemas;
using Authorizee.Tests.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Core.Tests;

public sealed class CheckEngineSpecs : BaseCheckEngineSpecs
{
    protected override ValueTask<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        var logger = Substitute.For<ILogger<CheckEngine>>();
        return ValueTask.FromResult(new CheckEngine(relationTupleReader, attributeReader, schema ?? TestsConsts.Schemas, logger));
    }
}