using System.Data;
using Authorizee.Core;
using Authorizee.Data.Configuration;
using FastMember;
using Microsoft.Data.SqlClient;

namespace Authorizee.Data.SqlServer.Tests;

public static class InsertExtensions
{
    public static async Task InsertRelations(this DbConnectionFactory factory, RelationTuple[] tuples)
    {
        if(tuples.Length == 0) return;
        await using var db = (SqlConnection) factory();
        await db.OpenAsync();
        var sqlBulkRelations = new SqlBulkCopy(db);
        sqlBulkRelations.DestinationTableName = "relation_tuples";

        await using var creader = ObjectReader.Create(tuples);
        sqlBulkRelations.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        sqlBulkRelations.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        sqlBulkRelations.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Relation", "relation"));
        sqlBulkRelations.ColumnMappings.Add( new SqlBulkCopyColumnMapping("SubjectType", "subject_type"));
        sqlBulkRelations.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectId", "subject_id"));
        sqlBulkRelations.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectRelation", "subject_relation"));
        
        await sqlBulkRelations.WriteToServerAsync(creader);
    }
    
    public static async Task InsertAttributes(this DbConnectionFactory factory, AttributeTuple[] tuples)
    {
        if(tuples.Length == 0) return;
        await using var db = (SqlConnection) factory();        
        await db.OpenAsync();
        
        using var sqlBulkAttributes = new SqlBulkCopy(db);
        sqlBulkAttributes.DestinationTableName = "attributes";

        await using var creader = ObjectReader.Create(tuples.Select( t => new { t.EntityType, t.EntityId, t.Attribute, Value = t.Value.ToJsonString() }));
        sqlBulkAttributes.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        sqlBulkAttributes.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        sqlBulkAttributes.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Attribute", "attribute"));
        sqlBulkAttributes.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Value", "value"));

        await sqlBulkAttributes.WriteToServerAsync(creader);
    }
}