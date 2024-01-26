-- Create "relation_tuples" table
CREATE TABLE "public"."relation_tuples" ("id" bigint NOT NULL GENERATED ALWAYS AS IDENTITY, "entity_type" character varying(256) NOT NULL, "entity_id" character varying(64) NOT NULL, "relation" character varying(64) NOT NULL, "userset_type" character varying(256) NOT NULL, "user_id" character varying(64) NOT NULL, "userset_relation" character varying(64) NOT NULL, PRIMARY KEY ("id"));
-- Create index "idx_tuples_user" to table: "relation_tuples"
CREATE INDEX "idx_tuples_user" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "user_id");
-- Create index "idx_tuples_userset" to table: "relation_tuples"
CREATE INDEX "idx_tuples_userset" ON "public"."relation_tuples" ("entity_type", "entity_id", "relation", "userset_type", "userset_relation");
