schema "dbo" {}

table "relation_tuples" {
  schema = schema.dbo
  column "id" {
    null = false
    type = bigint
    identity {
      seed      = 0
      increment = 1
    }
  }
  column "entity_type" {
    null = false
    type = nvarchar(256)
  }
  column "entity_id" {
    null = false
    type = nvarchar(64)
  }
  column "relation" {
    null = false
    type = nvarchar(64)
  }
  column "subject_type" {
    null = false
    type = nvarchar(256)
  }
  column "subject_id" {
    null = false
    type = nvarchar(64)
  }
  column "subject_relation" {
    null = true
    type = nvarchar(64)
  }

  primary_key {
    columns = [column.id]
  }

  index "idx_tuples_user" {
    columns = [column.entity_type, column.entity_id, column.relation, column.subject_id]
    nonclustered = true
  }

  index "idx_tuples_userset" {
    columns = [column.entity_type, column.entity_id, column.relation, column.subject_type, column.subject_relation]
    nonclustered = true
  }

  index "idx_tuples_subject_entities" {
    columns = [column.entity_type, column.relation, column.subject_type, column.subject_id]
    nonclustered = true
  }

  index "idx_tuples_entity_relation" {
    columns = [column.entity_type, column.relation]
    nonclustered = true
  }
}

table "attributes" {
  schema = schema.dbo
  column "id" {
    null = false
    type = bigint
    identity {
      seed      = 0
      increment = 1
    }
  }
  column "entity_type" {
    null = false
    type = nvarchar(256)
  }
  column "entity_id" {
    null = false
    type = nvarchar(64)
  }
  column "attribute" {
    null = false
    type = nvarchar(64)
  }
  column "value" {
    null = false
    type = nvarchar(256)
  }

  primary_key {
    columns = [column.id]
  }
  
  index "idx_attributes" {
    columns = [column.entity_type, column.entity_id, column.attribute]
    nonclustered = true
  }
}