using Weasel.Postgresql.Tables;

namespace Valtuutus.Data.Postgres.Weasel;

/// <summary>Declares the column as an identity the database always assigns, as the scripts do.</summary>
internal sealed class AlwaysIdentity : ColumnCheck
{
    public override string Declaration() => "GENERATED ALWAYS AS IDENTITY";
}
