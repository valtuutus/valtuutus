-- Create "attributes" table
CREATE TABLE "public"."attributes" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "attribute" character varying(64) NOT NULL, "value" jsonb NOT NULL);
-- Create index "idx_attributes" to table: "attributes"
CREATE INDEX "idx_attributes" ON "public"."attributes" ("entity_type", "entity_id", "attribute");
-- Create "relation_tuples" table
CREATE TABLE "public"."relation_tuples" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "relation" character varying(64) NOT NULL, "subject_type" character varying(256) NOT NULL, "subject_id" character varying(64) NOT NULL, "subject_relation" character varying(64) NOT NULL, PRIMARY KEY ("id"));
-- Create index "idx_tuples_entity_relation" to table: "relation_tuples"
CREATE INDEX "idx_tuples_entity_relation" ON "public"."relation_tuples" ("entity_type", "relation");
-- Create index "idx_tuples_subject_entities" to table: "relation_tuples"
CREATE INDEX "idx_tuples_subject_entities" ON "public"."relation_tuples" ("entity_type", "relation", "subject_type", "subject_id");
-- Create index "idx_tuples_user" to table: "relation_tuples"
CREATE INDEX "idx_tuples_user" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_id");
-- Create index "idx_tuples_userset" to table: "relation_tuples"
CREATE INDEX "idx_tuples_userset" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_type", "subject_relation");
