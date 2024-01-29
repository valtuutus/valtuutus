-- Drop index "idx_tuples_user" from table: "relation_tuples"
DROP INDEX "public"."idx_tuples_user";
-- Drop index "idx_tuples_userset" from table: "relation_tuples"
DROP INDEX "public"."idx_tuples_userset";
-- Modify "relation_tuples" table
ALTER TABLE "public"."relation_tuples" DROP COLUMN "userset_type", DROP COLUMN "user_id", DROP COLUMN "userset_relation", ADD COLUMN "subject_type" character varying(256) NOT NULL, ADD COLUMN "subject_id" character varying(64) NOT NULL, ADD COLUMN "subject_relation" character varying(64) NOT NULL;
-- Create index "idx_tuples_user" to table: "relation_tuples"
CREATE INDEX "idx_tuples_user" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_id");
-- Create index "idx_tuples_userset" to table: "relation_tuples"
CREATE INDEX "idx_tuples_userset" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "subject_type", "subject_relation");
-- Create "attributes" table
CREATE TABLE "public"."attributes" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "attribute" character varying(64) NOT NULL, "value" jsonb NOT NULL);
-- Create index "idx_attributes" to table: "attributes"
CREATE INDEX "idx_attributes" ON "public"."attributes" ("entity_type", "entity_id", "attribute");
