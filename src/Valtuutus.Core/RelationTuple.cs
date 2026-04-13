namespace Valtuutus.Core;

public readonly record struct RelationTuple
{
    public string EntityType { get; init; }
    public string EntityId { get; init; }
    public string Relation { get; init; }
    public string SubjectType { get; init; }
    public string SubjectId { get; init; }
    public string SubjectRelation { get; init; }

    public RelationTuple(string entityType, string entityId, string relation, string subjectType, string subjectId, string? subjectRelation = null)
    {
        EntityType = entityType;
        EntityId = entityId;
        Relation = relation;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectRelation = subjectRelation ?? "";
    }

    public bool IsDirectSubject() => SubjectRelation == "";
}

internal class RelationTupleComparer : IEqualityComparer<RelationTuple>
{
    private RelationTupleComparer() { }

    internal static IEqualityComparer<RelationTuple> Instance { get; } = new RelationTupleComparer();

    public bool Equals(RelationTuple x, RelationTuple y) =>
        x.SubjectType == y.SubjectType && x.SubjectId == y.SubjectId;

    public int GetHashCode(RelationTuple obj)
    {
        unchecked
        {
            return (obj.SubjectType.GetHashCode() * 397) ^ obj.SubjectId.GetHashCode();
        }
    }
}
