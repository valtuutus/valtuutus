using Valtuutus.Core.Pools;
using Valtuutus.Core.Engines.LookupEntity;

namespace Valtuutus.Core.Data;

public interface IDataReaderProvider
{
    /// <summary>
    /// Retrieves the latest SnapToken.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The latest SnapToken, or null if not found.</returns>
    Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of RelationTuples based on the provided filter.
    /// </summary>
    /// <param name="tupleFilter">The filter criteria for retrieving RelationTuples.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of RelationTuples matching the filter criteria.</returns>
    Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a direct (subject_relation IS NULL) tuple exists for the given subject ID.
    /// Avoids materialising the full relation list when only an existence check is needed.
    /// </summary>
    Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken);

    /// <summary>
    /// Batch variant of <see cref="HasDirectRelation"/>: returns true if ANY of the given
    /// entity IDs has a direct relation to <paramref name="subjectId"/>. Collapses N individual
    /// HasDirectRelation queries into a single DB round-trip.
    /// </summary>
    Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether the subject has access through a tuple-to-user-set relation:
    /// returns true if there exists an intermediate entity X such that
    /// (entityType, entityId) has [tupleSetRelation] to X, and X has [computedRelation] to (subjectType, subjectId).
    /// </summary>
    Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId,
        string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId,
        SnapToken snapToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves only indirect tuples (subject_relation IS NOT NULL) for the given filter.
    /// Used after <see cref="HasDirectRelation"/> returns false to avoid fetching direct
    /// tuples that are irrelevant to recursive resolution.
    /// </summary>
    Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of RelationTuples with specified entity IDs.
    /// </summary>
    /// <param name="entityRelationFilter">The filter criteria for retrieving RelationTuples.</param>
    /// <param name="subjectType">The type of the subject.</param>
    /// <param name="entityIds">The IDs of the entities.</param>
    /// <param name="subjectRelation">The relation the subjects carry: empty for a direct subject, otherwise the userset relation. Matched exactly.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of RelationTuples matching the filter criteria and entity IDs.</returns>
    Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string subjectRelation, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of RelationTuples with specified subject IDs.
    /// When <paramref name="scope"/> is provided, only entities that also have the scope relation to the scope subject are returned;
    /// the scope subject is always a direct subject.
    /// </summary>
    /// <param name="entityFilter">The filter criteria for retrieving RelationTuples.</param>
    /// <param name="subjectsIds">The IDs of the subjects.</param>
    /// <param name="subjectType">The type of the subject.</param>
    /// <param name="subjectRelation">The relation the subjects carry: empty for a direct subject, otherwise the userset relation. Matched exactly.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="scope">Optional entity scope to filter results.</param>
    /// <returns>A list of RelationTuples matching the filter criteria and subject IDs.</returns>
    Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds,
        string subjectType, string subjectRelation, EntityScope? scope, CancellationToken cancellationToken);

    /// <summary>
    /// Batch variant of <see cref="GetRelationsWithSubjectsIds"/> across sibling relation NAMES:
    /// widens the single <c>relation = @relation</c> filter to
    /// <c>relation IN (relationNames)</c>. Each returned <see cref="RelationTuple"/> still carries
    /// its own <see cref="RelationTuple.Relation"/> — callers fold the batched result by grouping
    /// rows by that column, no server-side aggregation is implied. Used to collapse N sibling
    /// direct-relation Union/Intersect children in LookupEntityEngine into one DB round trip.
    /// When <paramref name="scope"/> is provided, only entities that also have the scope relation
    /// to the scope subject are returned (same semantics as <see cref="GetRelationsWithSubjectsIds"/>).
    /// <paramref name="subjectRelation"/> follows the same contract as in <see cref="GetRelationsWithSubjectsIds"/>.
    /// </summary>
    Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(
        string entityType, string[] relationNames, string[] subjectsIds, string subjectType, string subjectRelation,
        SnapToken snapToken, EntityScope? scope, CancellationToken cancellationToken);

    /// <summary>
    /// Batch variant of <see cref="GetRelationsWithEntityIds"/> across sibling relation NAMES (not
    /// entity IDs): widens the single <c>relation = @relation</c> filter to
    /// <c>relation IN (relationNames)</c>. Each returned <see cref="RelationTuple"/> still carries
    /// its own <see cref="RelationTuple.Relation"/> — callers fold the batched result by grouping
    /// rows by that column, no server-side aggregation is implied. Used to collapse N sibling
    /// direct-relation Union/Intersect children in LookupSubjectEngine into one DB round trip, the
    /// LookupSubject analogue of <see cref="GetRelationsWithSubjectsIdsMultiRelation"/>. Unlike that
    /// method, there is no <c>EntityScope</c> variant — LookupSubjectEngine has no scope concept.
    /// <paramref name="subjectRelation"/> follows the same contract as in <see cref="GetRelationsWithEntityIds"/>.
    /// </summary>
    Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(
        string entityType, string[] relationNames, string subjectType, IEnumerable<string> entityIds,
        string subjectRelation, SnapToken snapToken, CancellationToken cancellationToken);

    /// <summary>
    /// Two-hop join: returns the <paramref name="mainFilter"/> tuples whose subject is
    /// <paramref name="subEntityType"/>:X#<paramref name="subjectRelation"/> for some X that has
    /// <paramref name="subRelation"/> to the direct subject <paramref name="subjectType"/>:<paramref name="subjectId"/>.
    /// When <paramref name="scope"/> is provided, only main entities that also have the scope
    /// relation to the direct scope subject are returned.
    /// </summary>
    Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter,
        string subEntityType,
        string subjectRelation,
        string subRelation,
        string subjectType,
        string subjectId,
        EntityScope? scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inverse of <see cref="GetRelationsJoined"/>: instead of filtering the main side down to
    /// rows whose subject is confirmed via a subquery against one fixed final subject, this
    /// filters the dependent (second-hop) side down to rows whose entity is confirmed via a
    /// subquery against a list of starting entity IDs. Used by LookupSubject, which starts from
    /// N entity IDs and has no single final subject to filter to — it's looking for all subjects
    /// reachable from those entities in one query instead of a dependent-query-then-recurse pair.
    /// The first hop reads the <paramref name="mainFilter"/> tuples of <paramref name="entityIds"/> whose
    /// subject is <paramref name="subEntityType"/>:X#<paramref name="subjectRelation"/>. The answer is
    /// the tuples of <paramref name="subRelation"/> on each such X whose subject is a direct
    /// <paramref name="subjectType"/>.
    /// </summary>
    Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(
        EntityRelationFilter mainFilter,
        IEnumerable<string> entityIds,
        string subEntityType,
        string subjectRelation,
        string subRelation,
        string subjectType,
        CancellationToken cancellationToken);

    /// <summary>
    /// Existence-only fast path for boolean attribute permission leaves: returns true when
    /// the entity has the given attribute visible at the snap token with a JSON value of
    /// exactly <c>true</c>. Missing attribute — or any non-<c>true</c> value, including
    /// malformed non-boolean data — returns false (it does not throw, unlike materialising
    /// the tuple and calling <c>GetValue&lt;bool&gt;()</c>).
    /// </summary>
    Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute,
        SnapToken snapToken, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of AttributeTuples based on the provided filter.
    /// </summary>
    /// <param name="filter">The filter criteria for retrieving AttributeTuples.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of AttributeTuples matching the filter criteria.</returns>
    Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a Dictionary of AttributeTuples based on the provided filter.
    /// When <paramref name="scope"/> is provided, only entities that have the scope relation to the scope subject are returned.
    /// The key of the dictionary is the attribute name and entityId,
    /// so that each entity instance will have only on attribute by name
    /// </summary>
    /// <param name="filter">The filter criteria for retrieving AttributeTuples.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="scope">Optional entity scope to filter results.</param>
    /// <returns>A Dictionary of AttributeTuples matching the filter criteria.</returns>
    Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of AttributeTuples with specified entity IDs.
    /// </summary>
    /// <param name="filter">The filter criteria for retrieving AttributeTuples.</param>
    /// <param name="entitiesIds">The IDs of the entities.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of AttributeTuples matching the filter criteria and entity IDs.</returns>
    Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a Dictionary of AttributeTuples based on the provided filter.
    /// The key of the dictionary is the attribute name and entityId,
    /// so that each entity instance will have only on attribute by name
    /// </summary>
    /// <param name="filter">The filter criteria for retrieving AttributeTuples.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A Dictionary of AttributeTuples matching the filter criteria.</returns>
    Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves attributes for a single entity. Optimized path for Check engine — avoids dictionary allocation.
    /// <paramref name="filter"/>.<see cref="EntityAttributesFilter.EntityId"/> must be set.
    /// </summary>
    Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all distinct entity IDs of the given type that are NOT in <paramref name="excludeIds"/>.
    /// Pass an empty collection to retrieve all entity IDs (no exclusion filter).
    /// Implements DB-side set complement for negation support in LookupEntity.
    /// </summary>
    Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all distinct subject IDs of the given subject type (direct tuples only, subject_relation IS NULL)
    /// that are NOT in <paramref name="excludeIds"/>.
    /// Implements DB-side set complement for negation support in LookupSubject.
    /// </summary>
    Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken);
}

