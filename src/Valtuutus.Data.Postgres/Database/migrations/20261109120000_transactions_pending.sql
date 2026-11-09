-- Writes register here while in flight, so a token resolved without a snap token is one no write
-- can still commit under (a settled token). Each row lives from just before its write takes an id
-- until that write commits; rows of writes that rolled back or whose connection died are removed
-- by later writes.
CREATE TABLE "public"."transactions_pending"
(
    "id"            bigint      NOT NULL GENERATED ALWAYS AS IDENTITY,
    "lower_bound"   char(26)    NOT NULL,
    "xid"           xid8        NOT NULL,
    "registered_at" timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY ("id")
);
CREATE INDEX "idx_pending_lower_bound" ON "public"."transactions_pending" ("lower_bound");
