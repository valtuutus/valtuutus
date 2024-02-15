using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Core.Schemas;
using Microsoft.Extensions.Logging;
using CheckFunction = System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>;

namespace Authorizee.Core;

public enum RelationType
{
    None,
    DirectRelation,
    Permission,
    Attribute,
    Rule
}

public class PermissionEngine(IRelationTupleReader relationReader, IAttributeReader attributeReader, Schema schema, ILogger<PermissionEngine> logger)
{
    public async Task<bool> Check(CheckRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        logger.LogDebug("Initializing check permission with request: {req}", req);
        return await CheckInternal(req)(ct);
    }

    private CheckFunction Fail()
    {
        return (_) => Task.FromResult(false);
    }

    private CheckFunction CheckInternal(CheckRequest req)
    {
        logger.LogDebug("Checking permission: {req}", req);
        var permission = schema.GetPermissions(req.EntityType)
            .FirstOrDefault(x => x.Name == req.Permission);
        var relation = schema.GetRelations(req.EntityType)
            .FirstOrDefault(x => x.Name == req.Permission);
        var attribute = schema.GetAttributes(req.EntityType)
            .FirstOrDefault(x => x.Name == req.Permission);

        var type = new { permission, relation, attribute } switch
        {
            { permission: null, relation: not null } => RelationType.DirectRelation,
            { permission: not null, relation: null } => RelationType.Permission,
            { attribute: not null} => RelationType.Attribute,
            _ => RelationType.None
        };

        var checkPermission = () =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");

            if (permission is null)
                return Fail();

            var permissionNode = permission.Tree;

            logger.LogDebug("Checking permission {}", permission.Name);
            
            return permissionNode.Type == PermissionNodeType.Expression
                ? CheckExpression(req, permissionNode)
                : CheckLeaf(req, permissionNode);
        };

        return type switch
        {
            RelationType.Permission => checkPermission(),
            RelationType.DirectRelation => CheckRelation(req),
            RelationType.Attribute => CheckAttribute(req),
            _ => Fail()
        };
    }

    private CheckFunction CheckAttribute(CheckRequest req)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var attribute = await attributeReader.GetAttribute(new AttributeFilter
            {
                Attribute = req.Permission,
                EntityId = req.EntityId,
                EntityType = req.EntityType
            });

            if (attribute is null)
                return false;

            var attrValue = attribute.Value.GetValue<bool>();

            return attrValue;
        };
    }

    private CheckFunction CheckExpression(CheckRequest req, PermissionNode node)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        logger.LogDebug("Checking permission expression: {req} with node {node}", req, node);
        return node.ExpressionNode!.Operation switch
        {
            PermissionOperation.Intersect => CheckExpressionChild(req, node.ExpressionNode!.Children, CheckIntersect),
            PermissionOperation.Union => CheckExpressionChild(req, node.ExpressionNode!.Children, CheckUnion),
            _ => throw new InvalidOperationException()
        };
    }

    private CheckFunction CheckExpressionChild(CheckRequest req, List<PermissionNode> children,
        Func<List<CheckFunction>, CancellationToken, Task<bool>> operationCombiner)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var checkFunctions = new List<Func<CancellationToken, Task<bool>>>(capacity: children.Count);
        foreach (var child in children)
        {
            logger.LogDebug("Checking permission expression child: {child}", child);
            switch (child.Type)
            {
                case PermissionNodeType.Expression:
                    checkFunctions.Add(CheckExpression(req, child));
                    break;
                case PermissionNodeType.Leaf:
                    checkFunctions.Add(CheckLeaf(req, child));
                    break;
            }
        }
        
        return async (ct) => await operationCombiner(checkFunctions, ct);
    }

    private CheckFunction CheckLeaf(CheckRequest req, PermissionNode node)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        logger.LogDebug("Checking leaf: {node}", node);
        var perm = node.LeafNode!.Value;

        if (perm.Split('.') is [{ } userSet, { } computedUserSet])
        {
            // Indirect Relation
            return CheckTupleToUserSet(req, userSet, computedUserSet);
        }

        // Direct Relation
        return CheckComputedUserSet(req, perm);
    }

    private CheckFunction CheckComputedUserSet(CheckRequest req, string computedUserSetRelation)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return CheckInternal(req with
        {
            Permission = computedUserSetRelation
        });
    }

    private CheckFunction CheckTupleToUserSet(CheckRequest req, string tupleSetRelation,
        string computedUserSetRelation)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var relations = await relationReader.GetRelations(new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = tupleSetRelation
            });

            var checkFunctions = new List<CheckFunction>(capacity: relations.Count);
            checkFunctions.AddRange(relations.Select(relation =>
                    CheckComputedUserSet(new CheckRequest
                    {
                        EntityType = relation.SubjectType, EntityId = relation.SubjectId,
                        Permission = relation.SubjectRelation, SubjectId = req.SubjectId,
                    }, computedUserSetRelation)
                )
            );

            return await CheckUnion(checkFunctions, ct);
        };
    }

    private CheckFunction CheckRelation(CheckRequest req)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

            logger.LogDebug("Checking relation {relation} with req: {req}", req.Permission, req);
            var relations = await relationReader.GetRelations(new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = req.Permission
            });

            var checkFunctions = new List<CheckFunction>(capacity: relations.Count);

            foreach (var relation in relations)
            {
                if (relation.SubjectId == req.SubjectId)
                {
                    logger.LogDebug("Checking relation {relation} with req: {req}, returned {value}", req.Permission, req, true);
                    return true;
                }

                if (!relation.IsDirectSubject())
                {
                    checkFunctions.Add(CheckInternal(new CheckRequest
                    {
                        EntityType = relation.SubjectType,
                        EntityId = relation.SubjectId,
                        Permission = relation.SubjectRelation,
                        SubjectId = req.SubjectId,
                    }));
                }
            }

            return await CheckUnion(checkFunctions, ct);
        };
    }

    private async Task<bool> CheckUnion(List<CheckFunction> functions, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            var results = await Task.WhenAll(functions
                .Select(f =>
                {
                    return f(cancellationToken).ContinueWith(t =>
                    {
                        if (t.Result)
                        {
                            logger.LogDebug("Checking union: a function returned: {value}", true);
                            cancellationTokenSource.Cancel();
                        }

                        logger.LogDebug("Checking union: a function returned: {value}", false);
                        return t.Result;
                    }, cancellationToken);
                })).ConfigureAwait(false);

            if (results.Any(b => b))
            {
                logger.LogDebug("Checking union: returned: {value}", true);
                return true;
            }
        }
        catch (OperationCanceledException exZ)
        {
            logger.LogDebug("Checking union: returned: {value}, operation cancelled", true);
            return true;
        }

        logger.LogDebug("Checking union: returned: {value}", false);
        return false;
    }
    
    private async Task<bool> CheckIntersect(List<CheckFunction> functions, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            var results = await Task.WhenAll(functions
                .Select(f =>
                {
                    return f(cancellationToken).ContinueWith(t =>
                    {
                        if (!t.Result)
                        {
                            logger.LogDebug("Checking intersection: a function returned: {value}", false);
                            cancellationTokenSource.Cancel();
                        }

                        logger.LogDebug("Checking intersection: a function returned: {value}", true);
                        return t.Result;
                    }, cancellationToken);
                })).ConfigureAwait(false);

            logger.LogDebug("Checking intersection: returned: {value}", results.All(b => b));
            return results.All(b => b);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug("Checking intersection: returned: {value}, operation cancelled", false);
            return false;
        }
    }
}