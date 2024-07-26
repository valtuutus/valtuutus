using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

public sealed class InMemoryController
{
    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _relations;
    private readonly IActorRef _attributes;
    
    public InMemoryController()
    {
        _actorSystem = ActorSystem.Create("InMemoryController");
        _relations = _actorSystem.ActorOf<RelationsActor>("relations");
        _attributes = _actorSystem.ActorOf<AttributesActor>("attributes");
    }

    public Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct)
    {
        return _relations.Ask<List<RelationTuple>>(new RelationsActor.Commands.GetRelations(tupleFilter), ct);
    }

    public Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entitiesIds, string? subjectRelation, CancellationToken ct)
    {
        return _relations.Ask<List<RelationTuple>>(
            new RelationsActor.Commands.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entitiesIds,
                subjectRelation), ct);
    }
    
    public Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, IList<string> subjectsIds, string subjectType, CancellationToken ct)
    {
        return _relations.Ask<List<RelationTuple>>(new RelationsActor.Commands.GetRelationsWithSubjectIds(entityFilter, subjectsIds, subjectType), ct);
    }

    public Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct)
    {
        return _attributes.Ask<AttributeTuple?>(new AttributesActor.Commands.GetAttribute(filter), ct);
    }

    public Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct)
    {
        return _attributes.Ask<List<AttributeTuple>>(new AttributesActor.Commands.GetAttributes(filter), ct);
    }

    public Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct)
    {
        return _attributes.Ask<List<AttributeTuple>>(new AttributesActor.Commands.GetAttributesWithEntitiesIds(filter, entitiesIds), ct);
    }

    public void Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        _relations.Tell(new RelationsActor.Commands.WriteRelations(relations));
        _attributes.Tell(new AttributesActor.Commands.WriteAttributes(attributes));
    }

    public void Delete(DeleteFilter filter, CancellationToken ct)
    {
        _attributes.Tell(new AttributesActor.Commands.DeleteAttributes(filter.Attributes));
        _relations.Tell(new RelationsActor.Commands.DeleteRelations(filter.Relations));
    }
    
    
    public async Task<(RelationTuple[], AttributeTuple[])> Dump(CancellationToken ct)
    {
        var relations = _relations.Ask<RelationTuple[]>(RelationsActor.Commands.DumpRelations.Instance, ct);
        var attributes = _attributes.Ask<AttributeTuple[]>(AttributesActor.Commands.DumpAttributes.Instance, ct);
        return (await relations, await attributes);

    }

}