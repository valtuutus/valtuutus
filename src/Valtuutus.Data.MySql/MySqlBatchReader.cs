using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Data.MySql;

/// <summary>
/// The reader <see cref="MySqlBatchOps.ExecuteBatchAsync"/> returns. Key columns are VARBINARY, which
/// MySqlConnector refuses to read with <see cref="DbDataReader.GetString"/>; the shared batch readers
/// read every key that way, so here <see cref="GetString"/> decodes a binary field's bytes as UTF-8.
/// Every other member reads the inner reader unchanged. Disposing this reader disposes the inner one,
/// then the connection the batch ran on, which goes back to the pool.
/// </summary>
internal sealed class MySqlBatchReader(DbDataReader inner, DbConnection connection) : DbDataReader
{
    // Ordinals of the current result set whose field type is byte[], one bit each, worked out on
    // the first GetString of the result set. Ordinals past the mask's width are checked per call.
    private const int MaskWidth = 64;
    private ulong _binaryMask;
    private bool _binaryMaskKnown;

    // Reused across every binary field this reader decodes, so each decode allocates only its string.
    private byte[]? _keyBuffer;

    // Set once the inner reader and the connection have been closed or disposed.
    private bool _closed;
    private bool _disposed;

    public override string GetString(int ordinal)
    {
        if (!IsBinary(ordinal))
            return inner.GetString(ordinal);
        _keyBuffer ??= MySqlSql.RentKeyBuffer();
        return MySqlSql.ReadKey(inner, ordinal, ref _keyBuffer);
    }

    private bool IsBinary(int ordinal)
    {
        if (ordinal >= MaskWidth)
            return inner.GetFieldType(ordinal) == typeof(byte[]);
        if (!_binaryMaskKnown)
        {
            var mask = 0UL;
            var count = Math.Min(inner.FieldCount, MaskWidth);
            for (var i = 0; i < count; i++)
            {
                if (inner.GetFieldType(i) == typeof(byte[]))
                    mask |= 1UL << i;
            }
            _binaryMask = mask;
            _binaryMaskKnown = true;
        }
        return (_binaryMask & (1UL << ordinal)) != 0;
    }

    public override bool NextResult()
    {
        _binaryMaskKnown = false;
        return inner.NextResult();
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        _binaryMaskKnown = false;
        return inner.NextResultAsync(cancellationToken);
    }

    public override bool Read() => inner.Read();

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);

    public override int Depth => inner.Depth;

    public override int FieldCount => inner.FieldCount;

    public override int VisibleFieldCount => inner.VisibleFieldCount;

    public override bool HasRows => inner.HasRows;

    public override bool IsClosed => inner.IsClosed;

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

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);

    public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);

    public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);

    public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);

    public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);

    public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);

    public override string GetName(int ordinal) => inner.GetName(ordinal);

    public override int GetOrdinal(string name) => inner.GetOrdinal(name);

    public override object GetValue(int ordinal) => inner.GetValue(ordinal);

    public override int GetValues(object[] values) => inner.GetValues(values);

    public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        inner.GetFieldValueAsync<T>(ordinal, cancellationToken);

    public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        inner.IsDBNullAsync(ordinal, cancellationToken);

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetProviderSpecificFieldType(int ordinal) => inner.GetProviderSpecificFieldType(ordinal);

    public override object GetProviderSpecificValue(int ordinal) => inner.GetProviderSpecificValue(ordinal);

    public override int GetProviderSpecificValues(object[] values) => inner.GetProviderSpecificValues(values);

    public override Stream GetStream(int ordinal) => inner.GetStream(ordinal);

    public override TextReader GetTextReader(int ordinal) => inner.GetTextReader(ordinal);

    public override DataTable? GetSchemaTable() => inner.GetSchemaTable();

    public override Task<DataTable?> GetSchemaTableAsync(CancellationToken cancellationToken = default) =>
        inner.GetSchemaTableAsync(cancellationToken);

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    // Closing gives the connection back to the pool. Each step runs even when the one before throws,
    // so a failure while the inner reader drains its remaining results still releases the
    // connection and the key buffer. A second Close, or a second dispose, does nothing; disposing
    // after Close still disposes the inner reader and the connection.
    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            inner.Close();
        }
        finally
        {
            try
            {
                connection.Close();
            }
            finally
            {
                ReturnKeyBuffer();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _closed = true;
            try
            {
                inner.Dispose();
            }
            finally
            {
                try
                {
                    connection.Dispose();
                }
                finally
                {
                    ReturnKeyBuffer();
                }
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _closed = true;
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ReturnKeyBuffer();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void ReturnKeyBuffer()
    {
        if (_keyBuffer is null)
            return;
        MySqlSql.ReturnKeyBuffer(_keyBuffer);
        _keyBuffer = null;
    }
}
