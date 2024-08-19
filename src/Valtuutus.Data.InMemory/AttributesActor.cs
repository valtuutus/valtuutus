using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class AttributesActor : ReceiveActor
{
    private record InMemoryTuple(AttributeTuple Attribute, string CreatedTxId, string? DeletedTxId)
    {
        public string? DeletedTxId { get; set; } = DeletedTxId;
    }

    private readonly List<InMemoryTuple> _attributesTuples;

    public AttributesActor()
    {
        _attributesTuples = new();

        Receive<Commands.GetAttribute>(GetAttributeHandler);

        Receive<Commands.GetAttributes>(GetAttributesHandler);

        Receive<Commands.GetAttributesWithEntitiesIds>(GetAttributesWithEntitiesIdsHandler);

        Receive<Commands.WriteAttributes>(WriteAttributesHandler);

        Receive<Commands.DeleteAttributes>(DeleteAttributesHandler);

        Receive<Commands.DumpAttributes>(DumpAttributesHandler);
    }

    private void DumpAttributesHandler(Commands.DumpAttributes _)
    {
        Sender.Tell(_attributesTuples.Where(x => x.DeletedTxId is null)
            .Select(x => x.Attribute)
            .ToArray());
    }

    private void DeleteAttributesHandler(Commands.DeleteAttributes msg)
    {
        foreach (var filter in msg.Filters)
        {
            var attributes = _attributesTuples.Where(x =>
                x.DeletedTxId is null &&
                (filter.EntityId == x.Attribute.EntityId || string.IsNullOrWhiteSpace(filter.EntityId)) &&
                (filter.EntityType == x.Attribute.EntityType || string.IsNullOrWhiteSpace(filter.EntityType)) &&
                (filter.Attribute == x.Attribute.Attribute || string.IsNullOrWhiteSpace(filter.Attribute)));

            foreach (var attribute in attributes)
            {
                attribute.DeletedTxId = msg.TransactId;
            }
        }
    }

    private void WriteAttributesHandler(Commands.WriteAttributes msg)
    {
        _attributesTuples.AddRange(msg.Attributes.Select(x => new InMemoryTuple(x, msg.TransactId, null)));
    }

    private void GetAttributesWithEntitiesIdsHandler(Commands.GetAttributesWithEntitiesIds msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType &&
            x.Attribute.Attribute == msg.Filter.Attribute &&
            msg.EntitiesIds.Contains(x.Attribute.EntityId));

        if (msg.Filter.SnapToken != null)
        {
            res = res
                .Where(x => string.Compare(x.CreatedTxId, msg.Filter.SnapToken.Value.Value,
                    StringComparison.InvariantCulture) <= 0)
                .Where(x => string.IsNullOrEmpty(x.DeletedTxId) || string.Compare(x.DeletedTxId,
                    msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) > 0);
        }

        Sender.Tell(res.Select(x => x.Attribute)
            .ToList());
    }

    private void GetAttributesHandler(Commands.GetAttributes msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType && x.Attribute.Attribute == msg.Filter.Attribute);

        if (msg.Filter.SnapToken != null)
        {
            res = res
                .Where(x => string.Compare(x.CreatedTxId, msg.Filter.SnapToken.Value.Value,
                    StringComparison.InvariantCulture) <= 0)
                .Where(x => string.IsNullOrEmpty(x.DeletedTxId) || string.Compare(x.DeletedTxId,
                    msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) > 0);
            ;
        }

        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId))
            res = res.Where(x => x.Attribute.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.Select(x => x.Attribute).ToList());
    }

    private void GetAttributeHandler(Commands.GetAttribute msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType && x.Attribute.Attribute == msg.Filter.Attribute);

        if (msg.Filter.SnapToken != null)
        {
            res = res.Where(x =>
                    string.Compare(x.CreatedTxId, msg.Filter.SnapToken.Value.Value,
                        StringComparison.InvariantCulture) <= 0)
                .Where(x => string.IsNullOrEmpty(x.DeletedTxId) || string.Compare(x.DeletedTxId,
                    msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) > 0);
        }

        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId))
            res = res.Where(x => x.Attribute.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.Select(x => x.Attribute).FirstOrDefault());
    }

    internal static class Commands
    {
        public record GetAttribute(EntityAttributeFilter Filter);

        public record GetAttributes(EntityAttributeFilter Filter);

        public record GetAttributesWithEntitiesIds(AttributeFilter Filter, IEnumerable<string> EntitiesIds);

        public record WriteAttributes(string TransactId, IEnumerable<AttributeTuple> Attributes);

        public record DeleteAttributes(string TransactId, DeleteAttributesFilter[] Filters);

        public record DumpAttributes
        {
            private DumpAttributes()
            {
            }

            public static DumpAttributes Instance { get; } = new();
        }
    }
}