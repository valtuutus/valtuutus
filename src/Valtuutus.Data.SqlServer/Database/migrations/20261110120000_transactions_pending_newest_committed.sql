-- The newest id committed when a write claimed its slot, read once the slot was locked. The write
-- commits above it, so readers settle up to it even when the write's own id is older.
ALTER TABLE [dbo].[transactions_pending] ADD [newest_committed] nchar(26) NULL;
