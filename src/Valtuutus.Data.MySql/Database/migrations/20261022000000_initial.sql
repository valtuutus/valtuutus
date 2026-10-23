-- Key columns are VARBINARY, so they compare byte for byte: a trailing space or a different case
-- makes a different key. They hold the UTF-8 bytes of the value. The writer enforces the length
-- limits of type names, ids, relations and attributes before anything reaches these columns.
CREATE TABLE transactions (
    id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (id),
    KEY idx_created_at (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
CREATE TABLE relation_tuples (
    id BIGINT NOT NULL AUTO_INCREMENT,
    entity_type VARBINARY(256) NOT NULL,
    entity_id VARBINARY(256) NOT NULL,
    relation VARBINARY(64) NOT NULL,
    subject_type VARBINARY(256) NOT NULL,
    subject_id VARBINARY(256) NOT NULL,
    subject_relation VARBINARY(64) NOT NULL,
    created_tx_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    deleted_tx_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
    PRIMARY KEY (id),
    KEY idx_tuples_entity (entity_type, entity_id, relation, subject_relation, subject_id, subject_type, created_tx_id, deleted_tx_id),
    KEY idx_tuples_subject (entity_type, relation, subject_type, subject_id, subject_relation, entity_id, created_tx_id, deleted_tx_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
-- active is 1 for live rows and NULL for tombstones, so unique_attributes binds only live rows.
-- unique_attributes keeps the primary key's column order, so attribute reads stay on the
-- clustered index.
CREATE TABLE attributes (
    id BIGINT NOT NULL AUTO_INCREMENT,
    entity_type VARBINARY(256) NOT NULL,
    entity_id VARBINARY(256) NOT NULL,
    attribute VARBINARY(64) NOT NULL,
    value LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    created_tx_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    deleted_tx_id CHAR(26) CHARACTER SET ascii COLLATE ascii_bin NULL,
    active TINYINT GENERATED ALWAYS AS (deleted_tx_id IS NULL OR NULL) VIRTUAL,
    PRIMARY KEY (entity_type, attribute, entity_id, id),
    KEY idx_id (id),
    UNIQUE KEY unique_attributes (entity_type, attribute, entity_id, active),
    CONSTRAINT attributes_value_json CHECK (JSON_VALID(value))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
