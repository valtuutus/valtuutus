using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// Runs the queries of one LookupEntity step. A step's queries never depend on each other, so a
/// runner may run them concurrently or together in one round trip.
/// </summary>
public interface ILookupEntityQueryRunner
{
    /// <summary>
    /// Runs <paramref name="queries"/>, storing each answer in <see cref="LookupEntityQuery.Result"/>,
    /// and returns the snap token they ran against. A null <paramref name="snapToken"/> stands for the
    /// latest one, which the runner resolves; the engine passes the returned token to every later
    /// step of the request, so a whole lookup reads one snapshot. A runner reads each query's
    /// <see cref="LookupEntityQuery.Ids"/> before the returned task completes; the engine reuses
    /// those collections afterwards.
    /// </summary>
    ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupEntityQuery> queries, SnapToken? snapToken,
        CancellationToken cancellationToken);
}

/// <summary>Runs each query as its own <see cref="IDataReaderProvider"/> call, a step's calls concurrently.</summary>
public sealed class DefaultLookupEntityQueryRunner(IDataReaderProvider reader) : ILookupEntityQueryRunner
{
    public async ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupEntityQuery> queries, SnapToken? snapToken,
        CancellationToken cancellationToken)
    {
        var token = await SnapTokenUtils.ResolveLatest(reader, snapToken, cancellationToken);
        if (queries.Count == 1)
        {
            await RunOneAsync(queries[0], token, cancellationToken);
            return token;
        }

        // Only the queries still running are awaited together; a reader that answers synchronously
        // leaves nothing to wait on.
        Task[]? running = null;
        var count = 0;
        for (var i = 0; i < queries.Count; i++)
        {
            var task = RunOneAsync(queries[i], token, cancellationToken);
            if (!task.IsCompletedSuccessfully)
                (running ??= new Task[queries.Count - i])[count++] = task;
        }
        if (running is not null)
            await Task.WhenAll(count == running.Length ? running : running[..count]);
        return token;
    }

    private async Task RunOneAsync(LookupEntityQuery query, SnapToken token, CancellationToken ct)
    {
        query.Result = query.Kind switch
        {
            LookupEntityQueryKind.Relations => (await reader.GetRelationsWithSubjectsIds(
                new EntityRelationFilter { EntityType = query.EntityType, Relation = query.Relation, SnapToken = token },
                AsArray(query.Ids), query.SubjectType, query.SubjectRelation, query.Scope, ct)).Transfer(),
            LookupEntityQueryKind.RelationsMulti => (await reader.GetRelationsWithSubjectsIdsMultiRelation(
                query.EntityType, query.Names, AsArray(query.Ids), query.SubjectType, query.SubjectRelation, token,
                query.Scope, ct)).Transfer(),
            LookupEntityQueryKind.Joined => (await reader.GetRelationsJoined(
                new EntityRelationFilter { EntityType = query.EntityType, Relation = query.Relation, SnapToken = token },
                query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType, query.Ids.First(),
                query.Scope, ct)).Transfer(),
            LookupEntityQueryKind.Attributes => await reader.GetAttributes(
                new EntityAttributesFilter { EntityType = query.EntityType, Attributes = query.Names, SnapToken = token },
                query.Scope, ct),
            LookupEntityQueryKind.AttributesWithIds => await reader.GetAttributesWithEntityIds(
                new EntityAttributesFilter { EntityType = query.EntityType, Attributes = query.Names, SnapToken = token },
                query.Ids, ct),
            LookupEntityQueryKind.Attribute => await reader.GetAttributes(
                new EntityAttributeFilter { EntityType = query.EntityType, Attribute = query.Names[0], SnapToken = token },
                ct),
            LookupEntityQueryKind.EntitiesExcluding => await reader.GetEntityIdsExcluding(
                query.EntityType, query.Ids, token, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null),
        };
    }

    private static string[] AsArray(IReadOnlyCollection<string> ids) => ids as string[] ?? ids.ToArray();
}
