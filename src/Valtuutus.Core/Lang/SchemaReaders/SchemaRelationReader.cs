using Valtuutus.Core.Schemas;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang.SchemaReaders;

internal class SchemaRelationReader(SchemaReader schemaReader)
{
    public OneOf<(string, Action<RelationSchemaBuilder>), List<LangError>> Parse(ValtuutusParser.RelationDefinitionContext relation,
        string entityName)
    {
        var relationName = relation.ID().GetText();

        if (IsRelationAlreadyDefined(relation, entityName, relationName))
        {
            return new List<LangError>()
            {
                new()
                {
                    Line = relation.Start.Line,
                    StartPos = relation.Start.Column,
                    Message = $"Entity '{entityName}' '{relationName}' relation already been defined.",
                }
            };
        }

        var refRelations = ParseRelationReferences(relation);
        
        var relationSymbol = new RelationSymbol(relationName, relation.Start.Line, relation.Start.Column, entityName,
            refRelations);

        ValidateMatchingEntitiesReferences(relation, entityName, relationSymbol, relationName);

        schemaReader.AddSymbol(relationSymbol);

        var config = (RelationSchemaBuilder builder) =>
        {
            foreach (var relationReference in refRelations)
            {
                if (!string.IsNullOrEmpty(relationReference.ReferencedEntityRelation))
                {
                    builder.WithEntityType(relationReference.ReferencedEntityName,
                        relationReference.ReferencedEntityRelation);
                }
                else
                {
                    builder.WithEntityType(relationReference.ReferencedEntityName);
                }
            }
        };

        return (relationName, config);
    }

    private List<RelationReference> ParseRelationReferences(ValtuutusParser.RelationDefinitionContext relation)
    {
        var refRelations = new List<RelationReference>();
        
        foreach (var member in relation.relationMember())
        {
            var subjectRelation = member.POUND() != null;
            var refEntityName = member.ID(0).GetText();

            ValidateEntityExists(relation, refEntityName);

            if (!subjectRelation)
            {
                refRelations.Add(new RelationReference()
                {
                    ReferencedEntityName = refEntityName, ReferencedEntityRelation = null
                });
            }
            else
            {
                var refEntityRelation = member.ID(1).GetText();

                ValidateEntityRelationExists(relation, refEntityName, refEntityRelation);

                refRelations.Add(new RelationReference()
                {
                    ReferencedEntityName = refEntityName, ReferencedEntityRelation = refEntityRelation
                });
            }
        }

        return refRelations;
    }

    private void ValidateMatchingEntitiesReferences(ValtuutusParser.RelationDefinitionContext relation,
        string entityName,
        RelationSymbol relationSymbol, string relationName)
    {
        var finalEntities = schemaReader.GetFinalEntitiesNames(relationSymbol);
        if (finalEntities.Count > 1)
        {
            // the relations, after expanding the relation tree, should always end up in the same entity
            // so, if the count > 1 we have more than one entity as leafs of the tree.
            // hence an invalid state
            schemaReader.AddError(new LangError()
            {
                Line = relation.Start.Line,
                StartPos = relation.Start.Column,
                Message =
                    $"Entity '{entityName}' '{relationName}' has inconsistent final entity references.",
            });
        }
    }

    private void ValidateEntityExists(ValtuutusParser.RelationDefinitionContext relation, string refEntityName)
    {
        if (schemaReader.FindEntity(refEntityName) is null)
        {
            schemaReader.AddError(new LangError()
            {
                Line = relation.Start.Line,
                StartPos = relation.Start.Column,
                Message = $"Entity '{refEntityName}' is not defined.",
            });
        }
    }

    private void ValidateEntityRelationExists(ValtuutusParser.RelationDefinitionContext relation, string refEntityName,
        string refEntityRelation)
    {
        if (schemaReader.FindEntityRelation(refEntityName, refEntityRelation) is null)
        {
            schemaReader.AddError(new LangError()
            {
                Line = relation.Start.Line,
                StartPos = relation.Start.Column,
                Message =
                    $"Entity '{refEntityName}' '{refEntityRelation}' relation is not defined.",
            });
        }
    }

    private bool IsRelationAlreadyDefined(ValtuutusParser.RelationDefinitionContext relation, string entityName,
        string relationName)
    {
        return schemaReader.FindEntityRelation(entityName, relationName) is not null;
    }
}