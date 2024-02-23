schema "public" {}

table "relation_tuples" {
  schema = schema.public
  column "id" {
    null = false
    type = bigint
    identity {
      generated = ALWAYS
      start     = 1
      increment = 1
    }
  }
  column "entity_type" {
    null = false
    type = varchar(256)
  }
  column "entity_id" {
    null = false
    type = varchar(64)
  }
  column "relation" {
    null = false
    type = varchar(64)
  }
  column "subject_type" {
    null = false
    type = varchar(256)
  }
  column "subject_id" {
    null = false
    type = varchar(64)
  }
  column "subject_relation" {
    null = false
    type = varchar(64)
  }

  primary_key {
    columns = [column.id]
  }

  index "idx_tuples_user" {
    columns = [column.entity_type, column.entity_id, column.relation, column.subject_id]
  }

  index "idx_tuples_userset" {
    columns = [column.entity_type, column.entity_id, column.relation, column.subject_type, column.subject_relation]
  }

  index "idx_tuples_subject_entities" {
    columns = [column.entity_type, column.relation, column.subject_type, column.subject_id]
  }

  index "idx_tuples_entity_relation" {
    columns = [column.entity_type, column.relation]
  }
}

table "attributes" {
  schema = schema.public
  column "id" {
    null = false
    type = bigint
    identity {
      generated = ALWAYS
      start     = 1
      increment = 1
    }
  }
  column "entity_type" {
    null = false
    type = varchar(256)
  }
  column "entity_id" {
    null = false
    type = varchar(64)
  }
  column "attribute" {
    null = false
    type = varchar(64)
  }
  column "value" {
    null = false
    type = jsonb
  }

  index "idx_attributes" {
    columns = [column.entity_type, column.entity_id, column.attribute]
  }
}