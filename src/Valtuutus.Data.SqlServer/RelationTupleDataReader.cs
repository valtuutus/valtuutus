using System.Collections;
using System.Data.Common;
using Valtuutus.Core;

namespace Valtuutus.Data.SqlServer;

internal sealed class RelationTupleDataReader : DbDataReader
{
    private static readonly string[] ColumnNames =
    [
        "EntityType", "EntityId", "SubjectType", "SubjectId", "Relation", "SubjectRelation", "TransactionId"
    ];

    private readonly IEnumerator<RelationTuple> _enumerator;
    private readonly string _transactionId;
    private bool _isClosed;

    public RelationTupleDataReader(IEnumerable<RelationTuple> relations, string transactionId)
    {
        _enumerator = relations.GetEnumerator();
        _transactionId = transactionId;
    }

    public override int FieldCount => ColumnNames.Length;

    public override bool HasRows => true;

    public override bool IsClosed => _isClosed;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override string GetName(int ordinal) => ColumnNames[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < ColumnNames.Length; i++)
        {
            if (string.Equals(ColumnNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException(name);
    }

    public override Type GetFieldType(int ordinal) => typeof(string);

    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();

    public override bool Read() => _enumerator.MoveNext();

    public override bool IsDBNull(int ordinal) => false;

    public override object GetValue(int ordinal)
    {
        var current = _enumerator.Current;
        return ordinal switch
        {
            0 => current.EntityType,
            1 => current.EntityId,
            2 => current.SubjectType,
            3 => current.SubjectId,
            4 => current.Relation,
            5 => current.SubjectRelation,
            6 => _transactionId,
            _ => throw new IndexOutOfRangeException(ordinal.ToString())
        };
    }

    public override void Close() => _isClosed = true;

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
    public override string GetString(int ordinal) => throw new NotSupportedException();
    public override object this[int ordinal] => throw new NotSupportedException();
    public override object this[string name] => throw new NotSupportedException();
}
