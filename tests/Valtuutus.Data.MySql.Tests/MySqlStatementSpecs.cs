using System.Collections;
using System.Data.Common;
using System.Reflection;
using AwesomeAssertions;
using MySqlConnector;

namespace Valtuutus.Data.MySql.Tests;

public sealed class MySqlStatementSpecs
{
    [Fact]
    public void Each_reference_becomes_a_placeholder_and_repeated_names_repeat_in_the_order()
    {
        var statement = MySqlStatement.Compile("SELECT @a, @b FROM t WHERE x = @a AND y = @c AND z = @b");

        statement.Sql.Should().Be("SELECT ?, ? FROM t WHERE x = ? AND y = ? AND z = ?");
        statement.ParameterNames.Should().Equal("a", "b", "c");
        statement.PlaceholderCount.Should().Be(5);
        statement.Reads("a").Should().BeTrue();
        statement.Reads("@c").Should().BeTrue();
        statement.Reads("d").Should().BeFalse();
    }

    [Fact]
    public void Bind_orders_the_parameters_by_placeholder_and_copies_a_repeated_one()
    {
        var statement = MySqlStatement.Compile("SELECT @a, @b FROM t WHERE x = @a AND y = @c AND z = @b");
        using var command = new MySqlCommand();
        var c = new MySqlParameter("c", MySqlDbType.JSON) { Value = "[]" };
        var a = new MySqlParameter("@a", MySqlDbType.VarChar) { Size = 64, Value = "A" };
        var b = new MySqlParameter("b", MySqlDbType.String) { Size = 26, Value = "B" };
        command.Parameters.Add(c);
        command.Parameters.Add(a);
        command.Parameters.Add(b);

        statement.ApplyTo(command);

        command.CommandText.Should().Be(statement.Sql);
        var bound = command.Parameters.Cast<MySqlParameter>().ToList();
        bound.Select(p => p.Value).Should().Equal("A", "B", "A", "[]", "B");
        bound.Should().OnlyContain(p => p.ParameterName == "");
        bound[0].Should().BeSameAs(a);
        bound[1].Should().BeSameAs(b);
        bound[3].Should().BeSameAs(c);
        bound[2].Should().NotBeSameAs(a);
        bound[2].MySqlDbType.Should().Be(MySqlDbType.VarChar);
        bound[2].Size.Should().Be(64);
        bound[4].Should().NotBeSameAs(b);
        bound[4].MySqlDbType.Should().Be(MySqlDbType.String);
        bound[4].Size.Should().Be(26);
    }

    [Fact]
    public void Bind_works_on_a_batch_command()
    {
        var statement = MySqlStatement.Compile("SELECT @a WHERE @a = @b");
        using var batch = new MySqlBatch();
        var command = batch.CreateBatchCommand();
        command.Parameters.Add(new MySqlParameter("b", "B"));
        command.Parameters.Add(new MySqlParameter("a", "A"));

        statement.ApplyTo(command);

        command.CommandText.Should().Be("SELECT ? WHERE ? = ?");
        command.Parameters.Cast<MySqlParameter>().Select(p => p.Value).Should().Equal("A", "A", "B");
    }

    [Fact]
    public void A_statement_with_many_names_finds_each_parameter()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"p{i}").ToList();
        var statement = MySqlStatement.Compile("SELECT " + string.Join(", ", names.Select(n => "@" + n)) + ", @p3");
        using var command = new MySqlCommand();
        foreach (var name in Enumerable.Reverse(names))
            command.Parameters.Add(new MySqlParameter(name.Length % 2 == 0 ? name : "@" + name, name));

        statement.Bind(command.Parameters);

        command.Parameters.Cast<MySqlParameter>().Select(p => p.Value).Should().Equal(names.Append("p3"));
    }

    [Fact]
    public void A_missing_parameter_throws()
    {
        var statement = MySqlStatement.Compile("SELECT 1 WHERE entity_type = @entity_type AND entity_id = @entity_id");
        using var command = new MySqlCommand();
        command.Parameters.Add(new MySqlParameter("entity_id", "d1"));

        var act = () => statement.Bind(command.Parameters);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'entity_type'*not added*");
    }

    [Fact]
    public void A_misspelt_parameter_throws()
    {
        var statement = MySqlStatement.Compile("SELECT 1 WHERE entity_type = @entity_type");
        using var command = new MySqlCommand();
        command.Parameters.Add(new MySqlParameter("@entity_typ", "doc"));

        var act = () => statement.Bind(command.Parameters);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_extra_parameter_throws()
    {
        var statement = MySqlStatement.Compile("SELECT 1 WHERE entity_type = @entity_type");
        using var command = new MySqlCommand();
        command.Parameters.Add(new MySqlParameter("entity_type", "doc"));
        command.Parameters.Add(new MySqlParameter("relation", "viewer"));

        var act = () => statement.Bind(command.Parameters);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'relation'*does not read*");
    }

    [Fact]
    public void An_extra_parameter_throws_on_a_statement_that_reads_none()
    {
        var statement = MySqlStatement.Compile("SELECT 1");
        using var command = new MySqlCommand();
        command.Parameters.Add(new MySqlParameter("relation", "viewer"));

        var act = () => statement.Bind(command.Parameters);

        act.Should().Throw<InvalidOperationException>();
    }

    // MySqlParameterCollection refuses a second parameter under the same name, bare or prefixed, so
    // the duplicate goes through a collection that keeps whatever it is given.
    [Fact]
    public void A_parameter_added_twice_throws()
    {
        var statement = MySqlStatement.Compile("SELECT 1 WHERE entity_type = @entity_type");
        var parameters = new ListParameterCollection
        {
            new MySqlParameter("entity_type", "doc"),
            new MySqlParameter("@entity_type", "doc"),
        };

        var act = () => statement.Bind(parameters);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'entity_type'*twice*");
    }

    [Fact]
    public void A_statement_that_reads_no_parameter_binds_none()
    {
        var statement = MySqlStatement.Compile("SELECT 1");
        using var command = new MySqlCommand();

        statement.ApplyTo(command);

        command.CommandText.Should().Be("SELECT 1");
        command.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void The_session_variable_stays_named()
    {
        var statement = MySqlStatement.Compile(
            $"SET {MySqlQueries.BatchResolvedSnapToken} = 'x'; SELECT 1 WHERE a <= {MySqlQueries.BatchResolvedSnapToken} AND b = @b");

        statement.Sql.Should().Be(
            $"SET {MySqlQueries.BatchResolvedSnapToken} = 'x'; SELECT 1 WHERE a <= {MySqlQueries.BatchResolvedSnapToken} AND b = ?");
        statement.ParameterNames.Should().Equal("b");
    }

    [Theory]
    [InlineData("SELECT @@version, @@session.time_zone, @@global.max_allowed_packet")]
    [InlineData("SELECT '@a', 'it''s @a', 'back\\'slash @a', \"@a\", `@a`")]
    [InlineData("SELECT x FROM JSON_TABLE('[]', '$[*]' COLUMNS (id VARBINARY(257) PATH '$', t VARBINARY(257) PATH '$.entity_type')) j")]
    [InlineData("SELECT /*+ NO_RANGE_OPTIMIZATION(r) */ 1 /* @a */")]
    [InlineData("SELECT 1 -- @a\n")]
    [InlineData("SELECT 1 # @a\n")]
    [InlineData("SELECT a@ b")]
    public void Text_that_is_not_a_parameter_is_left_alone(string sql)
    {
        var statement = MySqlStatement.Compile(sql);

        statement.Sql.Should().Be(sql);
        statement.ParameterNames.Should().BeEmpty();
    }

    [Fact]
    public void A_parameter_after_a_literal_or_comment_is_still_replaced()
    {
        var statement = MySqlStatement.Compile("SELECT '@a' /* @b */, @c, '$[*]', @@version, @d -- @e\n, @f");

        statement.Sql.Should().Be("SELECT '@a' /* @b */, ?, '$[*]', @@version, ? -- @e\n, ?");
        statement.ParameterNames.Should().Equal("c", "d", "f");
    }

    [Fact]
    public void A_question_mark_in_the_named_text_throws()
    {
        var act = () => MySqlStatement.Compile("SELECT ? WHERE a = @a");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("SELECT @a.b")]
    [InlineData("SELECT @a$")]
    public void A_parameter_followed_by_a_name_character_throws(string sql)
    {
        var act = () => MySqlStatement.Compile(sql);

        act.Should().Throw<ArgumentException>();
    }

    // Every catalog text keeps no named reference other than the session variable.
    [Fact]
    public void Catalog_statements_read_only_placeholders_and_the_session_variable()
    {
        foreach (var serverType in Enum.GetValues<MySqlServerType>())
        {
            var options = new ValtuutusMySqlOptions(serverType);
            foreach (var queries in new[] { MySqlQueries.Get(options), MySqlQueries.GetBatchResolved(options) })
            {
                var statements = typeof(MySqlQueries).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.PropertyType == typeof(MySqlStatement))
                    .Select(p => (MySqlStatement)p.GetValue(queries)!)
                    .ToList();

                statements.Should().NotBeEmpty();
                foreach (var statement in statements)
                {
                    statement.Sql.Replace(MySqlQueries.BatchResolvedSnapToken, "").Should().NotContain("@");
                    statement.Sql.Count(c => c == '?').Should().Be(statement.PlaceholderCount);
                }
            }
        }
    }

    private sealed class ListParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        public override int Count => _items.Count;
        public override object SyncRoot => _items;

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
                Add(value);
        }

        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _items[index];
        protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) =>
            _items[IndexOf(parameterName)] = value;
    }
}
