using JasperFx;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres.Weasel;

public sealed class ValtuutusPostgresWeaselOptions
{
    /// <summary>
    /// How far an apply may change the database. <see cref="AutoCreate.All"/> is refused because
    /// it recreates tables and deletes their data.
    /// </summary>
    public AutoCreate AutoCreate { get; set; } = AutoCreate.CreateOrUpdate;

    /// <summary>
    /// Connection used to apply the schema. Null uses the factory given to AddPostgres, resolved
    /// from a scope created for the apply.
    /// </summary>
    public Func<IServiceProvider, DbConnectionFactory>? ConnectionFactory { get; set; }

    /// <summary>Advisory lock key that serializes applies across nodes.</summary>
    public int LockId { get; set; } = 1447117908;

    /// <summary>
    /// How long an apply waits for another process holding <see cref="LockId"/> before it fails.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(1);
}
