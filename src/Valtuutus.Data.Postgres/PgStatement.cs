using System.Buffers;
using System.Text.RegularExpressions;
using Npgsql;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// A catalog statement in Npgsql's positional-parameter form. Statements are written with named
/// parameters (<c>@entity_id</c>) for readability and compiled once, when the catalog is built,
/// into <c>$1..$n</c> text plus the parameter order. Npgsql sends positional commands as-is,
/// whereas named ones are re-parsed and rewritten on every execution, at a cost that grows with
/// the statement's length.
/// </summary>
internal sealed partial class PgStatement
{
    private readonly string[] _parameterOrder;

    private PgStatement(string sql, string[] parameterOrder)
    {
        Sql = sql;
        _parameterOrder = parameterOrder;
    }

    /// <summary>The statement text with <c>$1..$n</c> placeholders.</summary>
    public string Sql { get; }

    /// <summary>
    /// Compiles a statement written with <c>@name</c> parameters: each distinct name becomes
    /// <c>$k</c>, numbered in order of first appearance, and every occurrence of the same name
    /// refers to the same <c>$k</c>.
    /// </summary>
    public static PgStatement Compile(string namedSql)
    {
        var order = new List<string>();
        var sql = ParameterName().Replace(namedSql, match =>
        {
            var name = match.Groups[1].Value;
            var index = order.IndexOf(name);
            if (index < 0)
            {
                order.Add(name);
                index = order.Count - 1;
            }
            return "$" + (index + 1);
        });
        return new PgStatement(sql, order.ToArray());
    }

    /// <summary>
    /// Puts the parameters a caller added by name into this statement's positional order and
    /// clears their names, so Npgsql binds them as <c>$1..$n</c>. Every parameter the statement
    /// references must have been added exactly once; one it does not reference is dropped, as
    /// Npgsql ignores it in a named command.
    /// </summary>
    public void Bind(NpgsqlParameterCollection parameters)
    {
        var count = _parameterOrder.Length;
        var ordered = ArrayPool<NpgsqlParameter>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                var index = Array.IndexOf(_parameterOrder, parameter.ParameterName);
                if (index < 0)
                    continue;
                if (ordered[index] is not null)
                    throw new InvalidOperationException($"Parameter '{parameter.ParameterName}' was added twice.");
                ordered[index] = parameter;
            }

            for (var i = 0; i < count; i++)
            {
                if (ordered[i] is null)
                    throw new InvalidOperationException($"Statement parameter '{_parameterOrder[i]}' was not added.");
            }

            parameters.Clear();
            for (var i = 0; i < count; i++)
            {
                ordered[i].ParameterName = "";
                parameters.Add(ordered[i]);
            }
        }
        finally
        {
            ArrayPool<NpgsqlParameter>.Shared.Return(ordered, clearArray: true);
        }
    }

    // A parameter reference: '@' followed by an identifier, not preceded by another identifier
    // character (so an address like a@b in a literal would not match — none of the catalog's SQL
    // contains one).
    [GeneratedRegex(@"(?<![\w@])@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ParameterName();
}
