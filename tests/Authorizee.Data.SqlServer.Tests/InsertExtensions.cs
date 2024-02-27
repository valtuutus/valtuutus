using System.Data;
using Authorizee.Core;
using Authorizee.Data.Configuration;
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

        using var relationsTable = tuples.ToDataTable();
        await sqlBulkRelations.WriteToServerAsync(relationsTable);
    }
    
    public static async Task InsertAttributes(this DbConnectionFactory factory, AttributeTuple[] tuples)
    {
        if(tuples.Length == 0) return;
        await using var db = (SqlConnection) factory();        
        await db.OpenAsync();
        
        using var sqlBulkAttributes = new SqlBulkCopy(db);
        sqlBulkAttributes.DestinationTableName = "attributes";

        using var attributesTable = tuples.ToDataTable();

        await sqlBulkAttributes.WriteToServerAsync(attributesTable);
    }
    
    private static DataTable ToDataTable(this IEnumerable<RelationTuple> items)
    {
        var dataTable = new DataTable("relation_tuples");
        
        dataTable.Columns.Add("entity_type", typeof(string));
        dataTable.Columns.Add("entity_id", typeof(string));
        dataTable.Columns.Add("relation", typeof(string));
        dataTable.Columns.Add("subject_type", typeof(string));
        dataTable.Columns.Add("subject_id", typeof(string));
        dataTable.Columns.Add("subject_relation", typeof(string));
        
        foreach (var tuple in items)
        {
            var row = dataTable.NewRow();
            row["entity_type"] = tuple.EntityType;
            row["entity_id"] = tuple.EntityId;
            row["relation"] = tuple.Relation;
            row["subject_type"] = tuple.SubjectType;
            row["subject_id"] = tuple.SubjectId;
            row["subject_relation"] = tuple.SubjectRelation;
            dataTable.Rows.Add(row);
        }

        return dataTable;
    }
    
    private static DataTable ToDataTable(this IEnumerable<AttributeTuple> items)
    {
        var dataTable = new DataTable("attributes");
        
        dataTable.Columns.Add("entity_type", typeof(string));
        dataTable.Columns.Add("entity_id", typeof(string));
        dataTable.Columns.Add("attribute", typeof(string));
        dataTable.Columns.Add("value", typeof(string));

        foreach (var tuple in items)
        {
            var row = dataTable.NewRow();
            row["entity_type"] = tuple.EntityType;
            row["entity_id"] = tuple.EntityId;
            row["attribute"] = tuple.Attribute;
            row["value"] = tuple.Value.ToJsonString();
            dataTable.Rows.Add(row);
        }

        return dataTable;
    }
}