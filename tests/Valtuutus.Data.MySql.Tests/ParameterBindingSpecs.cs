using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using MySqlConnector;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests;

/// <summary>
/// Runs every code path that writes a statement's parameters, through the provider's own binders,
/// and checks it binds exactly the <c>@names</c> the statement text reads, apart from the
/// batch-resolved session variable. The data source allows user variables, which the
/// batch-resolved token needs, so a missing or misspelt name would read as a NULL user variable
/// instead of failing; an extra name would mean the binder and the statement disagree. Statements
/// that read the batch-resolved token must bind no snap-token parameter. Commands are built without
/// a connection, so no database is needed.
/// </summary>
public sealed partial class ParameterBindingSpecs
{
    private static readonly SnapToken Token = new("01JAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly EntityScope Scope = new("member", "team", "t1");
    private static readonly int[] Counts = [1, 2];
    private static readonly EntityScope?[] Scopes = [null, Scope];
    private static readonly string BatchResolvedName = Bare(MySqlQueries.BatchResolvedSnapToken);

    private static IEnumerable<ValtuutusMySqlOptions> EveryServer() =>
        Enum.GetValues<MySqlServerType>().Select(t => new ValtuutusMySqlOptions(t));

    private static string[] Values(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => $"{prefix}{i}").ToArray();

    private static string ScopeName(EntityScope? scope) => scope is null ? "unscoped" : "scoped";

    // A statement-and-parameters binder over one catalog. BatchResolvable is false for binders that
    // take their token from a filter, which only the bound-token reader calls.
    private sealed record ReadCase(string Name, Func<DbParameterCollection, MySqlQueries, SnapToken?, string> Bind,
        bool BatchResolvable = true);

    private static readonly Dictionary<string, ReadCase> ReadCases =
        LookupReadCases().Concat(CheckReadCases()).ToDictionary(c => c.Name);

    private static IEnumerable<ReadCase> LookupReadCases()
    {
        foreach (var n in Counts)
        {
            var ids = Values("i", n);
            var names = Values("n", n);
            foreach (var scope in Scopes)
            {
                yield return new($"{nameof(MySqlLookupReads.RelationsWithSubjectIds)}/{n}/{ScopeName(scope)}",
                    (p, q, t) => MySqlLookupReads.RelationsWithSubjectIds(p, q, "doc", "viewer", ids, "user", "",
                        scope, t));
                yield return new(
                    $"{nameof(MySqlLookupReads.RelationsWithSubjectIdsMultiRelation)}/{n}/{ScopeName(scope)}",
                    (p, q, t) => MySqlLookupReads.RelationsWithSubjectIdsMultiRelation(p, q, "doc", names, ids, "user",
                        "", scope, t));
                foreach (var entityId in new[] { null, "e0" })
                {
                    yield return new(
                        $"{nameof(MySqlLookupReads.AttributesByAttributes)}/{n}/{ScopeName(scope)}/{entityId ?? "every entity"}",
                        (p, q, t) => MySqlLookupReads.AttributesByAttributes(p, q, "doc", names, entityId, scope, t));
                }
            }
            yield return new($"{nameof(MySqlLookupReads.RelationsWithEntityIds)}/{n}",
                (p, q, t) => MySqlLookupReads.RelationsWithEntityIds(p, q, "doc", "viewer", "user", ids, "", t));
            yield return new($"{nameof(MySqlLookupReads.RelationsWithEntityIdsMultiRelation)}/{n}",
                (p, q, t) => MySqlLookupReads.RelationsWithEntityIdsMultiRelation(p, q, "doc", names, "user", ids, "",
                    t));
            yield return new($"{nameof(MySqlLookupReads.RelationsJoinedByEntityIds)}/{n}",
                (p, q, t) => MySqlLookupReads.RelationsJoinedByEntityIds(p, q, "doc", "parent", ids, "folder",
                    "member", "viewer", "user", t));
            yield return new($"{nameof(MySqlLookupReads.AttributeWithEntityIds)}/{n}",
                (p, q, t) => MySqlLookupReads.AttributeWithEntityIds(p, q, "doc", "public", ids, t));
            yield return new($"{nameof(MySqlLookupReads.AttributesWithEntityIds)}/{n}",
                (p, q, t) => MySqlLookupReads.AttributesWithEntityIds(p, q, "doc", names, ids, t));
            yield return new($"{nameof(MySqlLookupReads.EntityIdsExcluding)}/{n}",
                (p, q, t) => MySqlLookupReads.EntityIdsExcluding(p, q, "doc", ids, t));
            yield return new($"{nameof(MySqlLookupReads.SubjectIdsExcluding)}/{n}",
                (p, q, t) => MySqlLookupReads.SubjectIdsExcluding(p, q, "user", ids, t));
        }
        foreach (var scope in Scopes)
        {
            yield return new($"{nameof(MySqlLookupReads.RelationsJoined)}/{ScopeName(scope)}",
                (p, q, t) => MySqlLookupReads.RelationsJoined(p, q, "doc", "parent", "folder", "member", "viewer",
                    "user", "alice", scope, t));
        }
        foreach (var entityId in new[] { null, "e0" })
        {
            yield return new($"{nameof(MySqlLookupReads.AttributesByAttribute)}/{entityId ?? "every entity"}",
                (p, q, t) => MySqlLookupReads.AttributesByAttribute(p, q, "doc", "public", entityId, t));
        }
    }

    private static RelationTupleFilter Filter(string? subjectType = null, string? subjectId = null,
        string? subjectRelation = null) => new()
    {
        EntityType = "doc",
        EntityId = "d1",
        Relation = "viewer",
        SubjectType = subjectType,
        SubjectId = subjectId,
        SubjectRelation = subjectRelation,
        SnapToken = Token,
    };

    private static IEnumerable<ReadCase> CheckReadCases()
    {
        yield return new($"{nameof(MySqlCheckReads.GetRelations)}/no subject filter",
            (p, q, _) => MySqlCheckReads.GetRelations(p, q, Filter()), BatchResolvable: false);
        yield return new($"{nameof(MySqlCheckReads.GetRelations)}/subject id filter",
            (p, q, _) => MySqlCheckReads.GetRelations(p, q, Filter(subjectId: "alice")), BatchResolvable: false);
        yield return new($"{nameof(MySqlCheckReads.GetRelations)}/every subject filter",
            (p, q, _) => MySqlCheckReads.GetRelations(p, q, Filter("team", "t1", "member")), BatchResolvable: false);
        yield return new(nameof(MySqlCheckReads.HasDirectRelation),
            (p, q, _) => MySqlCheckReads.HasDirectRelation(p, q, Filter(), "alice"), BatchResolvable: false);
        yield return new(nameof(MySqlCheckReads.GetIndirectRelations),
            (p, q, _) => MySqlCheckReads.GetIndirectRelations(p, q, Filter()), BatchResolvable: false);
        yield return new(nameof(MySqlCheckReads.HasTupleToUserSetRelation),
            (p, q, t) => MySqlCheckReads.HasTupleToUserSetRelation(p, q, "doc", "d1", "parent", "folder", "viewer",
                "user", "alice", t));
        yield return new(nameof(MySqlCheckReads.HasUsersetJoinRelation),
            (p, q, t) => MySqlCheckReads.HasUsersetJoinRelation(p, q, "doc", "d1", "viewer", "team", "member",
                "user", "alice", t));
        yield return new(nameof(MySqlCheckReads.HasTrueBoolAttribute),
            (p, q, t) => MySqlCheckReads.HasTrueBoolAttribute(p, q, "doc", "d1", "public", t));
        foreach (var n in Counts)
        {
            var values = Values("v", n);
            yield return new($"{nameof(MySqlCheckReads.HasAnyDirectRelation)}/{n}",
                (p, q, t) => MySqlCheckReads.HasAnyDirectRelation(p, q, "doc", values, "viewer", "alice", t));
            yield return new($"{nameof(MySqlCheckReads.HasAnyOfDirectRelations)}/{n}",
                (p, q, t) => MySqlCheckReads.HasAnyOfDirectRelations(p, q, "doc", "d1", values, "alice", t));
            yield return new($"{nameof(MySqlCheckReads.HasAnyOfAttributes)}/{n}",
                (p, q, t) => MySqlCheckReads.HasAnyOfAttributes(p, q, "doc", "d1", values, t));
            yield return new($"{nameof(MySqlCheckReads.AttributesSingleEntity)}/{n}",
                (p, q, t) => MySqlCheckReads.AttributesSingleEntity(p, q, "doc", "d1", values, t));
        }
        foreach (var (name, leaves) in LeafSets())
        {
            foreach (var requireAll in new[] { false, true })
            {
                yield return new($"{nameof(MySqlCheckReads.HasFusedExpression)}/{name}/{(requireAll ? "all" : "any")}",
                    (p, q, t) => MySqlCheckReads.HasFusedExpression(p, q, "doc", "d1", leaves, requireAll, "user",
                        "alice", t));
            }
        }
    }

    // Each leaf kind on its own, in both all-of and any-of forms where the kind has them, and every
    // kind together.
    private static IEnumerable<(string Name, FusedCheckLeaf[] Leaves)> LeafSets()
    {
        FusedCheckLeaf[] every =
        [
            new(FusedLeafKind.Direct, Negate: true, ["owner"]),
            new(FusedLeafKind.MultiDirect, Negate: false, ["editor", "admin"], RequireAll: false),
            new(FusedLeafKind.MultiDirect, Negate: true, ["editor", "admin"], RequireAll: true),
            new(FusedLeafKind.Attribute, Negate: false, ["public"]),
            new(FusedLeafKind.MultiAttribute, Negate: true, ["a0", "a1"], RequireAll: false),
            new(FusedLeafKind.MultiAttribute, Negate: false, ["a0", "a1"], RequireAll: true),
            new(FusedLeafKind.TupleToUserSet, Negate: false, ["parent"],
                TtuSubEntityType: "organization", TtuComputedRelation: "admin"),
        ];
        foreach (var leaf in every)
            yield return ($"{leaf.Kind}{(leaf.RequireAll ? " all" : "")}", [leaf]);
        yield return ("every kind", every);
    }

    public static TheoryData<string, bool> Reads()
    {
        var data = new TheoryData<string, bool>();
        foreach (var c in ReadCases.Values)
        {
            data.Add(c.Name, false);
            if (c.BatchResolvable)
                data.Add(c.Name, true);
        }
        return data;
    }

    // The bound-token catalog with a token, or the batch-resolved catalog with none, as the reader
    // and the lookup batch ops pair them.
    [Theory]
    [MemberData(nameof(Reads))]
    public void Reader_binder_binds_exactly_what_its_statement_reads(string name, bool batchResolved)
    {
        foreach (var options in EveryServer())
        {
            var queries = batchResolved ? MySqlQueries.GetBatchResolved(options) : MySqlQueries.Get(options);
            using var command = new MySqlCommand();

            var sql = ReadCases[name].Bind(command.Parameters, queries, batchResolved ? null : Token);

            ShouldBindExactlyWhatItReads(sql, command.Parameters, batchResolved, $"{name} on {options.ServerType}");
        }
    }

    [Fact]
    public void Every_reader_binder_has_a_case()
    {
        var binders = typeof(MySqlLookupReads).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Concat(typeof(MySqlCheckReads).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
            .Where(m => m.ReturnType == typeof(string))
            .Select(m => m.Name)
            .ToHashSet();
        var covered = ReadCases.Keys.Select(k => k.Split('/')[0]).ToHashSet();

        binders.Should().NotBeEmpty();
        binders.Should().BeSubsetOf(covered);
    }

    private static MySqlBatchOps NewBatchOps(ValtuutusMySqlOptions options) =>
        new(() => new MySqlConnection("Server=localhost;User ID=valtuutus;Password=unused"),
            new ValtuutusDataOptions(), options);

    private static readonly Dictionary<string, Action<IRelationalBatchOps, DbBatch>> BatchOpCases =
        BatchOpCaseList().ToDictionary(c => c.Name, c => c.Add);

    private static IEnumerable<(string Name, Action<IRelationalBatchOps, DbBatch> Add)> BatchOpCaseList()
    {
        yield return (nameof(IRelationalBatchOps.AddResolveLatestSnapTokenToBatch),
            (ops, b) => ops.AddResolveLatestSnapTokenToBatch(b));
        yield return (nameof(IRelationalBatchOps.AddHasDirectRelationToBatch),
            (ops, b) => ops.AddHasDirectRelationToBatch(b, Filter(), "alice"));
        yield return (nameof(IRelationalBatchOps.AddHasTrueBoolAttributeToBatch),
            (ops, b) => ops.AddHasTrueBoolAttributeToBatch(b, "doc", "d1", "public", Token));
        yield return (nameof(IRelationalBatchOps.AddHasTupleToUserSetRelationToBatch),
            (ops, b) => ops.AddHasTupleToUserSetRelationToBatch(b, "doc", "d1", "parent", "folder", "viewer", "user",
                "alice", Token));
        yield return (nameof(IRelationalBatchOps.AddHasUsersetJoinRelationToBatch),
            (ops, b) => ops.AddHasUsersetJoinRelationToBatch(b, "doc", "d1", "viewer", "team", "member", "user",
                "alice", Token));
        yield return ($"{nameof(IRelationalBatchOps.AddGetRelationsToBatch)}/no subject filter",
            (ops, b) => ops.AddGetRelationsToBatch(b, Filter()));
        yield return ($"{nameof(IRelationalBatchOps.AddGetRelationsToBatch)}/subject id filter",
            (ops, b) => ops.AddGetRelationsToBatch(b, Filter(subjectId: "alice")));
        yield return ($"{nameof(IRelationalBatchOps.AddGetRelationsToBatch)}/every subject filter",
            (ops, b) => ops.AddGetRelationsToBatch(b, Filter("team", "t1", "member")));
        yield return (nameof(IRelationalBatchOps.AddGetIndirectRelationsToBatch),
            (ops, b) => ops.AddGetIndirectRelationsToBatch(b, Filter()));
        foreach (var n in Counts)
        {
            var values = Values("v", n);
            yield return ($"{nameof(IRelationalBatchOps.AddHasAnyDirectRelationToBatch)}/{n}",
                (ops, b) => ops.AddHasAnyDirectRelationToBatch(b, "doc", values, "viewer", "alice", Token));
            yield return ($"{nameof(IRelationalBatchOps.AddHasAnyOfDirectRelationsToBatch)}/{n}",
                (ops, b) => ops.AddHasAnyOfDirectRelationsToBatch(b, "doc", "d1", values, "alice", Token));
            yield return ($"{nameof(IRelationalBatchOps.AddHasAnyOfAttributesToBatch)}/{n}",
                (ops, b) => ops.AddHasAnyOfAttributesToBatch(b, "doc", "d1", values, Token));
            yield return ($"{nameof(IRelationalBatchOps.AddGetAttributesSingleEntityToBatch)}/{n}",
                (ops, b) => ops.AddGetAttributesSingleEntityToBatch(b, "doc", "d1", values, Token));
        }
        foreach (var (name, leaves) in LeafSets())
        {
            foreach (var requireAll in new[] { false, true })
            {
                yield return ($"{nameof(IRelationalBatchOps.AddHasFusedExpressionToBatch)}/{name}/{(requireAll ? "all" : "any")}",
                    (ops, b) => ops.AddHasFusedExpressionToBatch(b, "doc", "d1", leaves, requireAll, "user", "alice",
                        Token));
            }
        }
    }

    public static TheoryData<string, bool> BatchOps()
    {
        var data = new TheoryData<string, bool>();
        foreach (var name in BatchOpCases.Keys)
        {
            data.Add(name, false);
            data.Add(name, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BatchOps))]
    public void Batch_op_binds_exactly_what_its_statement_reads(string name, bool batchResolved)
    {
        foreach (var options in EveryServer())
        {
            var owner = NewBatchOps(options);
            var ops = batchResolved ? owner.UsingBatchResolvedSnapToken! : owner;
            using var batch = new MySqlBatch();

            BatchOpCases[name](ops, batch);

            batch.BatchCommands.Should().ContainSingle();
            var command = batch.BatchCommands[0];
            ShouldBindExactlyWhatItReads(command.CommandText, command.Parameters, batchResolved,
                $"{name} on {options.ServerType}");
        }
    }

    [Fact]
    public void Every_batch_op_has_a_case()
    {
        var members = typeof(MySqlBatchOps).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Add", StringComparison.Ordinal)
                && m.Name.EndsWith("ToBatch", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToHashSet();
        var covered = BatchOpCases.Keys.Select(k => k.Split('/')[0]).ToHashSet();

        members.Should().NotBeEmpty();
        members.Should().BeEquivalentTo(covered);
    }

    public static TheoryData<LookupEntityQueryKind, int, int, bool, bool, int> LookupEntityQueries()
    {
        var data = new TheoryData<LookupEntityQueryKind, int, int, bool, bool, int>();
        var options = new ValtuutusMySqlOptions(MySqlServerType.MySql);
        foreach (var kind in Enum.GetValues<LookupEntityQueryKind>())
        {
            foreach (var withToken in new[] { false, true })
            {
                if (kind is LookupEntityQueryKind.RelationsLevels or LookupEntityQueryKind.RelationsLevelsFromSubject)
                {
                    var statements = MySqlLookupEntityBatchOps.RelationsLevelsStatements(options,
                        MySqlQueries.SnapTokenParameter, kind is LookupEntityQueryKind.RelationsLevelsFromSubject);
                    foreach (var levels in LevelCounts(statements))
                    {
                        data.Add(kind, 1, 1, false, withToken, levels);
                        data.Add(kind, 2, 1, false, withToken, levels);
                    }
                    continue;
                }
                foreach (var ids in Counts)
                foreach (var names in Counts)
                foreach (var scoped in new[] { false, true })
                    data.Add(kind, ids, names, scoped, withToken, 0);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LookupEntityQueries))]
    public void Lookup_entity_batch_op_binds_exactly_what_its_statement_reads(LookupEntityQueryKind kind, int ids,
        int names, bool scoped, bool withToken, int levels)
    {
        var query = new LookupEntityQuery
        {
            Kind = kind,
            EntityType = "doc",
            Relation = "viewer",
            Names = Values("n", names),
            SubjectType = "user",
            SubjectRelation = "",
            SubEntityType = "folder",
            SubRelation = "member",
            Ids = Values("i", ids),
            Scope = scoped ? Scope : null,
            Levels = levels,
            LevelOneIds = ["l0"],
        };
        foreach (var options in EveryServer())
        {
            using var batch = new MySqlBatch();

            new MySqlLookupEntityBatchOps(options).AddToBatch(batch, query, withToken ? Token : null);

            batch.BatchCommands.Should().ContainSingle();
            var command = batch.BatchCommands[0];
            ShouldBindExactlyWhatItReads(command.CommandText, command.Parameters, batchResolved: !withToken,
                $"{kind} on {options.ServerType}");
        }
    }

    public static TheoryData<LookupSubjectQueryKind, int, int, bool, int> LookupSubjectQueries()
    {
        var data = new TheoryData<LookupSubjectQueryKind, int, int, bool, int>();
        var options = new ValtuutusMySqlOptions(MySqlServerType.MySql);
        foreach (var kind in Enum.GetValues<LookupSubjectQueryKind>())
        {
            foreach (var withToken in new[] { false, true })
            {
                if (kind is LookupSubjectQueryKind.UsersetLevels)
                {
                    var statements = MySqlLookupSubjectBatchOps.UsersetLevelsStatements(options,
                        MySqlQueries.SnapTokenParameter);
                    foreach (var levels in LevelCounts(statements))
                    {
                        data.Add(kind, 1, 1, withToken, levels);
                        data.Add(kind, 2, 1, withToken, levels);
                    }
                    continue;
                }
                foreach (var ids in Counts)
                foreach (var names in Counts)
                    data.Add(kind, ids, names, withToken, 0);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LookupSubjectQueries))]
    public void Lookup_subject_batch_op_binds_exactly_what_its_statement_reads(LookupSubjectQueryKind kind, int ids,
        int names, bool withToken, int levels)
    {
        var query = new LookupSubjectQuery
        {
            Kind = kind,
            EntityType = "doc",
            Relation = "viewer",
            Names = Values("n", names),
            SubjectType = "user",
            SubjectRelation = "",
            SubEntityType = "team",
            SubRelation = "member",
            Ids = Values("i", ids),
            Levels = levels,
        };
        foreach (var options in EveryServer())
        {
            using var batch = new MySqlBatch();

            new MySqlLookupSubjectBatchOps(options).AddToBatch(batch, query, withToken ? Token : null);

            batch.BatchCommands.Should().ContainSingle();
            var command = batch.BatchCommands[0];
            ShouldBindExactlyWhatItReads(command.CommandText, command.Parameters, batchResolved: !withToken,
                $"{kind} on {options.ServerType}");
        }
    }

    private static IEnumerable<int> LevelCounts(string[] statements) =>
        Enumerable.Range(0, statements.Length).Where(i => statements[i] is not null);

    [Fact]
    public void A_misspelt_parameter_fails_the_check()
    {
        using var command = new MySqlCommand();
        command.Parameters.Add(new MySqlParameter("@entity_typ", "doc"));

        var act = () => ShouldBindExactlyWhatItReads("SELECT 1 WHERE entity_type = @entity_type",
            command.Parameters, batchResolved: false, "a misspelt name");

        act.Should().Throw<Exception>();
    }

    private static void ShouldBindExactlyWhatItReads(string sql, DbParameterCollection parameters, bool batchResolved,
        string because)
    {
        sql.Should().NotBeNullOrWhiteSpace(because);
        var bound = parameters.Cast<DbParameter>().Select(p => Bare(p.ParameterName)).ToList();
        var read = ParameterPattern().Matches(sql)
            .Select(m => Bare(m.Value))
            .Where(n => n != BatchResolvedName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bound.Should().OnlyHaveUniqueItems(because);
        bound.Should().BeEquivalentTo(read, because);
        if (batchResolved)
            sql.Should().Contain(MySqlQueries.BatchResolvedSnapToken, because)
                .And.NotContain(MySqlQueries.SnapTokenParameter, because);
    }

    // MySqlConnector matches a parameter bound with or without the @ prefix.
    private static string Bare(string name) => name.StartsWith('@') ? name[1..] : name;

    [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex ParameterPattern();
}
