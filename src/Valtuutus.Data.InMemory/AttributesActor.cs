using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class AttributesActor : ReceiveActor
{
    private readonly List<AttributeTuple> _attributesTuples;

    public AttributesActor()
    {
        _attributesTuples = new List<AttributeTuple>();
        
        Receive<Commands.GetAttribute>(GetAttributeHandler);
        
        Receive<Commands.GetAttributes>(GetAttributesHandler);
        
        Receive<Commands.GetAttributesWithEntitiesIds>(GetAttributesWithEntitiesIdsHandler);
        
        Receive<Commands.WriteAttributes>(WriteAttributesHandler);
        
        Receive<Commands.DeleteAttributes>(DeleteAttributesHandler);
        
        Receive<Commands.DumpAttributes>(DumpAttributesHandler);
        
    }

    private void DumpAttributesHandler(Commands.DumpAttributes _)
    {
        Sender.Tell(_attributesTuples.ToArray());
    }

    private void DeleteAttributesHandler(Commands.DeleteAttributes msg)
    {
        foreach (var filter in msg.Filters)
        {
            _attributesTuples.RemoveAll(x => (filter.EntityId == x.EntityId || string.IsNullOrWhiteSpace(filter.EntityId)) && (filter.EntityType == x.EntityType || string.IsNullOrWhiteSpace(filter.EntityType)) && (filter.Attribute == x.Attribute || string.IsNullOrWhiteSpace(filter.Attribute)));
        }
    }

    private void WriteAttributesHandler(Commands.WriteAttributes msg)
    {
        _attributesTuples.AddRange(msg.Attributes);
    }

    private void GetAttributesWithEntitiesIdsHandler(Commands.GetAttributesWithEntitiesIds msg)
    {
        Sender.Tell(_attributesTuples.Where(x => x.EntityType == msg.Filter.EntityType && x.Attribute == msg.Filter.Attribute && msg.EntitiesIds.Contains(x.EntityId))
            .ToList());
    }

    private void GetAttributesHandler(Commands.GetAttributes msg)
    {
        var res = _attributesTuples.Where(x => x.EntityType == msg.Filter.EntityType && x.Attribute == msg.Filter.Attribute);

        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId)) res = res.Where(x => x.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.ToList());
    }

    private void GetAttributeHandler(Commands.GetAttribute msg)
    {
        var res = _attributesTuples.Where(x => x.EntityType == msg.Filter.EntityType && x.Attribute == msg.Filter.Attribute);

        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId)) res = res.Where(x => x.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.FirstOrDefault());
    }

    internal static class Commands
    {
        public record GetAttribute(EntityAttributeFilter Filter);
        
        public record GetAttributes(EntityAttributeFilter Filter);
        
        public record GetAttributesWithEntitiesIds(AttributeFilter Filter, IEnumerable<string> EntitiesIds);
        
        public record WriteAttributes(IEnumerable<AttributeTuple> Attributes);

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