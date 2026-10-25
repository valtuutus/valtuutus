using System.Buffers;
using System.Data.Common;
using System.Text;
using MySqlConnector;

namespace Valtuutus.Data.MySql;

/// <summary>
/// A statement in MySqlConnector's positional-parameter form. Statements are written with named
/// parameters (<c>@entity_id</c>) for readability and compiled once, when the catalog is built, into
/// <c>?</c> text plus the name each placeholder reads. MySqlConnector looks up every named reference
/// in the text by name on every execution, copying the name out of the text each time, whereas a
/// <c>?</c> takes the next parameter in order. Session variables the statement reads stay named.
/// </summary>
internal sealed class MySqlStatement
{
    // Up to this many distinct names are found by a linear scan; a statement with more keeps an
    // index by name.
    private const int ScannedNames = 16;

    // Session variables a statement may read: they are assigned on the server, not bound.
    private static readonly HashSet<string> SessionVariables =
        new([MySqlQueries.BatchResolvedSnapToken[1..]], StringComparer.OrdinalIgnoreCase);

    // Each distinct parameter name, in order of first appearance.
    private readonly string[] _names;

    // For each placeholder in order, the index of the name it reads, and whether it is that name's
    // first placeholder.
    private readonly int[] _placeholders;
    private readonly bool[] _firstOfName;

    private readonly Dictionary<string, int>? _index;

    private MySqlStatement(string sql, string[] names, int[] placeholders, bool[] firstOfName)
    {
        Sql = sql;
        _names = names;
        _placeholders = placeholders;
        _firstOfName = firstOfName;
        if (names.Length > ScannedNames)
        {
            _index = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
                _index.Add(names[i], i);
        }
    }

    /// <summary>The statement text with <c>?</c> placeholders.</summary>
    public string Sql { get; }

    /// <summary>The distinct parameter names the statement reads, in order of first appearance.</summary>
    public IReadOnlyList<string> ParameterNames => _names;

    /// <summary>The number of <c>?</c> placeholders, one per parameter reference in the named text.</summary>
    public int PlaceholderCount => _placeholders.Length;

    /// <summary>
    /// Compiles a statement written with <c>@name</c> parameters. Each reference becomes one
    /// <c>?</c>, so a name referenced N times takes N placeholders. String literals, quoted
    /// identifiers, comments, <c>@@</c> system variables and the session variables are copied as they
    /// are.
    /// </summary>
    public static MySqlStatement Compile(string namedSql)
    {
        var sql = new StringBuilder(namedSql.Length);
        var names = new List<string>();
        var nameIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var placeholders = new List<int>();
        var firstOfName = new List<bool>();
        var i = 0;
        while (i < namedSql.Length)
        {
            var c = namedSql[i];
            var start = i;
            switch (c)
            {
                case '\'' or '"' or '`':
                    i = QuotedEnd(namedSql, i);
                    sql.Append(namedSql, start, i - start);
                    continue;
                case '#':
                case '-' when IsLineCommentStart(namedSql, i):
                    i = LineEnd(namedSql, i);
                    sql.Append(namedSql, start, i - start);
                    continue;
                case '/' when i + 1 < namedSql.Length && namedSql[i + 1] == '*':
                    var close = namedSql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? namedSql.Length : close + 2;
                    sql.Append(namedSql, start, i - start);
                    continue;
                case '?':
                    throw new ArgumentException(
                        $"The statement has a '?' placeholder at {i}; parameters are written as @name.",
                        nameof(namedSql));
                case '@' when i + 1 < namedSql.Length && namedSql[i + 1] == '@':
                    i += 2;
                    while (i < namedSql.Length && IsConnectorNameChar(namedSql[i]))
                        i++;
                    sql.Append(namedSql, start, i - start);
                    continue;
                case '@' when i + 1 < namedSql.Length && IsNameStart(namedSql[i + 1]):
                    i++;
                    while (i < namedSql.Length && IsNameChar(namedSql[i]))
                        i++;
                    // MySqlConnector reads these as part of a name after a '?' too, so the
                    // placeholder would swallow them.
                    if (i < namedSql.Length && IsConnectorNameChar(namedSql[i]))
                        throw new ArgumentException(
                            $"The parameter at {start} is followed by '{namedSql[i]}', which MySqlConnector " +
                            "reads as part of a name.",
                            nameof(namedSql));
                    var name = namedSql.Substring(start + 1, i - start - 1);
                    if (SessionVariables.Contains(name))
                    {
                        sql.Append(namedSql, start, i - start);
                        continue;
                    }
                    var first = nameIndex.TryAdd(name, names.Count);
                    if (first)
                        names.Add(name);
                    firstOfName.Add(first);
                    placeholders.Add(nameIndex[name]);
                    sql.Append('?');
                    continue;
                default:
                    sql.Append(c);
                    i++;
                    continue;
            }
        }
        return new MySqlStatement(sql.ToString(), names.ToArray(), placeholders.ToArray(), firstOfName.ToArray());
    }

    /// <summary>
    /// Puts the parameters a caller added by name, bare or <c>@</c>-prefixed, into this statement's
    /// placeholder order and clears their names, so MySqlConnector binds them to the <c>?</c>
    /// placeholders in turn. A name read by several placeholders binds the added parameter to the
    /// first and a copy of it to each later one. Every name the statement reads must have been
    /// added exactly once, and no other parameter may be.
    /// </summary>
    public void Bind(DbParameterCollection parameters)
    {
        var count = _names.Length;
        if (count == 0 && parameters.Count == 0)
            return;

        var added = ArrayPool<MySqlParameter>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = (MySqlParameter)parameters[i];
                var index = IndexOf(parameter.ParameterName);
                if (index < 0)
                    throw new InvalidOperationException(
                        $"Parameter '{parameter.ParameterName}' was added, but the statement does not read it.");
                if (added[index] is not null)
                    throw new InvalidOperationException($"Parameter '{_names[index]}' was added twice.");
                added[index] = parameter;
            }

            for (var i = 0; i < count; i++)
            {
                if (added[i] is null)
                    throw new InvalidOperationException($"Statement parameter '{_names[i]}' was not added.");
            }

            // Cleared out of the collection first, so the renames don't update its name index.
            parameters.Clear();
            for (var i = 0; i < count; i++)
                added[i].ParameterName = "";
            for (var p = 0; p < _placeholders.Length; p++)
            {
                var parameter = added[_placeholders[p]];
                parameters.Add(_firstOfName[p] ? parameter : parameter.Clone());
            }
        }
        finally
        {
            ArrayPool<MySqlParameter>.Shared.Return(added, clearArray: true);
        }
    }

    /// <summary>True when the statement reads the parameter <paramref name="name"/>.</summary>
    public bool Reads(string name) => IndexOf(name) >= 0;

    /// <summary>Sets the command's text and binds the parameters its binder added.</summary>
    public void ApplyTo(DbCommand command)
    {
        command.CommandText = Sql;
        Bind(command.Parameters);
    }

    /// <inheritdoc cref="ApplyTo(DbCommand)"/>
    public void ApplyTo(DbBatchCommand command)
    {
        command.CommandText = Sql;
        Bind(command.Parameters);
    }

    private int IndexOf(string parameterName)
    {
        var bare = parameterName.StartsWith('@') ? parameterName.AsSpan(1) : parameterName.AsSpan();
        if (_index is null)
        {
            for (var i = 0; i < _names.Length; i++)
            {
                if (bare.SequenceEqual(_names[i]))
                    return i;
            }
            return -1;
        }
#if NET9_0_OR_GREATER
        return _index.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(bare, out var found) ? found : -1;
#else
        return _index.TryGetValue(bare.Length == parameterName.Length ? parameterName : bare.ToString(), out var found)
            ? found
            : -1;
#endif
    }

    private static bool IsNameStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsNameChar(char c) => IsNameStart(c) || c is >= '0' and <= '9';

    // The characters MySqlConnector's SQL parser takes as part of a parameter or variable name.
    private static bool IsConnectorNameChar(char c) => IsNameChar(c) || c is '$' or '.' or >= '\u0080';

    // A quoted section ends at its closing quote. A doubled quote, and in '...' and "..." a
    // backslash, escapes the next character.
    private static int QuotedEnd(string sql, int start)
    {
        var quote = sql[start];
        var i = start + 1;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\\' && quote != '`')
            {
                i += 2;
                continue;
            }
            if (c == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    // "-- " starts a comment only when the dashes are followed by whitespace or a control character.
    private static bool IsLineCommentStart(string sql, int i) =>
        i + 2 < sql.Length && sql[i + 1] == '-' && (sql[i + 2] <= ' ' || sql[i + 2] == '\u007f');

    private static int LineEnd(string sql, int start)
    {
        var end = sql.IndexOf('\n', start);
        return end < 0 ? sql.Length : end;
    }
}
