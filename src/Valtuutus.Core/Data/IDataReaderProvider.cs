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
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of RelationTuples with specified entity IDs.
    /// </summary>
    /// <param name="entityRelationFilter">The filter criteria for retrieving RelationTuples.</param>
    /// <param name="subjectType">The type of the subject.</param>
    /// <param name="entityIds">The IDs of the entities.</param>
    /// <param name="subjectRelation">The relation of the subject.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of RelationTuples matching the filter criteria and entity IDs.</returns>
    Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a list of RelationTuples with specified subject IDs.
    /// </summary>
    /// <param name="entityFilter">The filter criteria for retrieving RelationTuples.</param>
    /// <param name="subjectsIds">The IDs of the subjects.</param>
    /// <param name="subjectType">The type of the subject.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of RelationTuples matching the filter criteria and subject IDs.</returns>
    Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,  IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken);

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
    /// Retrieves a list of AttributeTuples with specified entity IDs.
    /// </summary>
    /// <param name="filter">The filter criteria for retrieving AttributeTuples.</param>
    /// <param name="entitiesIds">The IDs of the entities.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of AttributeTuples matching the filter criteria and entity IDs.</returns>
    Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken);
}