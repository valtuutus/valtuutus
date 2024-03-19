using Valtuutus.Core.Data;

namespace Valtuutus.Core;

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
        if (relationTuples.Length == 0 && attributeTuples.Length == 0)
            throw new InvalidOperationException("Please provide at least one attribute or relation");
        return _writerProvider.Write(relationTuples, attributeTuples, ct);
    }

    public Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        if (filter.Relations.Length == 0 && filter.Attributes.Length == 0)
            throw new InvalidOperationException("Please provide at least one filter");
        return _writerProvider.Delete(filter, ct);
    }

    
}