using Akka.Actor;

namespace Valtuutus.Data.InMemory;

internal class InMemoryController : IDisposable
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

    public void Dispose()
    {
        _actorSystem.Dispose();
    }
}

internal class RelationsActor : ReceiveActor
{

    public RelationsActor()
    {
        
    }
}

internal class AttributesActor : ReceiveActor
{

}