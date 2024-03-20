using Valtuutus.Core.Data;

namespace Valtuutus.Core;

public sealed class DataEngine
{
    private readonly IDataWriterProvider _writerProvider;

    public DataEngine(IDataWriterProvider writerProvider)
    {
        _writerProvider = writerProvider;
    }

    /// <summary>
    /// The Write function provides a way to insert relations and attributes to the data store.
    /// </summary>
    /// <param name="relations">List of relations</param>
    /// <param name="attributes">List of attributes</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>SnapToken containing the information of the last transaction created</returns>
    /// <exception cref="InvalidOperationException">If you provide two empty lists</exception>
    public Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes,
        CancellationToken ct)
    {
        var relationTuples = relations as RelationTuple[] ?? relations.ToArray();
        var attributeTuples = attributes as AttributeTuple[] ?? attributes.ToArray();
        if (relationTuples.Length == 0 && attributeTuples.Length == 0)
            throw new InvalidOperationException("Please provide at least one attribute or relation");
        return _writerProvider.Write(relationTuples, attributeTuples, ct);
    }

    
    /// <summary>
    /// The Delete function provides a way to delete relations and attributes from the data store.
    /// </summary>
    /// <param name="filter">The filter containing information for deletion of attributes and relations</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>SnapToken containing the information of the last transaction created</returns>
    /// <exception cref="InvalidOperationException">If you provide only empty filters</exception>
    public Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        if (filter.Relations.Length == 0 && filter.Attributes.Length == 0)
            throw new InvalidOperationException("Please provide at least one filter");
        return _writerProvider.Delete(filter, ct);
    }

    
}