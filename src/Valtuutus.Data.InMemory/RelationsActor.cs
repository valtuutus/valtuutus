using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class RelationsActor : ReceiveActor
{

    private readonly List<(RelationTuple rel, string createdTxId, string? deletedTxId)> _relationTuples;

    public RelationsActor()
    {
        _relationTuples = new();
        
        Receive<Commands.GetRelations>(GetRelationsHandler);
        
        Receive<Commands.GetRelationsWithEntityIds>(GetRelationsWithEntityIdsHandler);
        
        Receive<Commands.GetRelationsWithSubjectIds>(GetRelationsWithSubjectIdsHandler);
        
        Receive<Commands.WriteRelations>(WriteRelationsHandler);

        Receive<Commands.DeleteRelations>(DeleteRelationsHandler);
        
        Receive<Commands.DumpRelations>(DumpRelationsHandler);
    }

    private void DumpRelationsHandler(Commands.DumpRelations _)
    {
        Sender.Tell(_relationTuples.Where(x => x.deletedTxId is null).ToArray());
    }

    private void DeleteRelationsHandler(Commands.DeleteRelations msg)
    {
        foreach (var filter in msg.FilterRelations)
        {
            _relationTuples.RemoveAll(x =>
                x.deletedTxId is null &&
                (filter.EntityId == x.rel.EntityId || string.IsNullOrWhiteSpace(filter.EntityId)) &&
                (filter.EntityType == x.rel.EntityType || string.IsNullOrWhiteSpace(filter.EntityType)) &&
                (filter.SubjectId == x.rel.SubjectId || string.IsNullOrWhiteSpace(filter.SubjectId)) &&
                (filter.SubjectType == x.rel.SubjectType || string.IsNullOrWhiteSpace(filter.SubjectType)) &&
                (filter.SubjectRelation == x.rel.SubjectRelation || string.IsNullOrWhiteSpace(filter.SubjectRelation)) &&
                (filter.Relation == x.rel.Relation || string.IsNullOrWhiteSpace(filter.Relation)));
        }
    }

    private void WriteRelationsHandler(Commands.WriteRelations msg)
    {
        _relationTuples.AddRange(msg.Relations.Select(x => (x, msg.TransactId, (string?)null)));
    }

    private void GetRelationsWithSubjectIdsHandler(Commands.GetRelationsWithSubjectIds msg)
    {
        var result = _relationTuples.Where(x =>
            x.rel.EntityType == msg.EntityRelationFilter.EntityType &&
            x.rel.Relation == msg.EntityRelationFilter.Relation &&
            x.rel.SubjectType == msg.SubjectType &&
            msg.SubjectsIds.Contains(x.rel.SubjectId));

        if (msg.EntityRelationFilter.SnapToken != null)
        {
            result = result.Where(x =>  string.Compare(x.createdTxId, msg.EntityRelationFilter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        Sender.Tell(result.ToList());
    }

    private void GetRelationsWithEntityIdsHandler(Commands.GetRelationsWithEntityIds msg)
    {
        var result = _relationTuples.Where(x =>
            x.rel.EntityType == msg.EntityRelationFilter.EntityType &&
            x.rel.Relation == msg.EntityRelationFilter.Relation &&
            x.rel.SubjectType == msg.SubjectType &&
            msg.EntitiesIds.Contains(x.rel.EntityId));

        if (msg.EntityRelationFilter.SnapToken != null)
        {
            result = result.Where(x =>  string.Compare(x.createdTxId, msg.EntityRelationFilter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        
        if (!string.IsNullOrEmpty(msg.SubjectRelation)) result = result.Where(x => x.rel.SubjectRelation == msg.SubjectRelation);

        Sender.Tell(result.ToList());
    }

    private void GetRelationsHandler(Commands.GetRelations msg)
    {
        var result = _relationTuples.Where(x =>
            x.rel.EntityType == msg.Filter.EntityType &&
            x.rel.EntityId == msg.Filter.EntityId &&
            x.rel.Relation == msg.Filter.Relation);

        if (msg.Filter.SnapToken != null)
        {
            result = result.Where(x =>  string.Compare(x.createdTxId, msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        if (!string.IsNullOrEmpty(msg.Filter.SubjectId)) result = result.Where(x => x.rel.SubjectId == msg.Filter.SubjectId);

        if (!string.IsNullOrEmpty(msg.Filter.SubjectRelation)) result = result.Where(x => x.rel.SubjectRelation == msg.Filter.SubjectRelation);

        if (!string.IsNullOrEmpty(msg.Filter.SubjectType)) result = result.Where(x => x.rel.SubjectType == msg.Filter.SubjectType);

        Sender.Tell(result.ToList());
    }

    internal static class Commands
    {
        public record GetRelations(RelationTupleFilter Filter);
        
        public record DumpRelations()
        {
            public static DumpRelations Instance { get; } = new();
        }

        public record GetRelationsWithEntityIds(
            EntityRelationFilter EntityRelationFilter,
            string SubjectType,
            IEnumerable<string> EntitiesIds,
            string? SubjectRelation);
        
        public record GetRelationsWithSubjectIds(
            EntityRelationFilter EntityRelationFilter,
            IEnumerable<string> SubjectsIds,
            string SubjectType);
        
        public record WriteRelations(string TransactId, IEnumerable<RelationTuple> Relations);

        public record DeleteRelations(DeleteRelationsFilter[] FilterRelations);
    }
}