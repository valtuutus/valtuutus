using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Core.Schemas;

public record Schema
{
    public FrozenDictionary<string, Entity> Entities { get; init; }
    public FrozenDictionary<string, Function> Functions { get; init; }

    // null value = universal (any subject type can reach — e.g., attribute/fn leaves, cycles)
    // non-null = only the listed subject types can reach
    // missing key = attribute check (not subject-type-dependent, so the guard skips it)
    private readonly FrozenDictionary<(string EntityType, string Name), FrozenSet<string>?> _reachableSubjectTypes;

    public Schema(IDictionary<string, Entity> entities, IDictionary<string, Function> functions)
    {
        Entities = entities.ToFrozenDictionary(StringComparer.Ordinal);
        Functions = functions.ToFrozenDictionary(StringComparer.Ordinal);
        _reachableSubjectTypes = ComputeReachableSubjectTypes(Entities);
    }

    /// <summary>
    /// Classifies <paramref name="permission"/> on <paramref name="entityType"/> as a permission,
    /// direct relation, attribute, or none of those (unknown name / unknown entity type).
    /// </summary>
    public RelationType GetRelationType(string entityType, string permission)
    {
        var found = Entities.TryGetValue(entityType, out var entity);
        if (!found) return RelationType.None;
        if (entity!.Permissions.ContainsKey(permission)) return RelationType.Permission;
        if (entity.Relations.ContainsKey(permission)) return RelationType.DirectRelation;
        if (entity.Attributes.ContainsKey(permission)) return RelationType.Attribute;
        return RelationType.None;
    }

    /// <summary>Looks up a direct relation's schema definition. Throws if not found — check
    /// <see cref="GetRelationType"/> first when the name might not be a direct relation.</summary>
    public Relation GetRelation(string entityType, string relation)
    {
        return Entities[entityType].Relations[relation];
    }

    internal Permission GetPermission(string entityType, string permission)
    {
        return Entities[entityType].Permissions[permission];
    }

    internal Attribute GetAttribute(string entityType, string attribute)
    {
        return Entities[entityType].Attributes[attribute];
    }

    internal IReadOnlyCollection<Permission> GetPermissions(string entityType)
    {
        return Entities[entityType].Permissions.Values;
    }

    /// <summary>
    /// Returns true when <paramref name="subjectType"/> (with optional <paramref name="subjectRelation"/>)
    /// is listed as an allowed subject type for <c>entityType.relation</c> in the schema.
    /// Call before any DB round-trip to short-circuit checks that are impossible by schema.
    /// </summary>
    internal bool IsSubjectTypeAllowedInRelation(string entityType, string relation,
        string subjectType, string? subjectRelation)
    {
        var rel = GetRelation(entityType, relation);
        var subRel = subjectRelation ?? string.Empty;
        foreach (var entity in rel.Entities)
        {
            if (entity.Type == subjectType && (entity.Relation ?? string.Empty) == subRel)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the given subject type can possibly satisfy <c>entityType#permOrRelation</c>
    /// anywhere in the schema graph. Returns true for attributes (not subject-type-constrained)
    /// and for any cycle that could not be statically resolved.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CanSubjectTypeReach(string entityType, string permOrRelation, string subjectType)
    {
        if (!_reachableSubjectTypes.TryGetValue((entityType, permOrRelation), out var types))
            return true; // not in map = attribute or unknown; don't prune

        if (types is null) return true; // universal
        return types.Contains(subjectType);
    }

    private static FrozenDictionary<(string, string), FrozenSet<string>?> ComputeReachableSubjectTypes(
        FrozenDictionary<string, Entity> entities)
    {
        var cache = new Dictionary<(string, string), HashSet<string>?>();
        var inProgress = new HashSet<(string, string)>();

        foreach (var (entityType, entity) in entities)
        {
            foreach (var relName in entity.Relations.Keys)
                ComputeNode(entityType, relName, entities, cache, inProgress);
            foreach (var permName in entity.Permissions.Keys)
                ComputeNode(entityType, permName, entities, cache, inProgress);
            // Attributes are omitted from the map intentionally — guard returns true for missing keys.
        }

        return cache.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value?.ToFrozenSet(StringComparer.Ordinal));
    }

    private static HashSet<string>? ComputeNode(string entityType, string name,
        FrozenDictionary<string, Entity> entities,
        Dictionary<(string, string), HashSet<string>?> cache,
        HashSet<(string, string)> inProgress)
    {
        var key = (entityType, name);
        if (cache.TryGetValue(key, out var cached)) return cached;
        if (!inProgress.Add(key)) return null; // cycle → universal (conservative, never prune)

        HashSet<string>? result;
        if (!entities.TryGetValue(entityType, out var entity))
        {
            result = [];
        }
        else if (entity.Relations.TryGetValue(name, out var rel))
        {
            result = ComputeRelation(rel, entities, cache, inProgress);
        }
        else if (entity.Permissions.TryGetValue(name, out var perm))
        {
            result = ComputePermissionNode(perm.Tree, entityType, entities, cache, inProgress);
        }
        else
        {
            // Attribute or unknown — not added to map, guard returns true for missing keys
            inProgress.Remove(key);
            return null;
        }

        cache[key] = result;
        inProgress.Remove(key);
        return result;
    }

    private static HashSet<string>? ComputeRelation(Relation rel,
        FrozenDictionary<string, Entity> entities,
        Dictionary<(string, string), HashSet<string>?> cache,
        HashSet<(string, string)> inProgress)
    {
        HashSet<string>? result = null;
        foreach (var e in rel.Entities)
        {
            if (e.Relation is null)
            {
                // Terminal: subjects of type e.Type appear directly here
                result ??= new HashSet<string>(StringComparer.Ordinal);
                result.Add(e.Type);
            }
            else
            {
                // Indirect: forward to e.Type#e.Relation
                var sub = ComputeNode(e.Type, e.Relation, entities, cache, inProgress);
                if (sub is null) return null; // universal sub-path → universal here
                result ??= new HashSet<string>(StringComparer.Ordinal);
                result.UnionWith(sub);
            }
        }
        return result ?? [];
    }

    private static HashSet<string>? ComputePermissionNode(PermissionNode node, string entityType,
        FrozenDictionary<string, Entity> entities,
        Dictionary<(string, string), HashSet<string>?> cache,
        HashSet<(string, string)> inProgress)
    {
        if (node.Type == PermissionNodeType.Leaf)
        {
            var leaf = node.LeafNode!;
            if (leaf.Type == PermissionNodeLeafType.Expression)
                return null; // fn/attribute leaf — universal

            var permLeaf = leaf.PermissionNode!;
            if (!permLeaf.IsIndirect)
                return ComputeNode(entityType, permLeaf.Permission, entities, cache, inProgress);

            // TupleToUserSet: entityType#userSet → relatedEntity#computedUserSet
            if (!entities.TryGetValue(entityType, out var entity)) return [];
            if (!entity.Relations.TryGetValue(permLeaf.UserSet!, out var tupleRel)) return [];

            HashSet<string>? result = null;
            foreach (var e in tupleRel.Entities)
            {
                var sub = ComputeNode(e.Type, permLeaf.ComputedUserSet!, entities, cache, inProgress);
                if (sub is null) return null;
                result ??= new HashSet<string>(StringComparer.Ordinal);
                result.UnionWith(sub);
            }
            return result ?? [];
        }

        // Expression node
        var expr = node.ExpressionNode!;
        if (expr.Operation == PermissionOperation.Union)
        {
            HashSet<string>? result = null;
            foreach (var child in expr.Children)
            {
                var sub = ComputePermissionNode(child, entityType, entities, cache, inProgress);
                if (sub is null) return null; // universal child → union is universal
                result ??= new HashSet<string>(StringComparer.Ordinal);
                result.UnionWith(sub);
            }
            return result ?? [];
        }
        else // Intersect
        {
            HashSet<string>? result = null;
            foreach (var child in expr.Children)
            {
                var sub = ComputePermissionNode(child, entityType, entities, cache, inProgress);
                if (sub is null) continue; // universal child — no constraint added
                if (result is null)
                    result = new HashSet<string>(sub, StringComparer.Ordinal);
                else
                    result.IntersectWith(sub);
            }
            return result; // null = all children universal → universal result
        }
    }
}
