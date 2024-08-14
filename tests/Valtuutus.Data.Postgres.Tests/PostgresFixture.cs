using Dapper;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;


[CollectionDefinition("PostgreSqlSpec")]
public sealed class PostgresSpecsFixture : ICollectionFixture<PostgresFixture>
{
}


public class PostgresFixture : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private NpgsqlConnection _dbConnection = default!;
    private Respawner _respawner = default!;

    
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-integration-tests-{Guid.NewGuid()}")
        .Build();
    
    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        DbFactory = () => new NpgsqlConnection(_dbContainer.GetConnectionString());
        _dbConnection = (NpgsqlConnection)DbFactory();
        await _dbConnection.ExecuteAsync(DbMigration);
        await SetupRespawnerAsync();

    }
    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConnection);
    }
    
    private async Task SetupRespawnerAsync()
    {
        await _dbConnection.OpenAsync();
        _respawner = await Respawner.CreateAsync(_dbConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            WithReseed = true,
        });
    }
    

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
    }


    private static string DbMigration = 
        """
        -- Create "attributes" table
        CREATE TABLE "public"."attributes" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "attribute" character varying(64) NOT NULL, "value" jsonb NOT NULL, created_tx_id char(26) NOT NULL, deleted_tx_id char(26), PRIMARY KEY ("id"));
        -- Create index "idx_attributes" to table: "attributes"
        CREATE INDEX "idx_attributes" ON "public"."attributes" ("entity_type", "entity_id", "attribute");
        -- Create "relation_tuples" table
        CREATE TABLE "public"."relation_tuples" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "relation" character varying(64) NOT NULL, "subject_type" character varying(256) NOT NULL, "subject_id" character varying(64) NOT NULL, "subject_relation" character varying(64) NOT NULL, created_tx_id char(26) NOT NULL, deleted_tx_id char(26), PRIMARY KEY ("id"));
        -- Create index "idx_tuples_entity_relation" to table: "relation_tuples"
        CREATE INDEX "idx_tuples_entity_relation" ON "public"."relation_tuples" ("entity_type", "relation");
        -- Create index "idx_tuples_subject_entities" to table: "relation_tuples"
        CREATE INDEX "idx_tuples_subject_entities" ON "public"."relation_tuples" ("entity_type", "relation", "subject_type", "subject_id");
        -- Create index "idx_tuples_user" to table: "relation_tuples"
        CREATE INDEX "idx_tuples_user" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_id");
        -- Create index "idx_tuples_userset" to table: "relation_tuples"
        CREATE INDEX "idx_tuples_userset" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_type", "subject_relation");
        CREATE TABLE "public"."transactions" ("id" char(26) NOT NULL, "created_at" timestamptz NOT NULL, PRIMARY KEY ("id"));
       """;
}