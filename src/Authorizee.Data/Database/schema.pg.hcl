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
    columns = [ column.id]
  }
  
  index "idx_tuples_user" {
    columns = [ column.entity_type, column.entity_id, column.relation, column.user_id]
  }
  
  index "idx_tuples_userset" {
    columns = [ column.entity_type, column.entity_id, column.relation, column.userset_type, column.userset_relation]
  }
}