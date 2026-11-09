-- Writes claim a slot here while in flight, so a token resolved without a snap token is one no write
-- can still commit under (a settled token). A slot holds its write's lower bound while the write's
-- transaction holds an app lock named after it; slots are reused rather than added and removed.
CREATE TABLE [dbo].[transactions_pending]
(
    [slot]        int IDENTITY (1,1) NOT NULL,
    [lower_bound] nchar(26)          NULL,
    [claimed_at]  datetime2(7)       NULL,
    CONSTRAINT [PK_transactions_pending] PRIMARY KEY CLUSTERED ([slot] ASC)
);
