using System.Data;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

/// <summary>
/// Provides methods for writing data to the data store using a provided connection and transaction.
/// </summary>
public interface IDbDataWriterProvider : IDataWriterProvider
{
    /// <summary>
    /// Writes the specified relations and attributes to the data store.
    /// </summary>
    /// <param name="connection">The connection to the database.</param>
    /// <param name="relations">The relations to write.</param>
    /// <param name="attributes">The attributes to write.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The SnapToken representing the state after the write operation.</returns>
    Task<SnapToken> Write(IDbConnection connection, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct);

    /// <summary>
    /// Writes the specified relations and attributes to the data store.
    /// </summary>
    /// <param name="connection">The connection to the database.</param>
    /// <param name="transaction">The transaction to be executed on a data source.</param>
    /// <param name="relations">The relations to write.</param>
    /// <param name="attributes">The attributes to write.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The SnapToken representing the state after the write operation.</returns>
    Task<SnapToken> Write(IDbConnection connection, IDbTransaction transaction, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct);

    /// <summary>
    /// Deletes data from the data store based on the specified filter.
    /// </summary>
    /// <param name="connection">The connection to the database.</param>
    /// <param name="filter">The filter criteria for deleting data.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The SnapToken representing the state after the delete operation.</returns>
    Task<SnapToken> Delete(IDbConnection connection, DeleteFilter filter, CancellationToken ct);

    /// <summary>
    /// Deletes data from the data store based on the specified filter.
    /// </summary>
    /// <param name="connection">The connection to the database.</param>
    /// <param name="transaction">The transaction to be executed on a data source.</param>
    /// <param name="filter">The filter criteria for deleting data.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The SnapToken representing the state after the delete operation.</returns>
    Task<SnapToken> Delete(IDbConnection connection, IDbTransaction transaction, DeleteFilter filter, CancellationToken ct);
}