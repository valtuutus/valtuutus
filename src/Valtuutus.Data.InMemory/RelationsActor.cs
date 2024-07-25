using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal class RelationsActor : ReceiveActor
{

    private readonly List<RelationTuple> _relationTuples;

    public RelationsActor()
    {
        _relationTuples = new List<RelationTuple>();
        
        Receive<Commands.GetRelations>(msg =>
        {
            var result = _relationTuples
                .Where(x => x.EntityType == msg.Filter.EntityType
                            && x.EntityId == msg.Filter.EntityId
                            && x.Relation == msg.Filter.Relation);

            if (!string.IsNullOrEmpty(msg.Filter.SubjectId))
                result = result.Where(x => x.SubjectId == msg.Filter.SubjectId);

            if (!string.IsNullOrEmpty(msg.Filter.SubjectRelation))
                result = result.Where(x => x.SubjectRelation == msg.Filter.SubjectRelation);

            if (!string.IsNullOrEmpty(msg.Filter.SubjectType))
                result = result.Where(x => x.SubjectType == msg.Filter.SubjectType);

            Sender.Tell(result.ToList());
        });
        
        Receive<Commands.GetRelationsWithEntityIds>(msg =>
        {
            var result = _relationTuples
                .Where(x => x.EntityType == msg.EntityRelationFilter.EntityType
                            && x.Relation == msg.EntityRelationFilter.Relation
                            && x.SubjectType == msg.SubjectType
                            && msg.EntitiesIds.Contains(x.EntityId));

            if (!string.IsNullOrEmpty(msg.SubjectRelation))
                result = result.Where(x => x.SubjectRelation == msg.SubjectRelation);

            Sender.Tell(result.ToList());
        });
        
        Receive<Commands.GetRelationsWithSubjectIds>(msg =>
        {
            var result = _relationTuples
                .Where(x => x.EntityType == msg.EntityRelationFilter.EntityType
                            && x.Relation == msg.EntityRelationFilter.Relation
                            && x.SubjectType == msg.SubjectType
                            && msg.SubjectsIds.Contains(x.SubjectId));
            
            Sender.Tell(result
                .ToList());
        });
        
        Receive<Commands.WriteRelations>(msg =>
        {
            _relationTuples.AddRange(msg.Relations);
        });

        Receive<Commands.DeleteRelations>(msg =>
        {
            foreach (var filter in msg.FilterRelations)
            {
                _relationTuples.RemoveAll(x => (filter.EntityId == x.EntityId || string.IsNullOrWhiteSpace(filter.EntityId))
                                              && (filter.EntityType == x.EntityType || string.IsNullOrWhiteSpace(filter.EntityType))
                                              && (filter.SubjectId == x.SubjectId || string.IsNullOrWhiteSpace(filter.SubjectId))
                                              && (filter.SubjectType == x.SubjectType || string.IsNullOrWhiteSpace(filter.SubjectType))
                                              && (filter.SubjectRelation == x.SubjectRelation || string.IsNullOrWhiteSpace(filter.SubjectRelation))
                                              && (filter.Relation == x.Relation || string.IsNullOrWhiteSpace(filter.Relation)));
            }
        });
        
        Receive<Commands.DumpRelations>(msg =>
        {
            Sender.Tell(_relationTuples.ToArray());
        });
    }

    internal static class Commands
    {
        public record GetRelations(RelationTupleFilter Filter);
        
        public record DumpRelations();

        public record GetRelationsWithEntityIds(
            EntityRelationFilter EntityRelationFilter,
            string SubjectType,
            IEnumerable<string> EntitiesIds,
            string? SubjectRelation);
        
        public record GetRelationsWithSubjectIds(
            EntityRelationFilter EntityRelationFilter,
            IEnumerable<string> SubjectsIds,
            string SubjectType);
        
        public record WriteRelations(IEnumerable<RelationTuple> Relations);

        public record DeleteRelations(DeleteRelationsFilter[] FilterRelations);
    }
}