using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[CollectionDefinition("YugabyteSpec")]
public sealed class YugabyteSpecsFixture : ICollectionFixture<YugabyteFixture>
{
}

/// <summary>
/// Runs the shared data-engine specs against a real YugabyteDB node. YugabyteDB is PostgreSQL-wire
/// compatible but rejects binary <c>COPY</c> and <c>MERGE</c> (SQLSTATE 0A000), so this proves the
/// <see cref="DependencyInjectionExtensions.AddYugabyte"/> provider — which swaps those for INSERT/UPDATE —
/// round-trips correctly through the (unchanged) Postgres reader.
/// </summary>
public class YugabyteFixture : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public DbConnectionFactory DbFactory { get; private set; } = default!;

    private readonly IContainer _dbContainer = new ContainerBuilder()
        .WithImage("yugabytedb/yugabyte:latest")
        .WithName($"yb-integration-tests-{Guid.NewGuid()}")
        .WithCommand("bin/yugabyted", "start", "--daemon=false", "--ui=false")
        .WithPortBinding(5433, true)
        .Build();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        var connectionString =
            $"Host={_dbContainer.Hostname};Port={_dbContainer.GetMappedPublicPort(5433)};" +
            "Username=yugabyte;Password=yugabyte;Database=yugabyte;Include Error Detail=true";
        DbFactory = () => new NpgsqlConnection(connectionString);

        // The mapped port opens before YSQL accepts queries; retry the schema apply until the node is ready.
        await RunWithRetryAsync(async () =>
        {
            await using var conn = (NpgsqlConnection)DbFactory();
            await conn.OpenAsync();
            await conn.ExecuteAsync(DbMigration);
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var conn = (NpgsqlConnection)DbFactory();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM public.relation_tuples; DELETE FROM public.attributes; DELETE FROM public.transactions;");
    }

    public async Task DisposeAsync() => await _dbContainer.DisposeAsync();

    private static async Task RunWithRetryAsync(Func<Task> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception) when (attempt < 40)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    // The full schema from Valtuutus' Postgres migration, including the INCLUDE covering and partial indexes —
    // so this fixture also proves YugabyteDB accepts those DDLs, not just that the writer round-trips against a
    // minimal schema.
    private const string DbMigration =
        """
        CREATE TABLE "public"."attributes" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "attribute" character varying(64) NOT NULL, "value" jsonb NOT NULL, created_tx_id char(26) NOT NULL, deleted_tx_id char(26), PRIMARY KEY ("id"));
        CREATE INDEX "idx_attributes" ON "public"."attributes" ("entity_type", "entity_id", "attribute") INCLUDE ("value");
        CREATE TABLE "public"."relation_tuples" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "relation" character varying(64) NOT NULL, "subject_type" character varying(256) NOT NULL, "subject_id" character varying(64) NOT NULL, "subject_relation" character varying(64) NOT NULL, created_tx_id char(26) NOT NULL, deleted_tx_id char(26), PRIMARY KEY ("id"));
        CREATE INDEX "idx_tuples_entity_relation" ON "public"."relation_tuples" ("entity_type", "relation");
        CREATE INDEX "idx_tuples_subject_entities" ON "public"."relation_tuples" ("entity_type", "relation", "subject_type", "subject_id") INCLUDE ("entity_id", "subject_relation");
        CREATE INDEX "idx_tuples_user" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_id");
        CREATE INDEX "idx_tuples_userset" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_type", "subject_relation");
        CREATE TABLE "public"."transactions" ("id" char(26) NOT NULL, "created_at" timestamptz NOT NULL, PRIMARY KEY ("id"));
        CREATE UNIQUE INDEX "unique_attributes" on public.attributes (entity_type, entity_id, attribute) where deleted_tx_id is null;
        CREATE INDEX "idx_tuples_direct" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_id") INCLUDE ("subject_type", "created_tx_id", "deleted_tx_id") WHERE subject_relation = '';
        CREATE INDEX "idx_tuples_indirect" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation") INCLUDE ("subject_type", "subject_id", "subject_relation", "created_tx_id", "deleted_tx_id") WHERE subject_relation <> '';
        """;
}
