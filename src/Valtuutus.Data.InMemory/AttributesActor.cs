using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal class AttributesActor : ReceiveActor
{
    private readonly List<AttributeTuple> _attributesTuples;

    public AttributesActor()
    {
        _attributesTuples = new List<AttributeTuple>();
        
        Receive<Commands.GetAttribute>(msg =>
        {
            var res = _attributesTuples.Where(x =>
                x.EntityType == msg.Filter.EntityType && x.Attribute ==  msg.Filter.Attribute);

            if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId))
                res = res.Where(x => x.EntityId ==  msg.Filter.EntityId);

            Sender.Tell(res.FirstOrDefault());
        });
        
        Receive<Commands.GetAttributes>(msg =>
        {
            var res = _attributesTuples.Where(x =>
                x.EntityType == msg.Filter.EntityType && x.Attribute == msg.Filter.Attribute);

            if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId))
                res = res.Where(x => x.EntityId == msg.Filter.EntityId);

            Sender.Tell(res.ToList());
        });
        
        Receive<Commands.GetAttributesWithEntitiesIds>(msg =>
        {
            Sender.Tell(_attributesTuples
                .Where(x => x.EntityType == msg.Filter.EntityType
                            && x.Attribute == msg.Filter.Attribute
                            && msg.EntitiesIds.Contains(x.EntityId))
                .ToList());
        });
        
        Receive<Commands.WriteAttributes>(msg =>
        {
            _attributesTuples.AddRange(msg.Attributes);
        });
        
        Receive<Commands.DeleteAttributes>(msg =>
        {
            foreach (var filter in msg.Filters)
            {
                _attributesTuples.RemoveAll(x => (filter.EntityId == x.EntityId || string.IsNullOrWhiteSpace(filter.EntityId))
                                                 && (filter.EntityType == x.EntityType || string.IsNullOrWhiteSpace(filter.EntityType))
                                                 && (filter.Attribute == x.Attribute || string.IsNullOrWhiteSpace(filter.Attribute)));
            }
        });
        
        Receive<Commands.DumpAttributes>(msg =>
        {
            Sender.Tell(_attributesTuples.ToArray());
        });
        
    }
    
    internal static class Commands
    {
        public record GetAttribute(EntityAttributeFilter Filter);
        
        public record GetAttributes(EntityAttributeFilter Filter);
        
        public record GetAttributesWithEntitiesIds(AttributeFilter Filter, IEnumerable<string> EntitiesIds);
        
        public record WriteAttributes(IEnumerable<AttributeTuple> Attributes);

        public record DeleteAttributes(DeleteAttributesFilter[] Filters);
        
        public record DumpAttributes;
    }

}