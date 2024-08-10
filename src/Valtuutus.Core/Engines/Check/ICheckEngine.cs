namespace Valtuutus.Core.Engines.Check;

public interface ICheckEngine
{
    /// <summary>
    /// The check function walks through the schema graph to answer the question: "Can entity U perform action Y in resource Z?".
    /// </summary>
    /// <param name="req">Object containing the required information to evaluate the check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the subject has the permission on the entity</returns>
    Task<bool> Check(CheckRequest req, CancellationToken cancellationToken);

    /// <summary>
    /// The SubjectPermission function walks through the schema graph and evaluates every condition required to check, for each permission
    /// if the provided subject with `SubjectId` and `SubjectType` on the entity with `EntityId` and `EntityType`.
    /// </summary>
    /// <param name="req">Object containing the required information to evaluate the check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A dictionary containing every permission for the entity and if the subject has access to it</returns>
    Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req, CancellationToken cancellationToken);
}