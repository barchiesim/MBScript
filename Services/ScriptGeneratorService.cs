using MBScript.Models;
using System.Text;

namespace MBScript.Services;

public class ScriptGeneratorService
{
    private readonly SqlService _sql;
    private readonly DatabaseExplorerService _dbExplorer;

    public ScriptGeneratorService(SqlService sql, DatabaseExplorerService dbExplorer)
    {
        _sql = sql; _dbExplorer = dbExplorer;
    }

    public async Task<string> GenerateCreateTableScriptAsync(string database, string schema, string tableName, ScriptGenerationOptions? options = null)
    {
        options ??= new ScriptGenerationOptions();
        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================");
        sb.AppendLine($"-- Create Table: [{schema}].[{tableName}]");
        sb.AppendLine($"-- Database: {database}");
        sb.AppendLine($"-- Generated: {DateTime.Now:O}");
        sb.AppendLine("-- ============================================");
        sb.AppendLine();
        if (options.IncludeIfExists)
        {
            sb.AppendLine($"IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[{schema}].[{tableName}]') AND type in (N'U'))");
            sb.AppendLine($"DROP TABLE [{schema}].[{tableName}]");
            sb.AppendLine("GO"); sb.AppendLine();
        }
        sb.Append(await _dbExplorer.GetTableDdlAsync(database, schema, tableName));
        sb.AppendLine("GO"); sb.AppendLine();
        if (options.IncludeForeignKeys)
        {
            var fkr = await _dbExplorer.GetForeignKeysAsync(database, schema, tableName);
            if (fkr.Success && fkr.Data?.Count > 0)
            {
                sb.AppendLine("-- Foreign Keys");
                foreach (var grp in fkr.Data.GroupBy(f => f.ConstraintName))
                {
                    var fks = grp.ToList();
                    var cols    = string.Join(", ", fks.Select(f => $"[{f.ColumnName}]"));
                    var refCols = string.Join(", ", fks.Select(f => $"[{f.ReferencedColumnName}]"));
                    sb.AppendLine($"ALTER TABLE [{schema}].[{tableName}]");
                    sb.AppendLine($"ADD CONSTRAINT [{grp.Key}] FOREIGN KEY ({cols})");
                    sb.AppendLine($"REFERENCES [{fks[0].ReferencedTableSchema}].[{fks[0].ReferencedTableName}] ({refCols})");
                    sb.AppendLine("GO"); sb.AppendLine();
                }
            }
        }
        if (options.IncludeIndexes)
        {
            var idxr = await _dbExplorer.GetTableIndexesAsync(database, schema, tableName);
            if (idxr.Success && idxr.Data?.Count > 0)
            {
                sb.AppendLine("-- Indexes");
                foreach (var grp in idxr.Data.GroupBy(i => i.IndexName))
                {
                    var cols = grp.ToList();
                    if (cols[0].IndexType is "CLUSTERED" or "PRIMARY") continue;
                    var unique   = cols[0].IsUnique ? "UNIQUE " : "";
                    var colNames = string.Join(", ", cols.Select(c => $"[{c.ColumnName}]"));
                    sb.AppendLine($"CREATE {unique}{cols[0].IndexType} INDEX [{grp.Key}]");
                    sb.AppendLine($"ON [{schema}].[{tableName}] ({colNames})");
                    sb.AppendLine("GO"); sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }

    public Task<string> GenerateInsertScriptAsync(string database, string schema, string tableName, ScriptGenerationOptions? options = null) =>
        _dbExplorer.GenerateInsertScriptAsync(database, schema, tableName, options?.BatchSize ?? 1000);

    public static string GenerateUpdateScript(string schema, string tableName,
        Dictionary<string, object?> updateColumns, Dictionary<string, object?> whereColumns)
    {
        var sb = new StringBuilder($"UPDATE [{schema}].[{tableName}]\nSET\n");
        sb.Append(string.Join(",\n", updateColumns.Select(kv => $"  [{kv.Key}] = {SqlService.SqlFmt(kv.Value)}")));
        if (whereColumns.Count > 0)
        {
            sb.Append("\nWHERE\n");
            sb.Append(string.Join("\n  AND ", whereColumns.Select(kv =>
                kv.Value is null ? $"  [{kv.Key}] IS NULL" : $"  [{kv.Key}] = {SqlService.SqlFmt(kv.Value)}")));
        }
        return sb.ToString();
    }

    public static string GenerateDeleteScript(string schema, string tableName, Dictionary<string, object?> whereColumns)
    {
        var sb = new StringBuilder($"DELETE FROM [{schema}].[{tableName}]");
        if (whereColumns.Count > 0)
        {
            sb.Append("\nWHERE\n");
            sb.Append(string.Join("\n  AND ", whereColumns.Select(kv =>
                kv.Value is null ? $"  [{kv.Key}] IS NULL" : $"  [{kv.Key}] = {SqlService.SqlFmt(kv.Value)}")));
        }
        return sb.ToString();
    }
}
