using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class AttributesActor : ReceiveActor
{
    private readonly List<(AttributeTuple attr, string createdTxId, string? deletedTxId)> _attributesTuples;

    public AttributesActor()
    {
        _attributesTuples = new List<(AttributeTuple attr, string createdTxId, string? deletedTxId)>();
        
        Receive<Commands.GetAttribute>(GetAttributeHandler);
        
        Receive<Commands.GetAttributes>(GetAttributesHandler);
        
        Receive<Commands.GetAttributesWithEntitiesIds>(GetAttributesWithEntitiesIdsHandler);
        
        Receive<Commands.WriteAttributes>(WriteAttributesHandler);
        
        Receive<Commands.DeleteAttributes>(DeleteAttributesHandler);
        
        Receive<Commands.DumpAttributes>(DumpAttributesHandler);
        
    }

    private void DumpAttributesHandler(Commands.DumpAttributes _)
    {
        Sender.Tell(_attributesTuples.Where(x => x.deletedTxId is null).ToArray());
    }

    private void DeleteAttributesHandler(Commands.DeleteAttributes msg)
    {
        foreach (var filter in msg.Filters)
        {
            _attributesTuples.RemoveAll(x =>
                x.deletedTxId is null &&
                (filter.EntityId == x.attr.EntityId || string.IsNullOrWhiteSpace(filter.EntityId)) &&
                (filter.EntityType == x.attr.EntityType || string.IsNullOrWhiteSpace(filter.EntityType)) &&
                (filter.Attribute == x.attr.Attribute || string.IsNullOrWhiteSpace(filter.Attribute)));
        }
    }

    private void WriteAttributesHandler(Commands.WriteAttributes msg)
    {
        _attributesTuples.AddRange(msg.Attributes.Select(x => (x, msg.TransactId, (string?)null)));
    }

    private void GetAttributesWithEntitiesIdsHandler(Commands.GetAttributesWithEntitiesIds msg)
    {
        var res = _attributesTuples.Where(x =>
            x.attr.EntityType == msg.Filter.EntityType &&
            x.attr.Attribute == msg.Filter.Attribute &&
            msg.EntitiesIds.Contains(x.attr.EntityId));
        
        if (msg.Filter.SnapToken != null)
        {
            res = res.Where(x =>  string.Compare(x.createdTxId, msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        Sender.Tell(res
            .ToList());
    }

    private void GetAttributesHandler(Commands.GetAttributes msg)
    {
        var res = _attributesTuples.Where(x => x.attr.EntityType == msg.Filter.EntityType && x.attr.Attribute == msg.Filter.Attribute);

        if (msg.Filter.SnapToken != null)
        {
            res = res.Where(x =>  string.Compare(x.createdTxId, msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId)) res = res.Where(x => x.attr.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.ToList());
    }

    private void GetAttributeHandler(Commands.GetAttribute msg)
    {
        var res = _attributesTuples.Where(x => x.attr.EntityType == msg.Filter.EntityType && x.attr.Attribute == msg.Filter.Attribute);

        if (msg.Filter.SnapToken != null)
        {
            res = res.Where(x =>  string.Compare(x.createdTxId, msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId)) res = res.Where(x => x.attr.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.FirstOrDefault());
    }

    internal static class Commands
    {
        public record GetAttribute(EntityAttributeFilter Filter);
        
        public record GetAttributes(EntityAttributeFilter Filter);
        
        public record GetAttributesWithEntitiesIds(AttributeFilter Filter, IEnumerable<string> EntitiesIds);
        
        public record WriteAttributes(string TransactId, IEnumerable<AttributeTuple> Attributes);

        public record DeleteAttributes(DeleteAttributesFilter[] Filters);
        
        public record DumpAttributes
        {
            private DumpAttributes()
            {
            }

            public static DumpAttributes Instance { get; } = new();
        }
    }

}