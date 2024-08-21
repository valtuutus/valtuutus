using Akka.Actor;

namespace Valtuutus.Data.InMemory;

internal class TransactionsActor : ReceiveActor
{
    private readonly List<Ulid> _transactions;

    public TransactionsActor()
    {
        _transactions = new List<Ulid>();
        Receive<Commands.GetLatest>(GetLatestHandler);
        Receive<Commands.Create>(CreateHandler);
    }

    private void CreateHandler(Commands.Create obj)
    {
        _transactions.Add(obj.Id);
    }

    private void GetLatestHandler(Commands.GetLatest _)
    {
        Sender.Tell(_transactions.LastOrDefault());
    }

    internal static class Commands
    {
        public record GetLatest()
        {
            public static GetLatest Instance { get; } = new();
        }

        public record Create(Ulid Id);
    }
    
    
    
}