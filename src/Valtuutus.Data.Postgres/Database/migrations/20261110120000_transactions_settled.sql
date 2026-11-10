-- Marks writes a request without a snap token is known to see: once marked, no write still in
-- flight can commit at or below them. On a large existing table, consider creating the index
-- CONCURRENTLY outside a transaction.
ALTER TABLE "public"."transactions" ADD COLUMN IF NOT EXISTS "settled" boolean NOT NULL DEFAULT false;
CREATE INDEX IF NOT EXISTS "idx_transactions_settled" ON "public"."transactions" ("id") WHERE "settled";
