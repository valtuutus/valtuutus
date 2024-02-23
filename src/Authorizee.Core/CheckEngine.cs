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
    Attribute
    
}

public class CheckEngine(IRelationTupleReader relationReader, IAttributeReader attributeReader, Schema schema, ILogger<CheckEngine> logger)
{
    public async Task<bool> Check(CheckRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        logger.LogDebug("Initializing check permission with request: {Req}", req);
        return await CheckInternal(req)(ct);
    }
    
    public async Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        var permission = schema.GetPermissions(req.EntityType);

        var tasks = permission.Select(x => new KeyValuePair<string, Task<bool>>(x.Name, CheckInternal(new CheckRequest
        {
            EntityType = req.EntityType,
            EntityId = req.EntityId,
            Permission = x.Name,
            SubjectType = req.SubjectType,
            SubjectId = req.SubjectId
        })(ct))).ToArray();

        await Task.WhenAll(tasks.Select(x => x.Value));
        return new Dictionary<string, bool>(tasks.ToDictionary(k => k.Key, v => v.Value.Result));

    }

    private static CheckFunction Fail()
    {
        return (_) => Task.FromResult(false);
    }

    private CheckFunction CheckInternal(CheckRequest req)
    {
        logger.LogDebug("Checking permission: {Req}", req);
        
        return schema.GetRelationType(req.EntityType, req.Permission) switch
        {
            RelationType.DirectRelation => CheckRelation(req),
            RelationType.Permission => CheckPermission(req, schema.GetPermission(req.EntityType, req.Permission)),
            RelationType.Attribute => CheckAttribute(req),
            _ => Fail()
        };
    }
    
    private CheckFunction CheckPermission(CheckRequest req, Permission permission)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");

        var permissionNode = permission!.Tree;

        logger.LogDebug("Checking permission {Permission}", permission.Name);

        return permissionNode.Type == PermissionNodeType.Expression
            ? CheckExpression(req, permissionNode)
            : CheckLeaf(req, permissionNode);
    }

    private CheckFunction CheckAttribute(CheckRequest req)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var attribute = await attributeReader.GetAttribute(new EntityAttributeFilter
            {
                Attribute = req.Permission,
                EntityId = req.EntityId,
                EntityType = req.EntityType
            }, ct);

            if (attribute is null)
                return false;

            var attrValue = attribute.Value.GetValue<bool>();

            return attrValue;
        };
    }

    private CheckFunction CheckExpression(CheckRequest req, PermissionNode node)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        logger.LogDebug("Checking permission expression: {Req} with node {Node}", req, node);
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
            logger.LogDebug("Checking permission expression child: {Child}", child);
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
        logger.LogDebug("Checking leaf: {Node}", node);
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
            }, ct);

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

            logger.LogDebug("Checking relation {Relation} with req: {Req}", req.Permission, req);
            var relations = await relationReader.GetRelations(new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = req.Permission
            }, ct);

            var checkFunctions = new List<CheckFunction>(capacity: relations.Count);

            foreach (var relation in relations)
            {
                if (relation.SubjectId == req.SubjectId)
                {
                    logger.LogDebug("Checking relation {Relation} with req: {Req}, returned {Value}", req.Permission, req, true);
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
                            logger.LogDebug("Checking union: a function returned: {Value}", true);
                            cancellationTokenSource.Cancel();
                        }

                        logger.LogDebug("Checking union: a function returned: {Value}", false);
                        return t.Result;
                    }, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
                })).ConfigureAwait(false);

            if (Array.Exists(results, b => b))
            {
                logger.LogDebug("Checking union: returned: {Value}", true);
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Checking union: returned: {Value}, operation cancelled", true);
            return true;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        logger.LogDebug("Checking union: returned: {Value}", false);
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
                            logger.LogDebug("Checking intersection: a function returned: {Value}", false);
                            cancellationTokenSource.Cancel();
                        }

                        logger.LogDebug("Checking intersection: a function returned: {Value}", true);
                        return t.Result;
                    }, cancellationToken);
                })).ConfigureAwait(false);

            var result = Array.TrueForAll(results, b => b);
            logger.LogDebug("Checking intersection: returned: {Value}", result);
            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Checking intersection: returned: {Value}, operation cancelled", false);
            return false;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }
}