using System.Collections;
using System.Data.Common;

namespace Valtuutus.Data.Db.Tests;

// Minimal single-string-column, forward-only DbDataReader test double — just enough surface to
// exercise IBatchableCheckOp.ReadResultAsync, which only ever calls ReadAsync + GetString(0)
// against the result set a batched HasAnyOfDirectRelations/HasAnyOfAttributes command produces
// (one distinct-name column per matched row). Every other member throws: a real reader would
// never be asked for them by ReadResultAsync, so a call reaching them here is a test bug.
internal sealed class FakeStringResultReader(IEnumerable<string> rows) : DbDataReader
{
    private readonly IEnumerator<string> _enumerator = rows.GetEnumerator();

    public override bool Read() => _enumerator.MoveNext();
    public override string GetString(int ordinal) =>
        ordinal == 0 ? _enumerator.Current : throw new IndexOutOfRangeException(ordinal.ToString());

    public override int FieldCount => 1;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override string GetName(int ordinal) => ordinal == 0 ? "value" : throw new IndexOutOfRangeException();
    public override int GetOrdinal(string name) => 0;
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();
    public override bool IsDBNull(int ordinal) => false;
    public override object GetValue(int ordinal) => GetString(ordinal);
    public override bool NextResult() => throw new NotSupportedException();
    public override int GetValues(object[] values) => throw new NotSupportedException();
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
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
