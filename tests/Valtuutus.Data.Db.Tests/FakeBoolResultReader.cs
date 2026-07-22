using System.Collections;
using System.Data.Common;

namespace Valtuutus.Data.Db.Tests;

// Minimal single-bool-column, 0-or-1-row DbDataReader test double — just enough surface to
// exercise UsersetJoinOp.ReadResultAsync, which only ever calls ReadAsync + GetBoolean(0)
// against the result set a batched HasUsersetJoinRelation command produces (one scalar row, or
// none if unmatched). `value: null` means the result set has zero rows.
internal sealed class FakeBoolResultReader(bool? value) : DbDataReader
{
    private bool _consumed;

    public override bool Read()
    {
        if (value is null || _consumed) return false;
        _consumed = true;
        return true;
    }

    public override bool GetBoolean(int ordinal) =>
        ordinal == 0 ? value!.Value : throw new IndexOutOfRangeException(ordinal.ToString());

    public override int FieldCount => 1;
    public override bool HasRows => value is not null;
    public override bool IsClosed => false;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override string GetName(int ordinal) => ordinal == 0 ? "value" : throw new IndexOutOfRangeException();
    public override int GetOrdinal(string name) => 0;
    public override Type GetFieldType(int ordinal) => typeof(bool);
    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
    public override bool IsDBNull(int ordinal) => false;
    public override object GetValue(int ordinal) => GetBoolean(ordinal);
    public override bool NextResult() => throw new NotSupportedException();
    public override int GetValues(object[] values) => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override string GetString(int ordinal) => throw new NotSupportedException();
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
