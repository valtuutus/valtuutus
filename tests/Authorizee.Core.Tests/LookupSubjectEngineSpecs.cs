using Authorizee.Core.Schemas;
using Authorizee.Tests.Shared;

namespace Authorizee.Core.Tests;

public sealed class LookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs
{
    protected override ValueTask<LookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        Schema? schema = null)
    {
        var readerProvider = new InMemoryReaderProvider(tuples, attributes);
        return ValueTask.FromResult<LookupSubjectEngine>(new LookupSubjectEngine(schema ?? TestsConsts.Schemas, readerProvider));
    }
}