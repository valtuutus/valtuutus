namespace Authorizee.Core.Data;

public interface IDataWriterProvider
{
    public Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes);

    public Task<SnapToken> Delete();
}