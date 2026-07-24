using System.Collections;
using System.Data.Common;

namespace Valtuutus.Data.Db.Tests;

// Multi-result-set DbDataReader test double for BatchedPhysicalExecutorSpecs — exercises the
// NextResultAsync-per-command walk a real batched reader (ExecuteBatchAsync's return value)
// supports. Positioned at the first result set on construction, matching the real contract
// (IRelationalBatchOps.ExecuteBatchAsync's doc comment): callers read the first command's rows
// directly, then NextResultAsync to move to each subsequent command's result set. Every row is a
// plain object[] read back via GetBoolean/GetString — the only two accessors
// BatchedPhysicalExecutor's ReadResult ever calls against a batched reader.
internal sealed class FakeMultiResultReader(params IReadOnlyList<object[]>[] resultSets) : DbDataReader
{
    private int _resultIndex;
    private int _rowIndex = -1;

    private IReadOnlyList<object[]> CurrentResultSet => resultSets[_resultIndex];

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        _resultIndex++;
        _rowIndex = -1;
        return Task.FromResult(_resultIndex < resultSets.Length);
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        _rowIndex++;
        return Task.FromResult(_rowIndex < CurrentResultSet.Count);
    }

    public override bool GetBoolean(int ordinal) => (bool)CurrentResultSet[_rowIndex][ordinal];
    public override string GetString(int ordinal) => (string)CurrentResultSet[_rowIndex][ordinal];

    public override bool Read() => throw new NotSupportedException();
    public override bool NextResult() => throw new NotSupportedException();
    public override int FieldCount => CurrentResultSet.Count == 0 ? 0 : CurrentResultSet[0].Length;
    public override bool HasRows => CurrentResultSet.Count > 0;
    public override bool IsClosed => false;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override string GetName(int ordinal) => throw new NotSupportedException();
    public override int GetOrdinal(string name) => throw new NotSupportedException();
    public override Type GetFieldType(int ordinal) => throw new NotSupportedException();
    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
    public override bool IsDBNull(int ordinal) => false;
    public override object GetValue(int ordinal) => CurrentResultSet[_rowIndex][ordinal];
    public override int GetValues(object[] values) => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();
    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
    public override object this[int ordinal] => throw new NotSupportedException();
    public override object this[string name] => throw new NotSupportedException();
}
