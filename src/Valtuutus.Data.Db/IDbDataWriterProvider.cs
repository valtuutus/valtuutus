using System.Data;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

public interface IDbDataWriterProvider
{
    public Task<SnapToken> Write(IDbConnection connection, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct);
    
    public Task<SnapToken> WriteRelations(IDbConnection connection, IEnumerable<RelationTuple> relations, CancellationToken ct);
    
    public Task<SnapToken> WriteAttributes(IDbConnection connection, IEnumerable<AttributeTuple> attributes, CancellationToken ct);
    
    public Task<SnapToken> Delete(IDbConnection connection, DeleteFilter filter, CancellationToken ct);
    
    public Task<SnapToken> Write(IDbConnection connection, IDbTransaction transaction, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct);
    
    public Task<SnapToken> WriteRelations(IDbConnection connection, IDbTransaction transaction, IEnumerable<RelationTuple> relations, CancellationToken ct);
    
    public Task<SnapToken> WriteAttributes(IDbConnection connection, IDbTransaction transaction, IEnumerable<AttributeTuple> attributes, CancellationToken ct);
    
    public Task<SnapToken> Delete(IDbConnection connection, IDbTransaction transaction, DeleteFilter filter, CancellationToken ct);
}