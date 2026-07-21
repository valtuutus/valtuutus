using System.Collections;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace Valtuutus.Data.SqlServer.Utils;

internal static class TvpHelper
{
    internal const string TvpListIds = "TVP_ListIds";

    internal static SqlDataRecordTvp AsTvpParameter(IEnumerable<string> values, string typeName) =>
        new(values, typeName);

    // Implements IEnumerable<SqlDataRecord> so SQL Client can stream rows without a DataTable.
    // A single SqlDataRecord is allocated per enumeration and mutated in-place — safe
    // because SqlClient copies field values before moving to the next row.
    internal sealed class SqlDataRecordTvp(IEnumerable<string> values, string typeName)
        : IEnumerable<SqlDataRecord>
    {
        private static readonly SqlMetaData[] _meta = [new("id", SqlDbType.NVarChar, 256)];

        public void AddParameter(SqlParameterCollection parameters, string name)
        {
            var param = parameters.Add(name, SqlDbType.Structured);
            param.TypeName = typeName;
            param.Value = this;
        }

        public IEnumerator<SqlDataRecord> GetEnumerator() => new Enumerator(values.GetEnumerator());
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(IEnumerator<string> inner) : IEnumerator<SqlDataRecord>
        {
            private readonly SqlDataRecord _record = new(_meta);

            public SqlDataRecord Current => _record;
            object IEnumerator.Current => _record;

            public bool MoveNext()
            {
                if (!inner.MoveNext()) return false;
                _record.SetString(0, inner.Current);
                return true;
            }

            public void Reset() => inner.Reset();
            public void Dispose() => inner.Dispose();
        }
    }
}
