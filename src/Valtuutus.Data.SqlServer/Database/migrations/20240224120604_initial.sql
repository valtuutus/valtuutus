-- Create "attributes" table
CREATE TABLE [attributes]
(
    [id]            bigint IDENTITY (1, 1)                             NOT NULL,
    [entity_type]   nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    [entity_id]     nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS  NOT NULL,
    [attribute]     nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS  NOT NULL,
    [value]         nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    [created_tx_id] nchar(26)                                          NOT NULL,
    [deleted_tx_id] nchar(26),
    CONSTRAINT [PK_attributes] PRIMARY KEY CLUSTERED ([id] ASC)
    );
CREATE NONCLUSTERED INDEX [idx_attributes_entity_id_entity_type_attribute] ON [dbo].[attributes]
    (
     [entity_id] ASC,
     [entity_type] ASC,
     [attribute] ASC
        )
    INCLUDE ([value]);

CREATE NONCLUSTERED INDEX [idx_attributes_attribute_entity_type_entity_id] ON [dbo].[attributes]
    (
     [attribute] ASC,
     [entity_type] ASC
        )
    INCLUDE ([entity_id], [value]);

-- Create "relation_tuples" table
CREATE TABLE [relation_tuples]
(
    [id]               bigint IDENTITY (1, 1)                             NOT NULL,
    [entity_type]      nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    [entity_id]        nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS  NOT NULL,
    [relation]         nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS  NOT NULL,
    [subject_type]     nvarchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
    [subject_id]       nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS  NOT NULL,
    [subject_relation] nvarchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS  NULL,
    [created_tx_id]    nchar(26)                                          NOT NULL,
    [deleted_tx_id]    nchar(26),
    CONSTRAINT [PK_relation_tuples] PRIMARY KEY CLUSTERED ([id] ASC)
    );

CREATE NONCLUSTERED INDEX [idx_relation_tuples_entity_type_relation_subject_type_subject_id] ON [dbo].[relation_tuples]
    (
     [entity_type] ASC,
     [relation] ASC,
     [subject_type] ASC,
     [subject_id] ASC
        );

CREATE NONCLUSTERED INDEX [idx_relation_tuples_entity_type_entity_id_relation] ON [dbo].[relation_tuples]
    (
     [entity_type] ASC,
     [entity_id] ASC,
     [relation] ASC
        )

CREATE NONCLUSTERED INDEX [idx_relation_tuples_relation_subject_type_subject_id_entity_type] ON [dbo].[relation_tuples]
    (
     [relation] ASC,
     [subject_type] ASC,
     [subject_id] ASC,
     [entity_type] ASC
        )
    INCLUDE ([id], [entity_id], [subject_relation]);

CREATE NONCLUSTERED INDEX [idx_relation_tuples_relation_entity_type_entity_id] ON [dbo].[relation_tuples]
    (
     [relation] ASC,
     [entity_type] ASC,
     [entity_id] ASC
        )
    INCLUDE ([subject_type], [subject_id], [subject_relation]);

CREATE NONCLUSTERED INDEX [idx_relation_tuples_entity_type_relation_subject_type_subject_id_id] ON [dbo].[relation_tuples]
    (
     [entity_type] ASC,
     [relation] ASC,
     [subject_type] ASC,
     [subject_id] ASC,
     [id] ASC
        )
    INCLUDE ([entity_id], [subject_relation]);

create nonclustered index [idx_relationtuples_entitytype_entityid_relation_createdtxid_deletedtxid_id] on [dbo].[relation_tuples]
    (
     [entity_type] asc,
     [entity_id] asc,
     [relation] asc,
     [created_tx_id] asc,
     [deleted_tx_id] asc,
     [id] asc
        )
    include ([subject_type], [subject_id], [subject_relation])

create nonclustered index [idx_relationtuples_relation_createdtxid_subjecttype] on [dbo].[relation_tuples]
    (
     [relation] asc,
     [created_tx_id] asc,
     [subject_type] asc
        )
    include ([id], [entity_type], [entity_id], [subject_id], [subject_relation], [deleted_tx_id])

create nonclustered index [idx_relationtuples_relation_createdtxid_subjecttype_noinclude] on [dbo].[relation_tuples]
    (
     [relation] asc,
     [created_tx_id] asc,
     [subject_type] asc
        )
create nonclustered index [idx_relationtuples_subjecttype_subjectid_relation] on [dbo].[relation_tuples]
    (
     [subject_type] asc,
     [subject_id] asc,
     [relation] asc
        )
    include ([id], [entity_type], [entity_id], [subject_relation], [created_tx_id], [deleted_tx_id])

-- Create custom type to be used as a list of ids - entity or subject
CREATE TYPE TVP_ListIds AS TABLE
(
    [id] [NVARCHAR](64) NOT NULL,
    index tvp_id (id)
);

CREATE TABLE [transactions]
(
    [id]         nchar(26)    NOT NULL,
    [created_at] datetime2(7) NOT NULL,
    CONSTRAINT [PK_transactions] PRIMARY KEY CLUSTERED ([id] ASC)
    );

CREATE UNIQUE NONCLUSTERED INDEX IX_UniqueAttribute ON attributes (entity_id, entity_type, [attribute]) WHERE deleted_tx_id IS NULL;