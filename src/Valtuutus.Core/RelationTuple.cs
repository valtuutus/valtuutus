namespace Valtuutus.Core;

public sealed record RelationTuple
{
    public string EntityType { get; private init; } = null!;
    public string EntityId { get; private init; } = null!;
    public string Relation { get; private init; } = null!;
    public string SubjectType { get; private init; } = null!;
    public string SubjectId { get; private init; } = null!;
    public string SubjectRelation { get; private init; } = null!;
    
    public RelationTuple(string entityType, string entityId, string relation, string subjectType, string subjectId, string? subjectRelation = null)
    {
        EntityType = entityType;
        EntityId = entityId;
        Relation = relation;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectRelation = subjectRelation ?? "";
    }


    public bool IsDirectSubject()
    {
        return SubjectRelation == "";
    }
}

internal class RelationTupleComparer : IEqualityComparer<RelationTuple> {
    private RelationTupleComparer()
    {
    }

    internal static IEqualityComparer<RelationTuple> Instance { get; } = new RelationTupleComparer();

    public bool Equals(RelationTuple? x, RelationTuple? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (ReferenceEquals(x, null)) return false;
        if (ReferenceEquals(y, null)) return false;
        if (x.GetType() != y.GetType()) return false;
        return x.SubjectType == y.SubjectType && x.SubjectId == y.SubjectId;
    }

    public int GetHashCode(RelationTuple obj)
    {
        unchecked
        {
            return (obj.SubjectType.GetHashCode() * 397) ^ obj.SubjectId.GetHashCode();
        }
    }
}