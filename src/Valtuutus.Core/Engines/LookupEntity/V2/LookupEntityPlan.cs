using System.Runtime.InteropServices;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// The compiled shape of a LookupEntity from one entity type's permission, relation or attribute to
/// one subject type: which queries to ask and how their answers combine. Holds no subject id, depth,
/// scope or request context, so every request with the same key walks the same plan. Built by Core;
/// <see cref="ILookupEntityPlanRewriter"/> implementations read it and may substitute parts of it.
/// </summary>
public abstract class LookupEntityPlan
{
    internal static readonly LookupEntityPlan Empty = new EmptyPlan();

    private protected LookupEntityPlan() { }

    private sealed class EmptyPlan : LookupEntityPlan;
}

/// <summary>A plan leaf evaluated from one query's answer into the ids of the entities it grants.</summary>
internal abstract class AnswerPlan : LookupEntityPlan
{
    public abstract HashSet<string> Evaluate(object result, LookupEntityRequest request);

    public static HashSet<string> EntityIds(List<RelationTuple> tuples, string? relation = null)
    {
        var ids = EntitySets.Rent();
        if (relation is null) ids.EnsureCapacity(tuples.Count);
        foreach (ref readonly var tuple in CollectionsMarshal.AsSpan(tuples))
        {
            if (relation is null || tuple.Relation == relation)
                ids.Add(tuple.EntityId);
        }
        return ids;
    }
}

/// <summary>
/// Entities whose tuples name the request's subject, through a direct relation or a joined two-hop
/// read. A joined read reaches into another entity type and needs a level of depth, so with no
/// depth left it asks nothing.
/// </summary>
internal sealed class SubjectBoundPlan(LookupEntityQuery template, bool isHop) : AnswerPlan
{
    public LookupEntityQuery Template { get; } = template;
    public bool IsHop { get; } = isHop;

    public override HashSet<string> Evaluate(object result, LookupEntityRequest request) =>
        EntityIds((List<RelationTuple>)result);
}

/// <summary>One relation's entities, read from the multi-relation query its parent shares.</summary>
internal sealed class SlicePlan(string relation) : AnswerPlan
{
    public string Relation { get; } = relation;

    public override HashSet<string> Evaluate(object result, LookupEntityRequest request) =>
        EntityIds((List<RelationTuple>)result, Relation);
}

/// <summary>A function over every entity's attributes; grants the entities it matches.</summary>
internal sealed class ExpressionPlan(LookupEntityQuery template, PermissionNodeLeafExp expression,
    Function function, Schema schema) : AnswerPlan
{
    public LookupEntityQuery Template { get; } = template;
    public PermissionNodeLeafExp Expression { get; } = expression;

    public override HashSet<string> Evaluate(object result, LookupEntityRequest request)
    {
        var attributes = (Dictionary<(string AttributeName, string EntityId), AttributeTuple>)result;
        var matches = EntitySets.Rent();
        foreach (var attribute in attributes.Values)
        {
            using var arguments = function.BuildArgs(Expression.Args,
                static (arg, paramType, state) =>
                    state.attributes.TryGetValue((arg.AttributeName, state.entityId), out var value)
                        ? value.GetValue(state.schema.GetAttribute(state.entityType, arg.AttributeName).Type)
                        : new LiteralValueUnion { LiteralType = paramType },
                (attributes, entityId: attribute.EntityId, entityType: Template.EntityType, schema),
                request.Context);
            if (function.Lambda(arguments.Span)) matches.Add(attribute.EntityId);
        }
        return matches;
    }
}

/// <summary>A boolean attribute looked up directly; grants the entities where it is true.</summary>
internal sealed class AttributePlan(LookupEntityQuery template) : LookupEntityPlan
{
    public LookupEntityQuery Template { get; } = template;
}

/// <summary>
/// A dependent hop: the lookup continues on <see cref="Name"/> of <see cref="EntityType"/> without
/// scope, spending a level of depth, and <see cref="Main"/> then reads the tuples whose subjects are
/// the entities it found.
/// </summary>
internal sealed class ExpandPlan(LookupEntityQuery main, string entityType, string name) : LookupEntityPlan
{
    public LookupEntityQuery Main { get; } = main;
    public string EntityType { get; } = entityType;
    public string Name { get; } = name;
}

/// <summary>
/// A direct relation whose only non-terminal userset is itself, walked hop by hop until a hop finds
/// no new entity or the depth is spent. <see cref="Direct"/> and <see cref="Joined"/> are the
/// relation's other targets; <see cref="Hop"/> reads the relation's tuples whose subjects are
/// entities of the same type through the same relation. Always the root of its relation's plan.
/// </summary>
public sealed class FixpointPlan : LookupEntityPlan
{
    internal FixpointPlan(SubjectBoundPlan[] direct, SubjectBoundPlan[] joined, LookupEntityQuery hop)
    {
        Direct = direct;
        Joined = joined;
        Hop = hop;
    }

    internal SubjectBoundPlan[] Direct { get; }
    internal SubjectBoundPlan[] Joined { get; }
    internal LookupEntityQuery Hop { get; }
}

/// <summary>
/// A <see cref="FixpointPlan"/> whose hops a data provider answers several at a time: one
/// <see cref="LookupEntityQueryKind.RelationsLevels"/> query follows up to <see cref="Levels"/> hops
/// of the walk. Installed by a provider's <see cref="ILookupEntityPlanRewriter"/> in place of
/// <see cref="OneLevel"/>, whose hop still runs when the depth left allows one level only.
/// </summary>
public sealed class FixpointLevelsPlan : LookupEntityPlan
{
    /// <param name="template">A <see cref="LookupEntityQueryKind.RelationsLevels"/> query with
    /// <see cref="LookupEntityQuery.EntityType"/> and <see cref="LookupEntityQuery.Relation"/> set and no ids.</param>
    /// <param name="levels">Most hops one query follows; at least 2.</param>
    /// <param name="oneLevel">The plan being replaced.</param>
    public FixpointLevelsPlan(LookupEntityQuery template, int levels, FixpointPlan oneLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        if (template.Kind != LookupEntityQueryKind.RelationsLevels)
            throw new ArgumentException($"The template must be a {nameof(LookupEntityQueryKind.RelationsLevels)} query.", nameof(template));
        Template = template;
        Levels = levels;
        OneLevel = oneLevel;
    }

    public LookupEntityQuery Template { get; }
    public int Levels { get; }
    public FixpointPlan OneLevel { get; }
}

/// <summary>Another permission, relation or attribute of the same entities, spending a level of depth.</summary>
internal sealed class RefPlan(string entityType, string name) : LookupEntityPlan
{
    public string EntityType { get; } = entityType;
    public string Name { get; } = name;
}

/// <summary>
/// A union or intersection. <see cref="Shared"/> is the multi-relation query that the
/// <see cref="SlicePlan"/> children read their relation from.
/// </summary>
internal sealed class SetPlan(LookupEntityPlan[] children, bool union, LookupEntityQuery? shared) : LookupEntityPlan
{
    public LookupEntityPlan[] Children { get; } = children;
    public bool Union { get; } = union;
    public LookupEntityQuery? Shared { get; } = shared;
}

/// <summary>
/// An intersection of attribute expressions with other children: the others are resolved first,
/// and each expression then reads its attributes for their common entities only.
/// </summary>
internal sealed class ConstrainedIntersectPlan(LookupEntityPlan[] others, LookupEntityQuery? shared,
    ExpressionPlan[] checks) : LookupEntityPlan
{
    public LookupEntityPlan[] Others { get; } = others;
    public LookupEntityQuery? Shared { get; } = shared;
    public ExpressionPlan[] Checks { get; } = checks;
}

/// <summary>
/// An intersection with negated children: the positive children's common entities minus every
/// entity a negated child's inner permission grants.
/// </summary>
internal sealed class NegateIntersectPlan(LookupEntityPlan[] positives, LookupEntityQuery? shared,
    LookupEntityPlan[] negated) : LookupEntityPlan
{
    public LookupEntityPlan[] Positives { get; } = positives;
    public LookupEntityQuery? Shared { get; } = shared;
    public LookupEntityPlan[] Negated { get; } = negated;
}

/// <summary>Every <see cref="EntityType"/> entity the child does not grant.</summary>
internal sealed class NegatePlan(LookupEntityPlan child, string entityType) : LookupEntityPlan
{
    public LookupEntityPlan Child { get; } = child;
    public string EntityType { get; } = entityType;
}
