-- Create "attributes" table
CREATE TABLE [attributes] ([id] bigint IDENTITY (1, 1) NOT NULL, [entity_type] varchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [entity_id] varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [attribute] varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [value] varchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, CONSTRAINT [PK_attributes] PRIMARY KEY CLUSTERED ([id] ASC));
-- Create index "idx_attributes" to table: "attributes"
CREATE NONCLUSTERED INDEX [idx_attributes] ON [attributes] ([entity_type] ASC, [entity_id] ASC, [attribute] ASC);
-- Create "relation_tuples" table
CREATE TABLE [relation_tuples] ([id] bigint IDENTITY (1, 1) NOT NULL, [entity_type] varchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [entity_id] varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [relation] varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [subject_type] varchar(256) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [subject_id] varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, [subject_relation] varchar(64) COLLATE SQL_Latin1_General_CP1_CI_AS NULL, CONSTRAINT [PK_relation_tuples] PRIMARY KEY CLUSTERED ([id] ASC));
-- Create index "idx_tuples_user" to table: "relation_tuples"
CREATE NONCLUSTERED INDEX [idx_tuples_user] ON [relation_tuples] ([entity_type] ASC, [entity_id] ASC, [relation] ASC, [subject_id] ASC);
-- Create index "idx_tuples_userset" to table: "relation_tuples"
CREATE NONCLUSTERED INDEX [idx_tuples_userset] ON [relation_tuples] ([entity_type] ASC, [entity_id] ASC, [relation] ASC, [subject_type] ASC, [subject_relation] ASC);
-- Create index "idx_tuples_subject_entities" to table: "relation_tuples"
CREATE NONCLUSTERED INDEX [idx_tuples_subject_entities] ON [relation_tuples] ([entity_type] ASC, [relation] ASC, [subject_type] ASC, [subject_id] ASC);
-- Create index "idx_tuples_entity_relation" to table: "relation_tuples"
CREATE NONCLUSTERED INDEX [idx_tuples_entity_relation] ON [relation_tuples] ([entity_type] ASC, [relation] ASC);


CREATE TYPE TVP_ListIds AS TABLE
    (
    [id] [VARCHAR](64) NOT NULL,
    index tvp_id (id)
    )