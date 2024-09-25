using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines;

namespace Valtuutus.Data.InMemory;

internal sealed class AttributesActor : ReceiveActor
{
    private sealed record InMemoryTuple(AttributeTuple Attribute, Ulid CreatedTxId, Ulid? DeletedTxId)
    {
        public Ulid? DeletedTxId { get; set; } = DeletedTxId;
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

        Receive<Commands.GetEntityAttributesByNames>(GetEntityAttributesByNamesHandler);

        Receive<Commands.GetEntityAttributesByNamesWithEntitiesIds>(GetEntityAttributesByNamesWithEntitiesIdsHandler);
    }

    private void GetEntityAttributesByNamesWithEntitiesIdsHandler(
        Commands.GetEntityAttributesByNamesWithEntitiesIds msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType &&
            msg.Filter.Attributes.Contains(x.Attribute.Attribute) &&
            msg.EntitiesIds.Contains(x.Attribute.EntityId));

        res = ApplySnapTokenFilter(msg.Filter, res);

        Sender.Tell(res.Select(x => x.Attribute)
            .ToDictionary(x => (x.Attribute, x.EntityId)));
    }

    private static IEnumerable<InMemoryTuple> ApplySnapTokenFilter<T>(T withSnapToken,
        IEnumerable<InMemoryTuple> current) where T : IWithSnapToken
    {
        if (withSnapToken.SnapToken != null)
        {
            current = current
                .Where(x => x.CreatedTxId.CompareTo(Ulid.Parse(withSnapToken.SnapToken.Value.Value)) <= 0)
                .Where(x => x.DeletedTxId is null ||
                            x.DeletedTxId.Value.CompareTo(Ulid.Parse(withSnapToken.SnapToken.Value.Value)) >
                            0);
        }

        return current;
    }

    private void GetEntityAttributesByNamesHandler(Commands.GetEntityAttributesByNames msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType &&
            msg.Filter.Attributes.Contains(x.Attribute.Attribute));

        res = ApplySnapTokenFilter(msg.Filter, res);


        Sender.Tell(res.Select(x => x.Attribute)
            .ToDictionary(x => (x.Attribute, x.EntityId)));
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
        var compareAttribute = (AttributeTuple a, AttributeTuple b) =>
            a.EntityId == b.EntityId && a.EntityType == b.EntityType && a.Attribute == b.Attribute;
        
        foreach (var existingAttribute in _attributesTuples)
        {
            if (msg.Attributes.Any(x => compareAttribute(x, existingAttribute.Attribute))
                && existingAttribute.DeletedTxId is null)
            {
                existingAttribute.DeletedTxId = msg.TransactId;
            }
        }

        _attributesTuples.AddRange(msg.Attributes.Select(x => new InMemoryTuple(x, msg.TransactId, null)));
    }

    private void GetAttributesWithEntitiesIdsHandler(Commands.GetAttributesWithEntitiesIds msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType &&
            x.Attribute.Attribute == msg.Filter.Attribute &&
            msg.EntitiesIds.Contains(x.Attribute.EntityId));

        res = ApplySnapTokenFilter(msg.Filter, res);

        Sender.Tell(res.Select(x => x.Attribute)
            .ToList());
    }

    private void GetAttributesHandler(Commands.GetAttributes msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType && x.Attribute.Attribute == msg.Filter.Attribute);

        res = ApplySnapTokenFilter(msg.Filter, res);

        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId))
            res = res.Where(x => x.Attribute.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.Select(x => x.Attribute).ToList());
    }

    private void GetAttributeHandler(Commands.GetAttribute msg)
    {
        var res = _attributesTuples.Where(x =>
            x.Attribute.EntityType == msg.Filter.EntityType && x.Attribute.Attribute == msg.Filter.Attribute);

        res = ApplySnapTokenFilter(msg.Filter, res);


        if (!string.IsNullOrWhiteSpace(msg.Filter.EntityId))
            res = res.Where(x => x.Attribute.EntityId == msg.Filter.EntityId);

        Sender.Tell(res.Select(x => x.Attribute).FirstOrDefault());
    }

    internal static class Commands
    {
        public record GetAttribute(EntityAttributeFilter Filter);

        public record GetAttributes(EntityAttributeFilter Filter);

        public record GetAttributesWithEntitiesIds(AttributeFilter Filter, IEnumerable<string> EntitiesIds);

        public record WriteAttributes(Ulid TransactId, IEnumerable<AttributeTuple> Attributes);

        public record DeleteAttributes(Ulid TransactId, DeleteAttributesFilter[] Filters);

        public record DumpAttributes
        {
            private DumpAttributes()
            {
            }

            public static DumpAttributes Instance { get; } = new();
        }

        public record GetEntityAttributesByNames(EntityAttributesFilter Filter)
        {
        }

        public record GetEntityAttributesByNamesWithEntitiesIds(
            EntityAttributesFilter Filter,
            IEnumerable<string> EntitiesIds);
    }
}