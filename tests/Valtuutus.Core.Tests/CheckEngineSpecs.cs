using Valtuutus.Core.Schemas;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Core.Tests;

public sealed class CheckEngineSpecs : BaseCheckEngineSpecs
{
    protected override ValueTask<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var readerProvider = new InMemoryReaderProvider(tuples, attributes);
        return ValueTask.FromResult(new CheckEngine(readerProvider, schema ?? TestsConsts.Schemas));
    }
}