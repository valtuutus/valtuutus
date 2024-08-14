namespace Valtuutus.Core.Data;

/// <summary>
/// Provides methods for writing data to the data store.
/// </summary>
public interface IDataWriterProvider
{
    /// <summary>
    /// Writes the specified relations and attributes to the data store.
    /// </summary>
    /// <param name="relations">The relations to write.</param>
    /// <param name="attributes">The attributes to write.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The SnapToken representing the state after the write operation.</returns>
    Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct);

    /// <summary>
    /// Deletes data from the data store based on the specified filter.
    /// </summary>
    /// <param name="filter">The filter criteria for deleting data.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The SnapToken representing the state after the delete operation.</returns>
    Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct);
}