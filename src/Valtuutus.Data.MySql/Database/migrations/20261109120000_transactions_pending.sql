-- Writes claim a slot here while in flight, so a token resolved without a snap token is one no
-- write can still commit under (a settled token). A slot holds its write's lower bound from just
-- before the write takes an id until the write commits, and '~~~~~~~~~~~~~~~~~~~~~~~~~~' (above every id) while free;
-- slots are reused rather than added and removed, so reads never step over removed rows.
CREATE TABLE transactions_pending (
    slot BIGINT NOT NULL AUTO_INCREMENT,
    lower_bound CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    claimed_at DATETIME(6) NOT NULL,
    PRIMARY KEY (slot)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
