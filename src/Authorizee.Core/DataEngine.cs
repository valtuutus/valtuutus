using Authorizee.Core.Data;

namespace Authorizee.Core;

public sealed class DataEngine
{
    private readonly IDataWriterProvider _writerProvider;

    public DataEngine(IDataWriterProvider writerProvider)
    {
        _writerProvider = writerProvider;
    }

    public Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes,
        CancellationToken ct)
    {
        var relationTuples = relations as RelationTuple[] ?? relations.ToArray();
        var attributeTuples = attributes as AttributeTuple[] ?? attributes.ToArray();
        if (!relationTuples.Any() && !attributeTuples.Any())
            throw new InvalidOperationException("Please provide at least one attribute or relation");
        return _writerProvider.Write(relationTuples, attributeTuples, ct);
    }

    public Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        return _writerProvider.Delete(filter, ct);
    }

    
}