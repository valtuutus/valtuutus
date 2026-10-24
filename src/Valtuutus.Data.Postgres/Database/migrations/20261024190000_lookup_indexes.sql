-- Serves attribute-wide reads (every entity of a type with a given attribute), which
-- idx_attributes can only bound by entity_type. On a large existing table, consider running
-- this as CREATE INDEX CONCURRENTLY outside a transaction.
CREATE INDEX IF NOT EXISTS "idx_attributes_type_attribute" ON "public"."attributes" ("entity_type", "attribute", "entity_id")
    INCLUDE ("value");
