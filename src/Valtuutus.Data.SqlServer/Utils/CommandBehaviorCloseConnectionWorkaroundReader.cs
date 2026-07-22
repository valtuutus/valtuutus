using System.Collections;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer.Utils;

/// <summary>
/// WORKAROUND, not a design choice — delete once the pinned Microsoft.Data.SqlClient version
/// fixes <c>CommandBehavior.CloseConnection</c> for <see cref="SqlBatch"/>.
///
/// Confirmed with a Valtuutus-free raw ADO.NET repro (open connection, run
/// <c>SqlBatch.ExecuteReaderAsync(CommandBehavior.CloseConnection)</c>, dispose the reader, check
/// <c>connection.State</c>): the connection stays <c>Open</c> on Microsoft.Data.SqlClient 6.0.1
/// and 6.1.6 (the latest 6.x as of this writing) — <c>CommandBehavior.CloseConnection</c> is
/// simply not honored for <see cref="SqlBatch"/>, unlike <see cref="SqlCommand"/>, where it
/// reliably works. Same repro against <c>7.1.0-preview2.26190.5</c> shows it fixed there, but
/// that's a prerelease with no GA yet, so it's not something to depend on for this fix.
///
/// Every batched query call otherwise leaked one physical connection permanently (never returned
/// to the pool), eventually exhausting it under sustained load.
///
/// This wraps the raw reader so the connection's disposal is tied explicitly to the reader's
/// lifetime instead — exactly what <c>CommandBehavior.CloseConnection</c> was supposed to do.
/// </summary>
internal sealed class CommandBehaviorCloseConnectionWorkaroundReader(DbDataReader inner, SqlConnection connection)
    : DbDataReader
{
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        inner.Dispose();
        if (disposing) connection.Dispose();
    }

    public override bool Read() => inner.Read();
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
    public override bool NextResult() => inner.NextResult();
    public override int FieldCount => inner.FieldCount;
    public override bool HasRows => inner.HasRows;
    public override bool IsClosed => inner.IsClosed;
    public override int Depth => inner.Depth;
    public override int RecordsAffected => inner.RecordsAffected;
    public override object this[int ordinal] => inner[ordinal];
    public override object this[string name] => inner[name];
    public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
    public override string GetName(int ordinal) => inner.GetName(ordinal);
    public override int GetOrdinal(string name) => inner.GetOrdinal(name);
    public override string GetString(int ordinal) => inner.GetString(ordinal);
    public override object GetValue(int ordinal) => inner.GetValue(ordinal);
    public override int GetValues(object[] values) => inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
    public override IEnumerator GetEnumerator() => inner.GetEnumerator();
}
