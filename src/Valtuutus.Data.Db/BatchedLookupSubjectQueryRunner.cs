using System.Data.Common;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Core.Pools;

namespace Valtuutus.Data.Db;

/// <summary>
/// Runs a lookup step's queries as one <see cref="DbBatch"/>. A request without a snap token
/// resolves it as the first command of its first batch. Without both batch surfaces from the
/// provider, every query runs as its own reader call.
/// </summary>
internal sealed class BatchedLookupSubjectQueryRunner(
    IDataReaderProvider reader,
    IRelationalBatchOps? batchOps,
    ILookupSubjectBatchOps? lookupBatchOps) : ILookupSubjectQueryRunner
{
    private readonly DefaultLookupSubjectQueryRunner _individual = new(reader);

    public async ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupSubjectQuery> queries, SnapToken? snapToken,
        CancellationToken cancellationToken)
    {
        if (batchOps is null || lookupBatchOps is null)
            return await _individual.RunAsync(queries, snapToken, cancellationToken);

        // Even a one-query step: the token and the answer in one round trip beat a dedicated token
        // round trip followed by the query.
        if (snapToken is null && batchOps.UsingBatchResolvedSnapToken is not null)
            return await RunBatchAsync(batchOps, lookupBatchOps, queries, null, cancellationToken);

        // One command is one round trip either way, and a plain command costs less to build.
        // UsersetLevels has no reader method, so it always runs as a batch command.
        if (queries.Count == 1 && queries[0].Kind != LookupSubjectQueryKind.UsersetLevels)
            return await _individual.RunAsync(queries, snapToken, cancellationToken);

        var token = snapToken ?? await reader.GetLatestSnapToken(cancellationToken) ?? SnapToken.MinValue;
        return await RunBatchAsync(batchOps, lookupBatchOps, queries, token, cancellationToken);
    }

    // A null snapToken resolves the latest one in the batch's first command.
    private static async ValueTask<SnapToken> RunBatchAsync(IRelationalBatchOps batchOps,
        ILookupSubjectBatchOps lookupBatchOps, IReadOnlyList<LookupSubjectQuery> queries, SnapToken? snapToken,
        CancellationToken ct)
    {
        using var span = ValtuutusTracing.Enabled
            ? ValtuutusTracing.StartRoundTrip(batchOps.DbSystemName, QueryNames(queries), snapToken is null)
            : default;
        await using var batch = batchOps.CreateBatch();
        if (snapToken is null)
            batchOps.AddResolveLatestSnapTokenToBatch(batch);
        foreach (var query in queries)
            lookupBatchOps.AddToBatch(batch, query, snapToken);

        await using var results = await batchOps.ExecuteBatchAsync(batch, ct).ConfigureAwait(false);
        span.Responded();
        var token = snapToken ?? await ReadSnapTokenAsync(results, ct).ConfigureAwait(false);
        for (var i = 0; i < queries.Count; i++)
        {
            if (i > 0 || snapToken is null)
                await results.NextResultAsync(ct).ConfigureAwait(false);
            queries[i].Result = await ReadResultAsync(results, queries[i].Kind, ct).ConfigureAwait(false);
        }
        return token;
    }

    private static string[] QueryNames(IReadOnlyList<LookupSubjectQuery> queries)
    {
        var names = new string[queries.Count];
        for (var i = 0; i < names.Length; i++)
            names[i] = queries[i].Kind.ToString();
        return names;
    }

    private static async Task<SnapToken> ReadSnapTokenAsync(DbDataReader results, CancellationToken ct) =>
        await results.ReadAsync(ct).ConfigureAwait(false) && !results.IsDBNull(0)
            ? new SnapToken(results.GetString(0))
            : SnapToken.MinValue;

    private static async Task<object> ReadResultAsync(DbDataReader results, LookupSubjectQueryKind kind,
        CancellationToken ct)
    {
        switch (kind)
        {
            case LookupSubjectQueryKind.Relations:
            case LookupSubjectQueryKind.RelationsMulti:
            case LookupSubjectQueryKind.Joined:
            case LookupSubjectQueryKind.UsersetLevels:
            {
                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    while (await results.ReadAsync(ct).ConfigureAwait(false))
                    {
                        pooled.Add(new RelationTuple(
                            results.GetString(0), results.GetString(1), results.GetString(2),
                            results.GetString(3), results.GetString(4), results.GetString(5)));
                    }
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
                return pooled.Transfer();
            }
            case LookupSubjectQueryKind.Attributes:
            {
                var attributes = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
                while (await results.ReadAsync(ct).ConfigureAwait(false))
                {
                    var tuple = ReadAttributeTuple(results);
                    attributes[(tuple.Attribute, tuple.EntityId)] = tuple;
                }
                return attributes;
            }
            case LookupSubjectQueryKind.Attribute:
            {
                var attributes = new List<AttributeTuple>();
                while (await results.ReadAsync(ct).ConfigureAwait(false))
                    attributes.Add(ReadAttributeTuple(results));
                return attributes;
            }
            case LookupSubjectQueryKind.SubjectsExcluding:
            {
                var subjectIds = new List<string>();
                while (await results.ReadAsync(ct).ConfigureAwait(false))
                    subjectIds.Add(results.GetString(0));
                return subjectIds;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static AttributeTuple ReadAttributeTuple(DbDataReader results) =>
        new(results.GetString(0), results.GetString(1), results.GetString(2),
            JsonNode.Parse(results.GetString(3)) as JsonValue
            ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue."));
}
