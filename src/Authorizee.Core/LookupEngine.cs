using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Microsoft.Extensions.Logging;

namespace Authorizee.Core;

public class LookupEngine(
    Schema schema,
    PermissionEngine permissionEngine,
    ILogger<LookupEngine> logger,
    IRelationTupleReader tupleReader)
{
    public async Task<ConcurrentBag<string>> LookupEntity(LookupEntityRequest req, CancellationToken ct)
    {
        var relationsOrAttributes =
            GetPermissionRelationsAndAttributes(req.EntityType, req.Permission, req.SubjectType);

        var relations = relationsOrAttributes.Where(x => x.Type == RelationOrAttributeType.Relation)
            .Select(x => x.Relation!);

        var requestedSubjectRelations = relations.Where(x =>
            x.SubjectType.Equals(req.SubjectType, StringComparison.InvariantCultureIgnoreCase));

        var relationTuples =
            await tupleReader.GetRelations(requestedSubjectRelations
                .Select(x => new EntityRelationFilter
                {
                    Relation = x.Name,
                    EntityType = x.EntityType
                }),
                new SubjectFilter
                {
                    SubjectId = req.SubjectId,
                    SubjectType = req.SubjectType
                }
            );

        ConcurrentBag<string> entityIDs = [];

        var callback = (string entityId) => { entityIDs.Add(entityId); };

        var bulkChecker = new ActionBlock<CheckRequest>(async request =>
        {
            var result = await permissionEngine.Check(request, ct);
            if (result)
            {
                callback(request.EntityId);
            }
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 5
        });

        bulkChecker.Complete();
        await bulkChecker.Completion;
        return entityIDs;
    }

    private HashSet<RelationOrAttribute> GetPermissionRelationsAndAttributes(string finalEntityType,
        string finalEntityPermission, string finalSubjectType)
    {
        var relationSet = new HashSet<RelationOrAttribute>();

        void walkLeaf(string entityType, string entityPermission)
        {
            var relation = schema.GetRelations(entityType)
                .First(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            foreach (var entity in relation.Entities)
            {
                relationSet.Add(new RelationOrAttribute
                {
                    Type = RelationOrAttributeType.Relation,
                    Relation = new Relation
                    {
                        Name = entityPermission,
                        EntityType = entityType,
                        SubjectType = entity.Type
                    }
                });

                if (entity.Type == finalSubjectType)
                {
                    continue;
                }

                if (entity.Relation is not null)
                    walk(entity.Type, entity.Relation);
            }
        }

        void walkExpression(string entityType, PermissionNode node)
        {
            switch (node.Type)
            {
                case PermissionNodeType.Expression:
                    foreach (var child in node.ExpressionNode!.Children)
                    {
                        walkExpression(entityType, child);
                    }

                    break;
                case PermissionNodeType.Leaf:
                    if (node.LeafNode!.Value.Split('.') is [{ } relation, { } entityPermission])
                    {
                        var r = schema.GetRelations(entityType)
                            .First(x => x.Name.Equals(relation, StringComparison.InvariantCultureIgnoreCase));

                        foreach (var entity in r.Entities)
                        {
                            walk(entity.Type, entity.Relation ?? entityPermission);
                        }
                    }
                    else
                    {
                        walk(entityType, node.LeafNode.Value);
                    }

                    break;
            }
        }

        void walk(string entityType, string entityPermission)
        {
            var permission = schema.GetPermissions(entityType)
                .FirstOrDefault(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            var relation = schema.GetRelations(entityType)
                .FirstOrDefault(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            var attribute = schema.GetAttributes(entityType)
                .FirstOrDefault(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            var type = new { permission, relation, attribute } switch
            {
                { permission: null, relation: not null } => RelationType.DirectRelation,
                { permission: not null, relation: null } => RelationType.Permission,
                { attribute: not null } => RelationType.Attribute,
                _ => RelationType.None
            };

            switch (type)
            {
                case RelationType.DirectRelation:
                    walkLeaf(entityType, entityPermission);
                    break;

                case RelationType.Attribute:
                    relationSet.Add(new RelationOrAttribute
                    {
                        Type = RelationOrAttributeType.Attribute,
                        Attribute = new Attribute
                        {
                            EntityType = entityType,
                            Name = entityPermission
                        }
                    });
                    break;

                case RelationType.Permission:
                    walkExpression(entityType, permission.Tree);
                    break;
            }
        }

        walk(finalEntityType, finalEntityPermission);

        return relationSet;
    }
}

public enum RelationOrAttributeType
{
    Attribute,
    Relation
}

public record Relation
{
    public required string SubjectType { get; init; }
    public required string EntityType { get; init; }
    public required string Name { get; init; }
}

public record Attribute
{
    public required string Name { get; init; }
    public required string EntityType { get; init; }
}

public record RelationOrAttribute
{
    public required RelationOrAttributeType Type { get; init; }
    public Attribute? Attribute { get; init; }
    public Relation? Relation { get; init; }
}