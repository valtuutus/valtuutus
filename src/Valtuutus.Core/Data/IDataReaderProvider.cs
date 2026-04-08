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
    /// <param name="subjectRelation">The relation of the subject.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of RelationTuples matching the filter criteria and entity IDs.</returns>
    Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of RelationTuples with specified subject IDs.
    /// When <paramref name="scope"/> is provided, only entities that also have the scope relation to the scope subject are returned.
    /// </summary>
    /// <param name="entityFilter">The filter criteria for retrieving RelationTuples.</param>
    /// <param name="subjectsIds">The IDs of the subjects.</param>
    /// <param name="subjectType">The type of the subject.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="scope">Optional entity scope to filter results.</param>
    /// <returns>A list of RelationTuples matching the filter criteria and subject IDs.</returns>
    Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, CancellationToken cancellationToken, EntityScope? scope = null);

    /// <summary>
    /// Two-hop join: finds main-entity tuples whose subject ID is itself a subject of the
    /// dependent relation. When <paramref name="scope"/> is provided, only main entities
    /// that also have the scope relation to the scope subject are returned.
    /// </summary>
    Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter,
        string subEntityType,
        string subRelation,
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken,
        EntityScope? scope = null);

    /// <summary>
    /// Retrieves a single AttributeTuple based on the provided filter.
    /// </summary>
    /// <param name="filter">The filter criteria for retrieving the AttributeTuple.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The AttributeTuple matching the filter criteria, or null if not found.</returns>
    Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken);

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
    Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, CancellationToken cancellationToken, EntityScope? scope = null);

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
}

