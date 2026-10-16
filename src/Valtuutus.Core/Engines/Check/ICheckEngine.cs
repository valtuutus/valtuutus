namespace Valtuutus.Core.Engines.Check;

public interface ICheckEngine
{
    /// <summary>
    /// The Check function walks through the schema graph to answer the question: "Can entity U perform action Y in resource Z?".
    /// </summary>
    /// <param name="req">Object containing the required information to evaluate the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the subject has the permission on the entity, and the snap token it was read at.</returns>
    ValueTask<CheckResult> Check(CheckRequest req, CancellationToken cancellationToken);

    /// <summary>
    /// The SubjectPermission function walks through the schema graph and evaluates every condition required to check, for each permission,
    /// if the provided subject with `SubjectId` and `SubjectType` on the entity with `EntityId` and `EntityType`.
    /// </summary>
    /// <param name="req">Object containing the required information to evaluate the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every permission for the entity and whether the subject has it, and the snap token they were read at.</returns>
    ValueTask<SubjectPermissionResult> SubjectPermission(SubjectPermissionRequest req, CancellationToken cancellationToken);

    /// <summary>
    /// Walks the schema graph like Check(), but returns a full resolution tree explaining
    /// which relations, attributes, and expressions determined the result.
    /// </summary>
    /// <param name="req">Same request as Check().</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The boolean result plus a tree of every evaluated node.</returns>
    ValueTask<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken);
}