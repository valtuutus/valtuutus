using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataReaderProvider : RateLimiterExecuter, IDataReaderProvider
{
    private const string UnformattedSelectAttributes = @"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM {0}.{1} /**where**/";

    private const string UnformattedSelectRelations = @"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id,
                    subject_relation
                FROM {0}.{1} /**where**/";

    private const string UnformattedExistsRelation = "SELECT EXISTS(SELECT 1 FROM {0}.{1} /**where**/)";
    private const string SnapTokenPredicate = "created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)";

    private readonly DbConnectionFactory _connectionFactory;
    private readonly NpgsqlDataSource _hotPathDataSource;
    private static readonly ConcurrentDictionary<DataSourceCacheKey, NpgsqlDataSource> DataSourceCache = new();
    private static string? _formattedSelect1Attribute;
    private static string? _formattedGetLatestSnapTokenQuery;
    private static string? _formattedSelectAttributes;
    private static string? _formattedSelectRelations;
    private static string? _formattedExistsRelation;
    private static string? _hasDirectRelationSql;
    private static string? _hasAnyDirectRelationSql;
    private static string? _getIndirectRelationsSql;
    private static string? _getRelationsWithSingleSubjectSql;
    private static string? _getRelationsWithSingleSubjectSnapSql;
    private static string? _getRelationsWithMultiSubjectSql;
    private static string? _getRelationsWithMultiSubjectSnapSql;
    private static string? _getRelationsJoinedSql;
    private static string? _hasTupleToUserSetRelationSql;
    private static string? _getAttributeSql;
    private static string? _getAttributeWithEntityIdSql;
    private static string? _getRelationsWithSingleSubjectScopedSql;
    private static string? _getRelationsWithMultiSubjectScopedSql;
    private static string? _getRelationsJoinedScopedSql;
    private static string? _getAttributesDictScopedSql;
    private static readonly object Lock = new();
    private readonly record struct DataSourceCacheKey(string ConnectionString, int MaxAutoPrepare, int AutoPrepareMinUsages);

    private static List<T> MaterializeList<T>(IEnumerable<T> rows)
    {
        if (rows is List<T> list)
            return list;

        if (rows is ICollection<T> collection)
        {
            var materialized = new List<T>(collection.Count);
            foreach (var row in collection)
                materialized.Add(row);
            return materialized;
        }

        return rows.ToList();
    }

    private static Dictionary<(string AttributeName, string EntityId), AttributeTuple> MaterializeAttributeDictionary(
        IEnumerable<AttributeTuple> rows)
    {
        if (rows is ICollection<AttributeTuple> collection)
        {
            var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>(collection.Count);
            foreach (var row in collection)
                dict[(row.Attribute, row.EntityId)] = row;
            return dict;
        }

        var result = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
        foreach (var row in rows)
            result[(row.Attribute, row.EntityId)] = row;
        return result;
    }

    private static void AddStringParameter(NpgsqlCommand command, string name, string value, int size)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Varchar)
        {
            Size = size,
            TypedValue = value
        });
    }

    private static void AddFixedCharParameter(NpgsqlCommand command, string name, string value, int size)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Char)
        {
            Size = size,
            TypedValue = value
        });
    }

    private static JsonValue ParseJsonValue(string rawJson)
    {
        return JsonNode.Parse(rawJson) as JsonValue
            ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue.");
    }

    private static AttributeTuple ReadAttributeTuple(NpgsqlDataReader reader)
    {
        return new AttributeTuple(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseJsonValue(reader.GetString(3)));
    }

    private static NpgsqlDataSource GetOrCreateDataSource(string connectionString, ValtuutusPostgresOptions options)
    {
        var key = new DataSourceCacheKey(connectionString, options.MaxAutoPrepare, options.AutoPrepareMinUsages);
        return DataSourceCache.GetOrAdd(key, static cacheKey =>
        {
            var csb = new NpgsqlConnectionStringBuilder(cacheKey.ConnectionString);
            if (cacheKey.MaxAutoPrepare > 0)
            {
                csb.MaxAutoPrepare = cacheKey.MaxAutoPrepare;
                csb.AutoPrepareMinUsages = cacheKey.AutoPrepareMinUsages;
            }

            return NpgsqlDataSource.Create(csb.ConnectionString);
        });
    }


    public PostgresDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        ValtuutusPostgresOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        InitializeQueries(dbOptions);
        using var probeConnection = (NpgsqlConnection)_connectionFactory();
        _hotPathDataSource = GetOrCreateDataSource(probeConnection.ConnectionString, dbOptions);
    }

    private static void InitializeQueries(IValtuutusDbOptions dbOptions)
    {
        if (_formattedSelectAttributes == null || _formattedSelectRelations == null ||
            _formattedGetLatestSnapTokenQuery == null || _formattedSelect1Attribute == null)
        {
            lock (Lock)
            {
                if (_formattedSelectAttributes == null)
                {
                    _formattedSelectAttributes ??= string.Format(UnformattedSelectAttributes, dbOptions.Schema,
                        dbOptions.AttributesTableName);
                    _formattedSelectRelations ??= string.Format(UnformattedSelectRelations, dbOptions.Schema,
                        dbOptions.RelationsTableName);
                    _formattedGetLatestSnapTokenQuery ??= string.Format(
                        "SELECT id FROM {0}.{1} ORDER BY created_at DESC LIMIT 1",
                        dbOptions.Schema, dbOptions.TransactionsTableName);
                    _formattedSelect1Attribute ??= $"{_formattedSelectAttributes!} LIMIT 1";
                    _formattedExistsRelation ??= string.Format(UnformattedExistsRelation, dbOptions.Schema,
                        dbOptions.RelationsTableName);

                    var relationsTable = $"{dbOptions.Schema}.{dbOptions.RelationsTableName}";
                    var attributesTable = $"{dbOptions.Schema}.{dbOptions.AttributesTableName}";
                    _hasDirectRelationSql =
                        $"SELECT EXISTS(SELECT 1 FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation AND subject_id = @subject_id AND subject_relation = '')";
                    _hasAnyDirectRelationSql =
                        $"SELECT EXISTS(SELECT 1 FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = ANY(@entity_ids) AND relation = @relation AND subject_id = @subject_id AND subject_relation = '')";
                    _getIndirectRelationsSql =
                        $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation AND subject_relation <> ''";
                    _getRelationsWithSingleSubjectSql =
                        $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = @subject_id";
                    _getRelationsWithSingleSubjectSnapSql =
                        $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = @subject_id";
                    _getRelationsWithMultiSubjectSql =
                        $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = ANY(@subject_ids)";
                    _getRelationsWithMultiSubjectSnapSql =
                        $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = ANY(@subject_ids)";
                    _getRelationsJoinedSql = $"""
                        SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                        FROM {relationsTable} AS r_main
                        WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                          AND r_main.entity_type = @entity_type
                          AND r_main.relation = @relation
                          AND r_main.subject_type = @sub_entity_type
                          AND r_main.subject_id IN (
                              SELECT entity_id FROM {relationsTable}
                              WHERE created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)
                                AND entity_type = @sub_entity_type
                                AND relation = @sub_relation
                                AND subject_type = @subject_type
                                AND subject_id = @subject_id
                          )
                        """;
                    _hasTupleToUserSetRelationSql = $"""
                        SELECT EXISTS(
                            SELECT 1 FROM {relationsTable} r_main
                            INNER JOIN {relationsTable} r_dep
                                ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                            WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                              AND r_main.entity_type = @entity_type
                              AND r_main.entity_id = @entity_id
                              AND r_main.relation = @tuple_set_relation
                              AND r_main.subject_relation = ''
                              AND r_dep.created_tx_id <= @snap_token AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @snap_token)
                              AND r_dep.relation = @computed_relation
                              AND r_dep.subject_type = @subject_type
                              AND r_dep.subject_id = @subject_id
                              AND r_dep.subject_relation = ''
                        )
                        """;
                    _getAttributeSql =
                        $"SELECT entity_type, entity_id, attribute, value FROM {attributesTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND attribute = @attribute LIMIT 1";
                    _getAttributeWithEntityIdSql =
                        $"SELECT entity_type, entity_id, attribute, value FROM {attributesTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND attribute = @attribute AND entity_id = @entity_id LIMIT 1";
                    _getRelationsWithSingleSubjectScopedSql = $"""
                        SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                        FROM {relationsTable} r_main
                        INNER JOIN {relationsTable} r_scope
                            ON r_scope.entity_type = r_main.entity_type
                            AND r_scope.entity_id = r_main.entity_id
                            AND r_scope.relation = @scope_relation
                            AND r_scope.subject_type = @scope_subject_type
                            AND r_scope.subject_id = @scope_subject_id
                            AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                        WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                          AND r_main.entity_type = @entity_type
                          AND r_main.relation = @relation
                          AND r_main.subject_type = @subject_type
                          AND r_main.subject_id = @subject_id
                        """;
                    _getRelationsWithMultiSubjectScopedSql = $"""
                        SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                        FROM {relationsTable} r_main
                        INNER JOIN {relationsTable} r_scope
                            ON r_scope.entity_type = r_main.entity_type
                            AND r_scope.entity_id = r_main.entity_id
                            AND r_scope.relation = @scope_relation
                            AND r_scope.subject_type = @scope_subject_type
                            AND r_scope.subject_id = @scope_subject_id
                            AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                        WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                          AND r_main.entity_type = @entity_type
                          AND r_main.relation = @relation
                          AND r_main.subject_type = @subject_type
                          AND r_main.subject_id = ANY(@subject_ids)
                        """;
                    _getRelationsJoinedScopedSql = $"""
                        SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                        FROM {relationsTable} AS r_main
                        INNER JOIN {relationsTable} r_scope
                            ON r_scope.entity_type = r_main.entity_type
                            AND r_scope.entity_id = r_main.entity_id
                            AND r_scope.relation = @scope_relation
                            AND r_scope.subject_type = @scope_subject_type
                            AND r_scope.subject_id = @scope_subject_id
                            AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                        WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                          AND r_main.entity_type = @entity_type
                          AND r_main.relation = @relation
                          AND r_main.subject_type = @sub_entity_type
                          AND r_main.subject_id IN (
                              SELECT entity_id FROM {relationsTable}
                              WHERE created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)
                                AND entity_type = @sub_entity_type
                                AND relation = @sub_relation
                                AND subject_type = @subject_type
                                AND subject_id = @subject_id
                          )
                        """;
                    _getAttributesDictScopedSql = $"""
                        SELECT a.entity_type, a.entity_id, a.attribute, a.value
                        FROM {attributesTable} a
                        INNER JOIN {relationsTable} r_scope
                            ON r_scope.entity_type = a.entity_type
                            AND r_scope.entity_id = a.entity_id
                            AND r_scope.relation = @scope_relation
                            AND r_scope.subject_type = @scope_subject_type
                            AND r_scope.subject_id = @scope_subject_id
                            AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                        WHERE a.created_tx_id <= @snap_token AND (a.deleted_tx_id IS NULL OR a.deleted_tx_id > @snap_token)
                          AND a.entity_type = @entity_type
                          AND a.attribute = ANY(@attributes)
                        """;
                }
            }
        }
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_formattedGetLatestSnapTokenQuery!);
            command.CommandText = _formattedGetLatestSnapTokenQuery!;
            var res = await command.ExecuteScalarAsync(cancellationToken) as string;
            return res is not null ? new SnapToken(res) : (SnapToken?)null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterRelations(tupleFilter)
                .AddTemplate(_formattedSelectRelations!);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_hasDirectRelationSql!);
            AddStringParameter(command, "entity_type", tupleFilter.EntityType, 256);
            AddStringParameter(command, "entity_id", tupleFilter.EntityId, 64);
            AddStringParameter(command, "relation", tupleFilter.Relation, 64);
            AddStringParameter(command, "subject_id", subjectId, 64);
            AddFixedCharParameter(command, "snap_token", tupleFilter.SnapToken.Value, 26);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_getIndirectRelationsSql!);
            AddStringParameter(command, "entity_type", tupleFilter.EntityType, 256);
            AddStringParameter(command, "entity_id", tupleFilter.EntityId, 64);
            AddStringParameter(command, "relation", tupleFilter.Relation, 64);
            AddFixedCharParameter(command, "snap_token", tupleFilter.SnapToken.Value, 26);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pooled.Add(new RelationTuple(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5)));
                }
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_hasAnyDirectRelationSql!);
            AddStringParameter(command, "entity_type", entityType, 256);
            command.Parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                TypedValue = entityIds
            });
            AddStringParameter(command, "relation", relation, 64);
            AddStringParameter(command, "subject_id", subjectId, 64);
            AddFixedCharParameter(command, "snap_token", snapToken.Value, 26);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterRelations(entityRelationFilter, subjectType, entityIds, subjectRelation)
                .AddTemplate(_formattedSelectRelations!);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(queryTemplate.RawSql,
                        queryTemplate.Parameters, cancellationToken: cancellationToken)));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (scope is { } s)
            {
                var sql = subjectsIds.Length == 1
                    ? _getRelationsWithSingleSubjectScopedSql!
                    : _getRelationsWithMultiSubjectScopedSql!;
                await using var command = _hotPathDataSource.CreateCommand(sql);
                AddStringParameter(command, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command, "relation", entityFilter.Relation, 64);
                AddStringParameter(command, "subject_type", subjectType, 256);
                AddFixedCharParameter(command, "snap_token", entityFilter.SnapToken.Value, 26);
                AddStringParameter(command, "scope_relation", s.Relation, 64);
                AddStringParameter(command, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command, "scope_subject_id", s.SubjectId, 64);
                if (subjectsIds.Length == 1)
                    AddStringParameter(command, "subject_id", subjectsIds[0], 64);
                else
                    command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = subjectsIds });

                var scoped = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        scoped.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return scoped;
                }
                catch { scoped.Dispose(); throw; }
            }

            if (subjectsIds.Length == 1)
            {
                await using var command = _hotPathDataSource.CreateCommand(_getRelationsWithSingleSubjectSnapSql!);
                AddStringParameter(command, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command, "relation", entityFilter.Relation, 64);
                AddStringParameter(command, "subject_type", subjectType, 256);
                AddStringParameter(command, "subject_id", subjectsIds[0], 64);
                AddFixedCharParameter(command, "snap_token", entityFilter.SnapToken.Value, 26);

                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        pooled.Add(new RelationTuple(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5)));
                    }
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            {
                await using var command = _hotPathDataSource.CreateCommand(_getRelationsWithMultiSubjectSnapSql!);
                AddStringParameter(command, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command, "relation", entityFilter.Relation, 64);
                AddStringParameter(command, "subject_type", subjectType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
                {
                    TypedValue = subjectsIds
                });
                AddFixedCharParameter(command, "snap_token", entityFilter.SnapToken.Value, 26);

                var multi = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        multi.Add(new RelationTuple(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5)));
                    }
                    return multi;
                }
                catch
                {
                    multi.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var sql = scope.HasValue ? _getRelationsJoinedScopedSql! : _getRelationsJoinedSql!;
            await using var command = _hotPathDataSource.CreateCommand(sql);
            AddFixedCharParameter(command, "snap_token", mainFilter.SnapToken.Value, 26);
            AddStringParameter(command, "entity_type", mainFilter.EntityType, 256);
            AddStringParameter(command, "relation", mainFilter.Relation, 64);
            AddStringParameter(command, "sub_entity_type", subEntityType, 256);
            AddStringParameter(command, "sub_relation", subRelation, 64);
            AddStringParameter(command, "subject_type", subjectType, 256);
            AddStringParameter(command, "subject_id", subjectId, 64);
            if (scope is { } s)
            {
                AddStringParameter(command, "scope_relation", s.Relation, 64);
                AddStringParameter(command, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command, "scope_subject_id", s.SubjectId, 64);
            }

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pooled.Add(new RelationTuple(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                }
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_hasTupleToUserSetRelationSql!);
            AddFixedCharParameter(command, "snap_token", snapToken.Value, 26);
            AddStringParameter(command, "entity_type", entityType, 256);
            AddStringParameter(command, "entity_id", entityId, 64);
            AddStringParameter(command, "tuple_set_relation", tupleSetRelation, 64);
            AddStringParameter(command, "computed_relation", computedRelation, 64);
            AddStringParameter(command, "subject_type", subjectType, 256);
            AddStringParameter(command, "subject_id", subjectId, 64);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);
            await using var command = _hotPathDataSource.CreateCommand(hasEntityId ? _getAttributeWithEntityIdSql! : _getAttributeSql!);
            command.CommandText = hasEntityId ? _getAttributeWithEntityIdSql : _getAttributeSql;

            AddStringParameter(command, "entity_type", filter.EntityType, 256);
            AddStringParameter(command, "attribute", filter.Attribute, 64);
            if (hasEntityId)
                AddStringParameter(command, "entity_id", filter.EntityId!, 64);
            AddFixedCharParameter(command, "snap_token", filter.SnapToken.Value, 26);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadAttributeTuple(reader);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_formattedSelectAttributes!);

            var rows = await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken));
            return MaterializeList(rows);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (scope is { } s)
            {
                await using var command = _hotPathDataSource.CreateCommand(_getAttributesDictScopedSql!);
                AddStringParameter(command, "entity_type", filter.EntityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
                AddFixedCharParameter(command, "snap_token", filter.SnapToken.Value, 26);
                AddStringParameter(command, "scope_relation", s.Relation, 64);
                AddStringParameter(command, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command, "scope_subject_id", s.SubjectId, 64);

                var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var tuple = ReadAttributeTuple(reader);
                    dict[(tuple.Attribute, tuple.EntityId)] = tuple;
                }
                return dict;
            }

            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter)
                .AddTemplate(_formattedSelectAttributes!);

            var rows = await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken));
            return MaterializeAttributeDictionary(rows);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(_formattedSelectAttributes!);

            var rows = await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken));
            return MaterializeList(rows);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            using var connection = _connectionFactory();
            var queryTemplate = new SqlBuilder()
                .FilterAttributes(filter, entitiesIds)
                .AddTemplate(_formattedSelectAttributes!);

            var rows = await connection.QueryAsync<AttributeTuple>(new CommandDefinition(queryTemplate.RawSql,
                queryTemplate.Parameters, cancellationToken: cancellationToken));
            return MaterializeAttributeDictionary(rows);
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
