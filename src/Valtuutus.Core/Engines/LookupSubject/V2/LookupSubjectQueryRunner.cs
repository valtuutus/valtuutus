using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Runs the queries of one lookup step. A step's queries never depend on each other, so a runner
/// may run them concurrently or together in one round trip.
/// </summary>
public interface ILookupSubjectQueryRunner
{
    /// <summary>
    /// Runs <paramref name="queries"/>, storing each answer in <see cref="LookupSubjectQuery.Result"/>,
    /// and returns the snap token they ran against. A null <paramref name="snapToken"/> stands for the
    /// latest one, which the runner resolves; the engine passes the returned token to every later
    /// step of the request, so a whole lookup reads one snapshot.
    /// </summary>
    ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupSubjectQuery> queries, SnapToken? snapToken,
        CancellationToken cancellationToken);
}

/// <summary>Runs each query as its own <see cref="IDataReaderProvider"/> call, a step's calls concurrently.</summary>
public sealed class DefaultLookupSubjectQueryRunner(IDataReaderProvider reader) : ILookupSubjectQueryRunner
{
    public async ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupSubjectQuery> queries, SnapToken? snapToken,
        CancellationToken cancellationToken)
    {
        var token = await SnapTokenUtils.ResolveLatest(reader, snapToken, cancellationToken);
        if (queries.Count == 1)
        {
            await RunOneAsync(queries[0], token, cancellationToken);
            return token;
        }

        var running = new Task[queries.Count];
        for (var i = 0; i < queries.Count; i++)
            running[i] = RunOneAsync(queries[i], token, cancellationToken);
        await Task.WhenAll(running);
        return token;
    }

    private async Task RunOneAsync(LookupSubjectQuery query, SnapToken token, CancellationToken ct)
    {
        query.Result = query.Kind switch
        {
            LookupSubjectQueryKind.Relations => (await reader.GetRelationsWithEntityIds(
                new EntityRelationFilter { EntityType = query.EntityType, Relation = query.Relation, SnapToken = token },
                query.SubjectType, query.Ids, query.SubjectRelation, ct)).Transfer(),
            LookupSubjectQueryKind.RelationsMulti => (await reader.GetRelationsWithEntityIdsMultiRelation(
                query.EntityType, query.Names, query.SubjectType, query.Ids, query.SubjectRelation, token, ct)).Transfer(),
            LookupSubjectQueryKind.Joined => (await reader.GetRelationsJoinedByEntityIds(
                new EntityRelationFilter { EntityType = query.EntityType, Relation = query.Relation, SnapToken = token },
                query.Ids, query.SubEntityType, query.SubjectRelation!, ct)).Transfer(),
            LookupSubjectQueryKind.Attributes => await reader.GetAttributesWithEntityIds(
                new EntityAttributesFilter { EntityType = query.EntityType, Attributes = query.Names, SnapToken = token },
                query.Ids, ct),
            LookupSubjectQueryKind.Attribute => await reader.GetAttributesWithEntityIds(
                new AttributeFilter { EntityType = query.EntityType, Attribute = query.Names[0], SnapToken = token },
                query.Ids, ct),
            LookupSubjectQueryKind.SubjectsExcluding => await reader.GetSubjectIdsExcluding(
                query.SubjectType, query.Ids, token, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null),
        };
    }
}
