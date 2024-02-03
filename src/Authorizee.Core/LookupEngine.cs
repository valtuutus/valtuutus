using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Authorizee.Core.Schemas;

namespace Authorizee.Core;

public class LookupEngine(Schema schema, PermissionEngine permissionEngine)
{
    public async Task<ConcurrentBag<string>> LookupEntity(LookupEntityRequest req, CancellationToken ct)
    {
        ConcurrentBag<string> entityIDs = [];

        var callback = (string entityId) =>
        {
            entityIDs.Add(entityId);
        };
        
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
}