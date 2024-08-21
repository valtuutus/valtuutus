using Akka.Actor;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class InMemoryController
{
    private readonly ActorSystem _actorSystem;
    private readonly IActorRef _relations;
    private readonly IActorRef _attributes;
    private readonly IActorRef _transactions;

    
    public InMemoryController()
    {
        _actorSystem = ActorSystem.Create("InMemoryController");
        _relations = _actorSystem.ActorOf<RelationsActor>("relations");
        _attributes = _actorSystem.ActorOf<AttributesActor>("attributes");
        _transactions = _actorSystem.ActorOf<TransactionsActor>("transactions");
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

    public void Write(Ulid transactId, IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes,
        CancellationToken ct)
    {
        _relations.Tell(new RelationsActor.Commands.WriteRelations(transactId, relations));
        _attributes.Tell(new AttributesActor.Commands.WriteAttributes(transactId, attributes));
    }

    public void Delete(Ulid transactId, DeleteFilter filter, CancellationToken ct)
    {
        _attributes.Tell(new AttributesActor.Commands.DeleteAttributes(transactId, filter.Attributes));
        _relations.Tell(new RelationsActor.Commands.DeleteRelations(transactId, filter.Relations));
    }
    
    
    public async Task<(RelationTuple[], AttributeTuple[])> Dump(CancellationToken ct)
    {
        var relations = _relations.Ask<RelationTuple[]>(RelationsActor.Commands.DumpRelations.Instance, ct);
        var attributes = _attributes.Ask<AttributeTuple[]>(AttributesActor.Commands.DumpAttributes.Instance, ct);
        return (await relations, await attributes);

    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken ct)
    {
        var id = await _transactions.Ask<Ulid?>(TransactionsActor.Commands.GetLatest.Instance, ct);
        return id is null ? null : new SnapToken(id.Value.ToString());
    }

    public void CreateTransaction(Ulid id)
    {
        _transactions.Tell(new TransactionsActor.Commands.Create(id));
    }
}