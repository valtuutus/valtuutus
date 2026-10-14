using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// The compiled shape of a lookup from one entity type's permission, relation or attribute to one
/// subject type: which queries to ask and how their answers combine. Holds no entity ids, depth or
/// request context, so every request with the same key walks the same plan. Built by Core;
/// <see cref="ILookupSubjectPlanRewriter"/> implementations read it and may substitute
/// <see cref="UsersetLevelsPlan"/>.
/// </summary>
public abstract class LookupPlan
{
    internal static readonly LookupPlan Empty = new EmptyPlan();

    private protected LookupPlan() { }

    private sealed class EmptyPlan : LookupPlan;
}

/// <summary>A plan leaf evaluated from one query's answer.</summary>
internal abstract class AnswerPlan : LookupPlan
{
    public abstract SubjectSet Evaluate(object result, LookupSubjectRequest request);
}

/// <summary>A leaf that asks its own query, kept as a <see cref="Template"/> without ids.</summary>
internal abstract class QueryPlan(LookupSubjectQuery template) : AnswerPlan
{
    public LookupSubjectQuery Template { get; } = template;
}

/// <summary>
/// Subjects listed by tuples. A hop reaches into another entity type and spends a level of depth,
/// so with no depth left it asks nothing.
/// </summary>
internal sealed class SubjectsPlan(LookupSubjectQuery template, bool isHop) : QueryPlan(template)
{
    public bool IsHop { get; } = isHop;

    public override SubjectSet Evaluate(object result, LookupSubjectRequest request) =>
        SubjectSet.FromTuples((List<RelationTuple>)result);
}

/// <summary>One relation's subjects, read from the multi-relation query its parent set shares.</summary>
internal sealed class SlicePlan(string relation) : AnswerPlan
{
    public override SubjectSet Evaluate(object result, LookupSubjectRequest request) =>
        SubjectSet.FromTuples((List<RelationTuple>)result, relation);
}

/// <summary>A function over the entities' attributes; a gate that matches when any entity passes.</summary>
internal sealed class ExpressionPlan(LookupSubjectQuery template, string entityType, PermissionNodeLeafExp expression,
    Function function, Schema schema) : QueryPlan(template)
{
    public PermissionNodeLeafExp Expression { get; } = expression;

    public override SubjectSet Evaluate(object result, LookupSubjectRequest request)
    {
        var attributes = (Dictionary<(string AttributeName, string EntityId), AttributeTuple>)result;
        foreach (var attribute in attributes.Values)
        {
            using var arguments = function.BuildArgs(Expression.Args,
                static (arg, paramType, state) =>
                    state.attributes.TryGetValue((arg.AttributeName, state.entityId), out var value)
                        ? value.GetValue(state.schema.GetAttribute(state.entityType, arg.AttributeName).Type)
                        : new LiteralValueUnion { LiteralType = paramType },
                (attributes, entityId: attribute.EntityId, entityType, schema),
                request.Context);
            if (function.Lambda(arguments.Span)) return SubjectSet.Gate(true);
        }
        return SubjectSet.Gate(false);
    }
}

/// <summary>A boolean attribute; a gate that matches when any entity has it set.</summary>
internal sealed class AttributePlan(LookupSubjectQuery template) : QueryPlan(template)
{
    public override SubjectSet Evaluate(object result, LookupSubjectRequest request) =>
        SubjectSet.Gate(((List<AttributeTuple>)result).Exists(a => a.Value.TryGetValue<bool>(out var value) && value));
}

/// <summary>
/// A dependent hop: <see cref="Template"/> finds the intermediate subjects, and the lookup continues
/// from them through <see cref="Name"/> on <see cref="EntityType"/>, spending a level of depth.
/// </summary>
public sealed class ExpandPlan : LookupPlan
{
    internal ExpandPlan(LookupSubjectQuery template, string entityType, string name)
    {
        Template = template;
        EntityType = entityType;
        Name = name;
    }

    public LookupSubjectQuery Template { get; }
    public string EntityType { get; }
    public string Name { get; }
}

/// <summary>Another permission, relation or attribute of the same entities, spending a level of depth.</summary>
internal sealed class RefPlan(string entityType, string name) : LookupPlan
{
    public string EntityType { get; } = entityType;
    public string Name { get; } = name;
}

/// <summary>
/// A union or intersection. <see cref="Shared"/> is the multi-relation query that the
/// sibling-relation children read their relation from.
/// </summary>
public sealed class SetPlan : LookupPlan
{
    internal SetPlan(LookupPlan[] children, bool union, LookupSubjectQuery? shared)
    {
        Children = children;
        Union = union;
        Shared = shared;
    }

    public IReadOnlyList<LookupPlan> Children { get; }
    public bool Union { get; }
    public LookupSubjectQuery? Shared { get; }
}

/// <summary>Every subject of the request's subject type the child does not grant.</summary>
internal sealed class NegatePlan(LookupPlan child) : LookupPlan
{
    public LookupPlan Child { get; } = child;
}

/// <summary>
/// A direct relation whose userset subjects are followed for up to <see cref="Levels"/> levels in
/// one query, each level spending a level of depth; usersets on the last level read continue as
/// their own lookups. A data provider's <see cref="ILookupSubjectPlanRewriter"/> installs it in
/// place of <see cref="OneLevel"/>, the relation's compiled plan, which still runs when the request
/// has depth left for one level only.
/// </summary>
public sealed class UsersetLevelsPlan : LookupPlan
{
    /// <param name="template">A <see cref="LookupSubjectQueryKind.UsersetLevels"/> query with
    /// <see cref="LookupSubjectQuery.EntityType"/>, <see cref="LookupSubjectQuery.Relation"/> and
    /// <see cref="LookupSubjectQuery.SubjectType"/> set and no ids.</param>
    /// <param name="levels">Most levels one query follows; at least 2.</param>
    /// <param name="oneLevel">The plan being replaced.</param>
    public UsersetLevelsPlan(LookupSubjectQuery template, int levels, LookupPlan oneLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        if (template.Kind != LookupSubjectQueryKind.UsersetLevels)
            throw new ArgumentException($"The template must be a {nameof(LookupSubjectQueryKind.UsersetLevels)} query.", nameof(template));
        Template = template;
        Levels = levels;
        OneLevel = oneLevel;
    }

    public LookupSubjectQuery Template { get; }
    public int Levels { get; }
    public LookupPlan OneLevel { get; }
}
