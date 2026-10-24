-- Covers the snap predicate of attribute-wide reads (every entity of a type with a given
-- attribute), so their cached plan seeks this index for every attribute, however many rows the
-- call that compiled it returned.
CREATE NONCLUSTERED INDEX [idx_attributes_attribute_entity_type_entity_id] ON [dbo].[attributes]
    (
     [attribute] ASC,
     [entity_type] ASC
        )
    INCLUDE ([entity_id], [value], [created_tx_id], [deleted_tx_id])
    WITH (DROP_EXISTING = ON);
