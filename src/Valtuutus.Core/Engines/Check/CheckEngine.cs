using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;
using CheckFunction = System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>>;

namespace Valtuutus.Core.Engines.Check;

public enum RelationType
{
    None,
    DirectRelation,
    Permission,
    Attribute
}

public sealed class CheckEngine(IDataReaderProvider reader, Schema schema) : ICheckEngine
{
    //<inheritdoc/>
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateCheckSpanAttributes(req));

        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);
        var val = await CheckInternal(req)(cancellationToken);
        activity?.AddEvent(new ActivityEvent("CheckFinished",
            tags: new ActivityTagsCollection(CreateCheckResultAttributes(val))));
        return val;
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateCheckResultAttributes(bool result)
    {
        yield return new KeyValuePair<string, object?>("CheckResult", result);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateCheckSpanAttributes(CheckRequest req)
    {
        yield return new KeyValuePair<string, object?>("CheckRequest", req);
    }


    //<inheritdoc/>
    public async Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
            tags: CreateSubjectPermissionSpanAttributes(req));
        var permission = schema.GetPermissions(req.EntityType);
        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);

        var tasks = permission.Select(x => new KeyValuePair<string, Task<bool>>(x.Name,
            CheckInternal(new CheckRequest
            {
                EntityType = req.EntityType,
                EntityId = req.EntityId,
                Permission = x.Name,
                SubjectType = req.SubjectType,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            })(cancellationToken))).ToArray();

        await Task.WhenAll(tasks.Select(x => x.Value));
        var dict = new Dictionary<string, bool>(tasks.ToDictionary(k => k.Key, v => v.Value.Result));
        activity?.AddEvent(new ActivityEvent("SubjectPermissionFinished",
            tags: new ActivityTagsCollection(CreateSubjectPermissionResultAttributes(dict))));
        return dict;
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateSubjectPermissionResultAttributes(
        Dictionary<string, bool> result)
    {
        foreach (var (k, v) in result)
            yield return new KeyValuePair<string, object?>(k, v);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateSubjectPermissionSpanAttributes(
        SubjectPermissionRequest req)
    {
        yield return new KeyValuePair<string, object?>("SubjectPermissionRequest", req);
    }

    private static CheckFunction Fail()
    {
        return (_) => Task.FromResult(false);
    }

    private CheckFunction CheckInternal(CheckRequest req)
    {
        if (req.CheckDepthLimit())
            return Fail();

        req.DecreaseDepth();

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

        return permissionNode.Type == PermissionNodeType.Expression
            ? CheckExpression(req, permissionNode)
            : CheckLeaf(req, permissionNode);
    }

    private CheckFunction CheckAttribute(CheckRequest req)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var attribute = await reader.GetAttribute(
                new EntityAttributeFilter
                {
                    Attribute = req.Permission,
                    EntityId = req.EntityId,
                    EntityType = req.EntityType,
                    SnapToken = req.SnapToken
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

        var checkFunctions = new List<CheckFunction>(capacity: children.Count);
        foreach (var child in children)
        {
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

        return (ct) => operationCombiner(checkFunctions, ct);
    }

    private CheckFunction CheckLeaf(CheckRequest req, PermissionNode node)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return node.LeafNode!.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.LeafNode!.PermissionNode!),
            PermissionNodeLeafType.Expression => CheckLeafFn(req, node.LeafNode!.ExpressionNode!),
            _ => throw new InvalidOperationException()
        };
    }

    private CheckFunction CheckLeafFn(CheckRequest req, PermissionNodeLeafExp node)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

            var fn = schema.Functions[node.FunctionName];

            if (fn is null)
            {
                throw new InvalidOperationException();
            }

            if (!node.IsContextValid(req.Context))
            {
                return false;
            }

            var attributeArguments = node.GetArgsAttributesNames();

            var attributes = await reader.GetAttributes(
                new EntityAttributesFilter
                {
                    Attributes = attributeArguments,
                    EntityId = req.EntityId,
                    EntityType = req.EntityType,
                    SnapToken = req.SnapToken
                }, ct);


            using var paramToArg = fn.CreateParamToArgMap(node.Args);
            
            var getDynamicallyTypedAttribute = (PermissionNodeExpArgumentAttribute arg) =>
            {
                if (!attributes.TryGetValue((arg.AttributeName, req.EntityId), out var attr))
                {
                    return null;
                }

                var attrType = schema.GetAttribute(req.EntityType, arg.AttributeName).Type;

                return attr.GetValue(attrType);
            };

            using var fnArgs = paramToArg.ToLambdaArgs(getDynamicallyTypedAttribute, req.Context);
            
            var res = fn.Lambda(fnArgs.Dictionary);
            return res;
        };
    }

    private CheckFunction CheckLeafPermission(CheckRequest req, PermissionNodeLeafPermission node)
    {
        var perm = node.Permission;

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

        return CheckInternal(req with { Permission = computedUserSetRelation });
    }

    private CheckFunction CheckTupleToUserSet(CheckRequest req, string tupleSetRelation,
        string computedUserSetRelation)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var relations = await reader.GetRelations(
                new RelationTupleFilter
                {
                    EntityId = req.EntityId,
                    EntityType = req.EntityType,
                    Relation = tupleSetRelation,
                    SnapToken = req.SnapToken
                }, ct);

            var checkFunctions = new List<CheckFunction>(capacity: relations.Count);
            checkFunctions.AddRange(relations.Select(relation =>
                    CheckComputedUserSet(
                        new CheckRequest
                        {
                            EntityType = relation.SubjectType,
                            EntityId = relation.SubjectId,
                            Permission = relation.SubjectRelation,
                            SubjectId = req.SubjectId,
                            SnapToken = req.SnapToken,
                            Depth = req.Depth
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

            var relations = await reader.GetRelations(
                new RelationTupleFilter
                {
                    EntityId = req.EntityId,
                    EntityType = req.EntityType,
                    Relation = req.Permission,
                    SnapToken = req.SnapToken
                }, ct);

            var checkFunctions = new List<CheckFunction>(capacity: relations.Count);

            foreach (var relation in relations)
            {
                if (relation.SubjectId == req.SubjectId)
                {
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
                        SnapToken = req.SnapToken,
                        Depth = req.Depth
                    }));
                }
            }

            return await CheckUnion(checkFunctions, ct);
        };
    }

    private static async Task<bool> CheckUnion(List<CheckFunction> functions, CancellationToken ct)
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
                            cancellationTokenSource.Cancel();
                        }

                        return t.Result;
                    }, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
                })).ConfigureAwait(false);

            if (Array.Exists(results, b => b))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        return false;
    }

    private static async Task<bool> CheckIntersect(List<CheckFunction> functions, CancellationToken ct)
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
                            cancellationTokenSource.Cancel();
                        }

                        return t.Result;
                    }, cancellationToken);
                })).ConfigureAwait(false);

            var result = Array.TrueForAll(results, b => b);
            return result;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }
}