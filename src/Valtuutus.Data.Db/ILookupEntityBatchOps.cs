using System.Data.Common;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;

namespace Valtuutus.Data.Db;

/// <summary>
/// Writes LookupEntity V2 queries as commands of a <see cref="DbBatch"/>, so a relational provider
/// runs a whole lookup step in one round trip. The batch itself is created and executed through
/// the provider's <see cref="IRelationalBatchOps"/>. A provider without an implementation runs
/// each query as its own reader call.
/// </summary>
public interface ILookupEntityBatchOps
{
    /// <summary>
    /// Adds the command answering <paramref name="query"/> to <paramref name="batch"/>, on the same
    /// SQL text the provider's single-command reader method runs. A null <paramref name="snapToken"/>
    /// reads the token resolved by an earlier command of the same batch
    /// (<see cref="IRelationalBatchOps.AddResolveLatestSnapTokenToBatch"/>). Reads
    /// <see cref="LookupEntityQuery.Ids"/> before returning.
    /// <para>
    /// Result set shapes: entity_type, entity_id, relation, subject_type, subject_id,
    /// subject_relation for the relation kinds (for <see cref="LookupEntityQueryKind.RelationsLevels"/> and
    /// <see cref="LookupEntityQueryKind.RelationsLevelsFromSubject"/>, one row per entity with empty
    /// subject columns and the relation set only on entities first reached at the last level);
    /// entity_type, entity_id, attribute, value (JSON text) for the
    /// attribute kinds; entity_id for <see cref="LookupEntityQueryKind.EntitiesExcluding"/>.
    /// </para>
    /// </summary>
    void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken);
}
