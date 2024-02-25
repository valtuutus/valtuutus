CREATE NONCLUSTERED INDEX [_dta_index_relation_tuples_8_677577452__K2_K3_K4_K1_5_6_7] ON [dbo].[relation_tuples]
(
	[entity_type] ASC,
	[entity_id] ASC,
	[relation] ASC,
	[id] ASC
)
INCLUDE([subject_type],[subject_id],[subject_relation]) WITH (SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF) ON [PRIMARY]


CREATE NONCLUSTERED INDEX [_dta_index_relation_tuples_8_677577452__K6_K2_K4_K5_K1_3_7] ON [dbo].[relation_tuples]
(
	[subject_id] ASC,
	[entity_type] ASC,
	[relation] ASC,
	[subject_type] ASC,
	[id] ASC
)
INCLUDE([entity_id],[subject_relation]) WITH (SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF) ON [PRIMARY]

CREATE STATISTICS [_dta_stat_677577452_1_2_4_5_6] ON [dbo].[relation_tuples]([id], [entity_type], [relation], [subject_type], [subject_id])

CREATE STATISTICS [_dta_stat_677577452_1_3_2_4] ON [dbo].[relation_tuples]([id], [entity_id], [entity_type], [relation])

DROP INDEX [_dta_index_relation_tuples_8_677577452__K2_K3_K4_K1_5_6_7] ON [dbo].[relation_tuples]
DROP INDEX [idx_attributes] ON [dbo].[attributes]
DROP INDEX [_dta_index_relation_tuples_8_677577452__K6_K2_K4_K5_K1_3_7] ON [dbo].[relation_tuples]
DROP INDEX [_dta_index_relation_tuples_8_677577452_0_K3] ON [dbo].[relation_tuples]
DROP INDEX [_dta_index_relation_tuples_8_677577452_0_K6_6497] ON [dbo].[relation_tuples]
DROP INDEX [idx_tuples_entity_relation] ON [dbo].[relation_tuples]
DROP INDEX [idx_tuples_subject_entities] ON [dbo].[relation_tuples]
DROP INDEX [idx_tuples_user] ON [dbo].[relation_tuples]
DROP INDEX [idx_tuples_userset] ON [dbo].[relation_tuples]
    SET ANSI_PADDING ON

CREATE NONCLUSTERED INDEX [_dta_index_relation_tuples_8_677577452__K3] ON [dbo].[relation_tuples]
(
	[entity_id] ASC
)WITH (SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF) ON [PRIMARY]
SET ANSI_PADDING ON

CREATE NONCLUSTERED INDEX [_dta_index_relation_tuples_8_677577452__K6] ON [dbo].[relation_tuples]
(
	[subject_id] ASC
)WITH (SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF) ON [PRIMARY]
SET ANSI_PADDING ON

CREATE NONCLUSTERED INDEX [_dta_index_relation_tuples_8_677577452__K6_K5_K1] ON [dbo].[relation_tuples]
(
	[subject_id] ASC,
	[subject_type] ASC,
	[id] ASC
)WITH (SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF) ON [PRIMARY]
CREATE STATISTICS [_dta_stat_677577452_1_5] ON [dbo].[relation_tuples]([id], [subject_type])
CREATE NONCLUSTERED INDEX idx_relation_tuples_entity_type_entity_id_relation
ON [dbo].[relation_tuples] ([entity_type],[entity_id],[relation])
INCLUDE ([subject_type],[subject_id],[subject_relation])