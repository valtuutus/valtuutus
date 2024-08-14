using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class RelationsActor : ReceiveActor
{

    private record InMemoryTuple(RelationTuple Relation, string CreatedTxId, string? DeletedTxId)
    {
        public string? DeletedTxId { get; set; } = DeletedTxId;
    }

    private readonly List<InMemoryTuple> _relationTuples;

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
        Sender.Tell(_relationTuples.Where(x => x.DeletedTxId is null)
            .Select(x => x.Relation)
            .ToArray());
    }

    private void DeleteRelationsHandler(Commands.DeleteRelations msg)
    {
        foreach (var filter in msg.FilterRelations)
        { 
            var relations = _relationTuples.Where(x =>
                x.DeletedTxId is null &&
                (filter.EntityId == x.Relation.EntityId || string.IsNullOrWhiteSpace(filter.EntityId)) &&
                (filter.EntityType == x.Relation.EntityType || string.IsNullOrWhiteSpace(filter.EntityType)) &&
                (filter.SubjectId == x.Relation.SubjectId || string.IsNullOrWhiteSpace(filter.SubjectId)) &&
                (filter.SubjectType == x.Relation.SubjectType || string.IsNullOrWhiteSpace(filter.SubjectType)) &&
                (filter.SubjectRelation == x.Relation.SubjectRelation || string.IsNullOrWhiteSpace(filter.SubjectRelation)) &&
                (filter.Relation == x.Relation.Relation || string.IsNullOrWhiteSpace(filter.Relation)));

            foreach (var relation in relations)
            {
                relation.DeletedTxId = msg.TransactId;
            }
        }
    }

    private void WriteRelationsHandler(Commands.WriteRelations msg)
    {
        _relationTuples.AddRange(msg.Relations.Select(x => new InMemoryTuple(x, msg.TransactId, null)));
    }

    private void GetRelationsWithSubjectIdsHandler(Commands.GetRelationsWithSubjectIds msg)
    {
        var result = _relationTuples.Where(x =>
            x.Relation.EntityType == msg.EntityRelationFilter.EntityType &&
            x.Relation.Relation == msg.EntityRelationFilter.Relation &&
            x.Relation.SubjectType == msg.SubjectType &&
            msg.SubjectsIds.Contains(x.Relation.SubjectId));

        if (msg.EntityRelationFilter.SnapToken != null)
        {
            result = result.Where(x =>  string.Compare(x.CreatedTxId, msg.EntityRelationFilter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        Sender.Tell(result.Select(x => x.Relation).ToList());
    }

    private void GetRelationsWithEntityIdsHandler(Commands.GetRelationsWithEntityIds msg)
    {
        var result = _relationTuples.Where(x =>
            x.Relation.EntityType == msg.EntityRelationFilter.EntityType &&
            x.Relation.Relation == msg.EntityRelationFilter.Relation &&
            x.Relation.SubjectType == msg.SubjectType &&
            msg.EntitiesIds.Contains(x.Relation.EntityId));

        if (msg.EntityRelationFilter.SnapToken != null)
        {
            result = result.Where(x =>  string.Compare(x.CreatedTxId, msg.EntityRelationFilter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        
        if (!string.IsNullOrEmpty(msg.SubjectRelation)) result = result.Where(x => x.Relation.SubjectRelation == msg.SubjectRelation);

        Sender.Tell(result.Select(x => x.Relation).ToList());
    }

    private void GetRelationsHandler(Commands.GetRelations msg)
    {
        var result = _relationTuples.Where(x =>
            x.Relation.EntityType == msg.Filter.EntityType &&
            x.Relation.EntityId == msg.Filter.EntityId &&
            x.Relation.Relation == msg.Filter.Relation);

        if (msg.Filter.SnapToken != null)
        {
            result = result.Where(x =>  string.Compare(x.CreatedTxId, msg.Filter.SnapToken.Value.Value, StringComparison.InvariantCulture) <= 0);
        }
        if (!string.IsNullOrEmpty(msg.Filter.SubjectId)) result = result.Where(x => x.Relation.SubjectId == msg.Filter.SubjectId);

        if (!string.IsNullOrEmpty(msg.Filter.SubjectRelation)) result = result.Where(x => x.Relation.SubjectRelation == msg.Filter.SubjectRelation);

        if (!string.IsNullOrEmpty(msg.Filter.SubjectType)) result = result.Where(x => x.Relation.SubjectType == msg.Filter.SubjectType);

        Sender.Tell(result.Select(x => x.Relation).ToList());
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

        public record DeleteRelations(string TransactId, DeleteRelationsFilter[] FilterRelations);
    }
}