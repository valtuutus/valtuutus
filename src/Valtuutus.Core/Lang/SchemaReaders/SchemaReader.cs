using Antlr4.Runtime;
using Valtuutus.Core.Schemas;
using Valtuutus.Lang;

namespace Valtuutus.Core.Lang.SchemaReaders;

public class SchemaReader
{
    private readonly List<LangError> _errors = new();
    private readonly List<SchemaSymbol> _symbols = new();
    private readonly SchemaFunctionReader _schemaFunctionReader;
    private readonly SchemaRelationReader _schemaRelationReader;
    private readonly SchemaAttributeReader _schemaAttributeReader;
    private readonly SchemaPermissionReader _schemaPermissionReader;

    public SchemaReader()
    {
        _schemaFunctionReader = new SchemaFunctionReader(this);
        _schemaAttributeReader = new SchemaAttributeReader(this);
        _schemaPermissionReader = new SchemaPermissionReader(this);
        _schemaRelationReader = new SchemaRelationReader(this);
    }

    internal SchemaSymbol? FindEntity(string entityName)
    {
        return _symbols.FirstOrDefault(s => s.Name == entityName && s.Type == SymbolType.Entity);
    }

    internal FunctionSymbol? FindFunction(string functionName)
    {
        return _symbols
            .OfType<FunctionSymbol>()
            .FirstOrDefault(s => s.Name == functionName && s.Type == SymbolType.Function);
    }

    internal RelationSymbol? FindEntityRelation(string entityName, string relationName)
    {
        return _symbols.OfType<RelationSymbol>()
            .FirstOrDefault(s => s.EntityName == entityName &&
                                 s.Name == relationName
                                 && s.Type == SymbolType.Relation);
    }

    internal AttributeSymbol? FindEntityAttribute(string entityName, string attrName)
    {
        return _symbols.OfType<AttributeSymbol>()
            .FirstOrDefault(s => s.EntityName == entityName &&
                                 s.Name == attrName
                                 && s.Type == SymbolType.Attribute);
    }

    internal PermissionSymbol? FindEntityPermission(string entityName, string permission)
    {
        return _symbols.OfType<PermissionSymbol>()
            .FirstOrDefault(s => s.EntityName == entityName &&
                                 s.Name == permission
                                 && s.Type == SymbolType.Permission);
    }

    internal void AddError(LangError error)
    {
        _errors.Add(error);
    }
    
    internal void AddSymbol(SchemaSymbol symbol)
    {
        _symbols.Add(symbol);
    }

    public OneOf<Schema, List<LangError>> Parse(string schema)
    {
        var str = new AntlrInputStream(schema);
        return ParseInternal(str);
    }
    
    public OneOf<Schema, List<LangError>> Parse(Stream schemaStream)
    {
        var str = new AntlrInputStream(schemaStream);
        return ParseInternal(str);
    }

    private OneOf<Schema, List<LangError>> ParseInternal(AntlrInputStream str)
    {
        var schemaBuilder = new SchemaBuilder();
        
        var lexer = new ValtuutusLexer(str);
        var errorListener = new ParserSchemaErrorListener();
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errorListener);
        var tokens = new CommonTokenStream(lexer);
        var parser = new ValtuutusParser(tokens);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errorListener);
        var tree = parser.schema();

        if (errorListener.HasErrors)
        {
            return errorListener.Errors;
        }

        // Parse functions
        foreach (var funcs in tree.functionDefinition())
        {
            try
            {
                var functionNode = _schemaFunctionReader.Parse(funcs);
                schemaBuilder.WithFunction(functionNode);
            }
            catch (LangException ex)
            {
                _errors.Add(ex.ToLangError());
            }
        }

        // Parse entities
        foreach (var entityCtx in tree.entityDefinition())
        {
            var entityName = entityCtx.ID().GetText();

            var existingEntity = FindEntity(entityName);
            if (existingEntity is not null)
            {
                _errors.Add(new LangError()
                {
                    Line = entityCtx.Start.Line,
                    Message =
                        $"Entity '{entityName}' already declared in line {existingEntity.DeclarationLine}:{existingEntity.StartPosition}.",
                    StartPos = existingEntity.StartPosition,
                });
            }

            _symbols.Add(new SchemaSymbol(SymbolType.Entity, entityName, entityCtx.Start.Line,
                entityCtx.ID().Symbol.Column));

            var entityBuilder = schemaBuilder.WithEntity(entityName);

            var relationCtx = entityCtx.entityBody().relationDefinition()!;

            foreach (var relation in relationCtx)
            {
                var res = _schemaRelationReader.Parse(relation, entityName);

                if (res.IsT1)
                {
                    _errors.AddRange(res.AsT1);
                    continue;
                }

                var (relationName, relationConfig) = res.AsT0;
                entityBuilder.WithRelation(relationName, relationConfig);
            }

            var attributeCtx = entityCtx.entityBody().attributeDefinition()!;

            foreach (var attribute in attributeCtx)
            {
                var res = _schemaAttributeReader.Parse(attribute, entityName);
                if (res.IsT1)
                {
                    _errors.AddRange(res.AsT1);
                    continue;
                }

                var (attrName, attrType) = res.AsT0;
                
                entityBuilder.WithAttribute(attrName, attrType);
            }

            var permissionCtx = entityCtx.entityBody().permissionDefinition()!;

            foreach (var permission in permissionCtx)
            {
                try
                {
                    var permissionTree = _schemaPermissionReader.Parse(permission, entityName);
                    entityBuilder.WithPermission(permission.ID().GetText(), permissionTree);
                }
                catch (LangException ex)
                {
                    _errors.Add(ex.ToLangError());
                }
            }
        }

        if (_errors.Count != 0)
        {
            return _errors;
        }

        return schemaBuilder.Build();
    }

    internal HashSet<string> GetFinalEntitiesNames(RelationSymbol relationSymbol)
    {
        var res = new HashSet<string>();
        
        GetFinalEntityNameInternal(relationSymbol, res, []);

        return res;

        void GetFinalEntityNameInternal(RelationSymbol symbol, HashSet<string> finalEntities,
            HashSet<string> visitedEntities)
        {
            if (visitedEntities.Contains(symbol.EntityName))
                return;
                    
            foreach (var reference in symbol.References)
            {
                if (!string.IsNullOrEmpty(reference.ReferencedEntityRelation))
                {
                    var relation = FindEntityRelation(reference.ReferencedEntityName, reference.ReferencedEntityRelation!);

                    if (relation is null)
                    {
                        // Is not up to this algorithm to validate a valid relation
                        continue;
                    }
                    
                    GetFinalEntityNameInternal(relation, finalEntities, visitedEntities);
                    continue;
                }

                finalEntities.Add(reference.ReferencedEntityName);
            }
            
            visitedEntities.Add(symbol.EntityName);
        }
    }
}