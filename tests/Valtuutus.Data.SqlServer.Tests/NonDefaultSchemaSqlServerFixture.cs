using Dapper;
using Microsoft.Data.SqlClient;
using Respawn;
using Testcontainers.MsSql;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;


[CollectionDefinition("SqlServerAuthzSpec")]
public sealed class SqlServerAuthzSpecsFixture : ICollectionFixture<NonDefaultSchemaSqlServerFixture>
{
}

/// <summary>
/// Provisions the Valtuutus tables AND the TVP_ListIds user-defined table type under a
/// NON-default SQL schema ("authz") so the schema-qualification fix (commit cda7589) is
/// exercised end-to-end. SQL Server resolves an unqualified UDTT name against the connection's
/// default schema, so without the fix the bare "TVP_ListIds" fails to resolve in [authz] and
/// every lookup/exclusion/batch-attribute query errors.
///
/// This lives in its own test assembly on purpose: SqlServerDataReaderProvider /
/// SqlServerDataWriterProvider cache their schema-qualified SQL in process-wide static fields,
/// so a non-default-schema test cannot share a process with the default-schema (dbo) tests.
/// </summary>
public class NonDefaultSchemaSqlServerFixture : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public const string Schema = "authz";

    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private Respawner _respawner = default!;

    private readonly MsSqlContainer _dbContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithPassword("Valtuutus123!")
        .WithName($"mssql-nondefaultschema-integration-tests-{Guid.NewGuid()}")
        .Build();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        await CreateDatabase("Valtuutus");
        DbFactory = () => new SqlConnection(_dbContainer.GetConnectionString());
        await using var dbConnection = (SqlConnection)DbFactory();
        // CREATE SCHEMA must be the only statement in its batch, so run it separately.
        await dbConnection.ExecuteAsync($"CREATE SCHEMA [{Schema}];");
        await dbConnection.ExecuteAsync(DbMigration);
        await SetupRespawnerAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbContainer.GetConnectionString());
    }

    private async Task SetupRespawnerAsync()
    {
        _respawner = await Respawner.CreateAsync(_dbContainer.GetConnectionString(), new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            SchemasToInclude = new[] { Schema },
        });
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
    }

    private async Task CreateDatabase(string databaseName)
    {
        var createDatabaseScript = $@"
        IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = '{databaseName}')
          BEGIN
            CREATE DATABASE [{databaseName}];
          END;
      ";

        await _dbContainer.ExecScriptAsync(createDatabaseScript)
            .ConfigureAwait(false);
    }

    // Same DDL as the default-schema fixture, but every table, index target and the
    // TVP_ListIds type are qualified with [authz] instead of defaulting to dbo.
    private static string DbMigration =
        """
       -- Create "attributes" table
       CREATE TABLE [authz].[attributes] ([id] bigint IDENTITY (1, 1) NOT NULL, [entity_type] nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [entity_id] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [attribute] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [value] nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [created_tx_id] nchar(26) NOT NULL, [deleted_tx_id] nchar(26), CONSTRAINT [PK_attributes] PRIMARY KEY CLUSTERED ([id] ASC));
       CREATE NONCLUSTERED INDEX [idx_attributes_entity_id_entity_type_attribute] ON [authz].[attributes]
              (
              	[entity_id] ASC,
              	[entity_type] ASC,
              	[attribute] ASC
              )
              INCLUDE([value]);

              CREATE NONCLUSTERED INDEX [idx_attributes_attribute_entity_type_entity_id] ON [authz].[attributes]
              (
              	[attribute] ASC,
              	[entity_type] ASC
              )
              INCLUDE([entity_id],[value]);


              -- Create "relation_tuples" table
       CREATE TABLE [authz].[relation_tuples] ([id] bigint IDENTITY (1, 1) NOT NULL, [entity_type] nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [entity_id] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [relation] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [subject_type] nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [subject_id] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [subject_relation] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NULL, [created_tx_id] nchar(26) NOT NULL, [deleted_tx_id] nchar(26), CONSTRAINT [PK_relation_tuples] PRIMARY KEY CLUSTERED ([id] ASC));

       CREATE NONCLUSTERED INDEX [idx_relation_tuples_entity_type_relation_subject_type_subject_id] ON [authz].[relation_tuples]
              (
              	[entity_type] ASC,
              	[relation] ASC,
              	[subject_type] ASC,
              	[subject_id] ASC
              );

          CREATE NONCLUSTERED INDEX [idx_relation_tuples_entity_type_entity_id_relation] ON [authz].[relation_tuples]
          (
           [entity_type] ASC,
           [entity_id] ASC,
           [relation] ASC
          )
          INCLUDE([subject_type],[subject_id],[subject_relation]);

          CREATE NONCLUSTERED INDEX [idx_relation_tuples_relation_subject_type_subject_id_entity_type] ON [authz].[relation_tuples]
          (
           [relation] ASC,
           [subject_type] ASC,
           [subject_id] ASC,
           [entity_type] ASC
          )
          INCLUDE([id],[entity_id],[subject_relation]);

          CREATE NONCLUSTERED INDEX [idx_relation_tuples_relation_entity_type_entity_id] ON [authz].[relation_tuples]
          (
           [relation] ASC,
           [entity_type] ASC,
           [entity_id] ASC
          )
          INCLUDE([subject_type],[subject_id],[subject_relation]);

          CREATE NONCLUSTERED INDEX [idx_relation_tuples_entity_type_relation_subject_type_subject_id_id] ON [authz].[relation_tuples]
          (
           [entity_type] ASC,
           [relation] ASC,
           [subject_type] ASC,
           [subject_id] ASC,
           [id] ASC
          )
          INCLUDE([entity_id],[subject_relation]);

              -- Create custom type to be used as a list of ids - entity or subject
       CREATE TYPE [authz].[TVP_ListIds] AS TABLE
           (
           [id] [NVARCHAR](64) NOT NULL,
           index tvp_id (id)
           );

       CREATE TABLE [authz].[transactions] ([id] nchar(26) NOT NULL, [created_at] datetime2(7) NOT NULL, CONSTRAINT [PK_transactions] PRIMARY KEY CLUSTERED ([id] ASC));

       CREATE UNIQUE NONCLUSTERED INDEX IX_UniqueAttribute ON [authz].[attributes] (entity_id, entity_type, [attribute]) WHERE deleted_tx_id IS NULL

       CREATE NONCLUSTERED INDEX [idx_relation_tuples_direct] ON [authz].[relation_tuples]
              (
              [entity_type] ASC,
              [entity_id] ASC,
              [relation] ASC,
              [subject_id] ASC
              )
              INCLUDE ([subject_type], [created_tx_id], [deleted_tx_id])
              WHERE [subject_relation] = ''

       CREATE NONCLUSTERED INDEX [idx_relation_tuples_indirect] ON [authz].[relation_tuples]
              (
              [entity_type] ASC,
              [entity_id] ASC,
              [relation] ASC
              )
              INCLUDE ([subject_type], [subject_id], [subject_relation], [created_tx_id], [deleted_tx_id])
              WHERE [subject_relation] <> ''
       """;
}
