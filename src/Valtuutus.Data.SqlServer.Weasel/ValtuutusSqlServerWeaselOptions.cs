using JasperFx;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer.Weasel;

public sealed class ValtuutusSqlServerWeaselOptions
{
    /// <summary>
    /// How far an apply may change the database. <see cref="AutoCreate.All"/> is refused because
    /// it recreates tables and deletes their data.
    /// </summary>
    public AutoCreate AutoCreate { get; set; } = AutoCreate.CreateOrUpdate;

    /// <summary>
    /// Connection used to apply the schema. Null uses the factory given to AddSqlServer, resolved
    /// from a scope created for the apply.
    /// </summary>
    public Func<IServiceProvider, DbConnectionFactory>? ConnectionFactory { get; set; }

    /// <summary>Application lock resource that serializes applies across nodes.</summary>
    public string LockId { get; set; } = "valtuutus-schema";

    /// <summary>
    /// How long an apply waits for another process holding <see cref="LockId"/> before it fails.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(1);
}
