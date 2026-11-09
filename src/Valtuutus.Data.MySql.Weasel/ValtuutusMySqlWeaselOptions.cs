using JasperFx;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Weasel;

public sealed class ValtuutusMySqlWeaselOptions
{
    /// <summary>
    /// How far an apply may change the database. <see cref="AutoCreate.All"/> is refused because
    /// it recreates tables and deletes their data.
    /// </summary>
    public AutoCreate AutoCreate { get; set; } = AutoCreate.CreateOrUpdate;

    /// <summary>
    /// Connection used to apply the schema. Null uses the factory given to AddMySql, resolved
    /// from a scope created for the apply.
    /// </summary>
    public Func<IServiceProvider, DbConnectionFactory>? ConnectionFactory { get; set; }

    /// <summary>
    /// Named lock (GET_LOCK) that serializes applies across nodes. The lock is taken per database, so
    /// applies over different databases on one server don't wait for each other. At most 31 characters.
    /// </summary>
    public string LockId { get; set; } = "valtuutus-schema";

    /// <summary>
    /// How long an apply waits for another process holding <see cref="LockId"/> before it fails.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(1);
}
