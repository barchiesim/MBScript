using PBScriptNew.Models;
using System.Text;

namespace PBScriptNew.Services;

public class DatabaseExplorerService
{
    private readonly SqlService _sql;

    public DatabaseExplorerService(SqlService sqlService) => _sql = sqlService;

    public Task<SqlResult<List<DatabaseInfo>>> GetDatabasesAsync() =>
        _sql.ExecuteQueryAsync<DatabaseInfo>(@"
            SELECT name, database_id, create_date, state_desc
            FROM sys.databases WHERE database_id > 4 ORDER BY name",
        r => new DatabaseInfo
        {
            Name       = r["name"]?.ToString()                                 ?? "",
            DatabaseId = r["database_id"] is int id ? id : 0,
            CreateDate = r["create_date"] is DateTime dt ? dt : DateTime.MinValue,
            StateDesc  = r["state_desc"]?.ToString()                           ?? ""
        });

    public Task<SqlResult<List<TableInfo>>> GetTablesAsync(string database, string? filter = null)
    {
        var sb = new StringBuilder($@"
            SELECT TABLE_SCHEMA AS table_schema, TABLE_NAME AS table_name, TABLE_TYPE AS table_type
            FROM [{database}].INFORMATION_SCHEMA.TABLES");
        if (!string.IsNullOrWhiteSpace(filter))
            sb.Append($" WHERE TABLE_NAME LIKE '%{filter}%'");
        sb.Append(" ORDER BY TABLE_SCHEMA, TABLE_NAME");
        return _sql.ExecuteQueryAsync<TableInfo>(sb.ToString(), r => new TableInfo
        {
            TableSchema = r["table_schema"]?.ToString() ?? "",
            TableName   = r["table_name"]?.ToString()   ?? "",
            TableType   = r["table_type"]?.ToString()   ?? ""
        });
    }

    public Task<SqlResult<List<ColumnInfo>>> GetTableColumnsAsync(string database, string schema, string tableName) =>
        _sql.ExecuteQueryAsync<ColumnInfo>($@"
            SELECT c.COLUMN_NAME AS column_name, c.DATA_TYPE AS data_type,
                   c.CHARACTER_MAXIMUM_LENGTH AS character_maximum_length,
                   c.IS_NULLABLE AS is_nullable, c.COLUMN_DEFAULT AS column_default,
                   c.ORDINAL_POSITION AS ordinal_position,
                   sc.is_identity
            FROM [{database}].INFORMATION_SCHEMA.COLUMNS c
            JOIN [{database}].sys.columns sc
                ON sc.object_id = OBJECT_ID(N'[{database}].[{schema}].[{tableName}]')
               AND sc.name = c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = '{schema}' AND c.TABLE_NAME = '{tableName}'
            ORDER BY c.ORDINAL_POSITION",
        r => new ColumnInfo
        {
            ColumnName             = r["column_name"]?.ToString()                           ?? "",
            DataType               = r["data_type"]?.ToString()                             ?? "",
            CharacterMaximumLength = r["character_maximum_length"] is int len ? len : null,
            IsNullable             = r["is_nullable"]?.ToString()                           ?? "YES",
            ColumnDefault          = r["column_default"]?.ToString(),
            OrdinalPosition        = r["ordinal_position"] is int ord ? ord : 0,
            IsIdentity             = r["is_identity"] is bool b ? b : r["is_identity"]?.ToString() == "True"
        });

    public Task<SqlResult<List<PrimaryKeyInfo>>> GetPrimaryKeysAsync(string database, string schema, string tableName) =>
        _sql.ExecuteQueryAsync<PrimaryKeyInfo>($@"
            SELECT kcu.CONSTRAINT_NAME AS constraint_name,
                   kcu.COLUMN_NAME AS column_name, kcu.ORDINAL_POSITION AS ordinal_position
            FROM [{database}].INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN [{database}].INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
             AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA AND tc.TABLE_NAME = kcu.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND tc.TABLE_SCHEMA = '{schema}' AND tc.TABLE_NAME = '{tableName}'
            ORDER BY kcu.ORDINAL_POSITION",
        r => new PrimaryKeyInfo
        {
            ConstraintName  = r["constraint_name"]?.ToString()   ?? "",
            ColumnName      = r["column_name"]?.ToString()       ?? "",
            OrdinalPosition = r["ordinal_position"] is int o ? o : 0
        });

    public Task<SqlResult<List<ForeignKeyInfo>>> GetForeignKeysAsync(string database, string schema, string tableName) =>
        _sql.ExecuteQueryAsync<ForeignKeyInfo>($@"
            SELECT fk.name AS constraint_name,
                   COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS column_name,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id, DB_ID('{database}')) AS referenced_table_schema,
                   OBJECT_NAME(fk.referenced_object_id, DB_ID('{database}')) AS referenced_table_name,
                   COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS referenced_column_name
            FROM [{database}].sys.foreign_keys fk
            INNER JOIN [{database}].sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id, DB_ID('{database}')) = '{schema}'
              AND OBJECT_NAME(fk.parent_object_id, DB_ID('{database}')) = '{tableName}'",
        r => new ForeignKeyInfo
        {
            ConstraintName        = r["constraint_name"]?.ToString()         ?? "",
            ColumnName            = r["column_name"]?.ToString()             ?? "",
            ReferencedTableSchema = r["referenced_table_schema"]?.ToString() ?? "",
            ReferencedTableName   = r["referenced_table_name"]?.ToString()   ?? "",
            ReferencedColumnName  = r["referenced_column_name"]?.ToString()  ?? ""
        });

    public Task<SqlResult<List<IndexInfo>>> GetTableIndexesAsync(string database, string schema, string tableName) =>
        _sql.ExecuteQueryAsync<IndexInfo>($@"
            SELECT i.name AS index_name, i.type_desc AS index_type, i.is_unique AS is_unique,
                   COL_NAME(ic.object_id, ic.column_id) AS column_name, ic.key_ordinal AS ordinal_position
            FROM [{database}].sys.indexes i
            INNER JOIN [{database}].sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            WHERE OBJECT_SCHEMA_NAME(i.object_id, DB_ID('{database}')) = '{schema}'
              AND OBJECT_NAME(i.object_id, DB_ID('{database}')) = '{tableName}'
              AND i.type > 0
            ORDER BY i.name, ic.key_ordinal",
        r => new IndexInfo
        {
            IndexName       = r["index_name"]?.ToString()                             ?? "",
            IndexType       = r["index_type"]?.ToString()                             ?? "",
            IsUnique        = r["is_unique"] is bool b ? b : r["is_unique"]?.ToString() == "True",
            ColumnName      = r["column_name"]?.ToString()                            ?? "",
            OrdinalPosition = r["ordinal_position"] is int o ? o : 0
        });

    public async Task<List<string>> GetTableKeyColumnsAsync(string database, string schema, string tableName)
    {
        var pk = await GetPrimaryKeysAsync(database, schema, tableName);
        return pk.Success && pk.Data is not null
            ? pk.Data.Select(p => p.ColumnName).ToList()
            : new List<string>();
    }

    public async Task<List<string>> GetIdentityColumnsAsync(string database, string schema, string tableName)
    {
        var result = await _sql.ExecuteQueryAsync<string>($@"
            SELECT c.name
            FROM [{database}].sys.columns c
            JOIN [{database}].sys.tables t ON c.object_id = t.object_id
            JOIN [{database}].sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = '{schema}' AND t.name = '{tableName}' AND c.is_identity = 1",
            r => r[0]?.ToString() ?? "");
        return result.Success && result.Data is not null ? result.Data : new List<string>();
    }

    public async Task<string> GetTableDdlAsync(string database, string schema, string tableName)
    {
        var colsResult = await GetTableColumnsAsync(database, schema, tableName);
        if (!colsResult.Success || colsResult.Data is null) return "";
        var pkResult = await GetPrimaryKeysAsync(database, schema, tableName);

        var sb = new StringBuilder($"CREATE TABLE [{schema}].[{tableName}] (\n");
        var defs = colsResult.Data.Select(col =>
        {
            var def = new StringBuilder($"  [{col.ColumnName}] {col.DataType.ToUpperInvariant()}");
            if (col.CharacterMaximumLength.HasValue && col.CharacterMaximumLength > 0)
                def.Append($"({col.CharacterMaximumLength})");
            def.Append(col.IsNullable == "NO" ? " NOT NULL" : " NULL");
            if (!string.IsNullOrEmpty(col.ColumnDefault))
                def.Append($" DEFAULT {col.ColumnDefault}");
            return def.ToString();
        }).ToList();

        sb.Append(string.Join(",\n", defs));
        if (pkResult.Success && pkResult.Data?.Count > 0)
        {
            var pkCols = string.Join(", ", pkResult.Data.Select(p => $"[{p.ColumnName}]"));
            sb.Append($",\n  CONSTRAINT [{pkResult.Data[0].ConstraintName}] PRIMARY KEY ({pkCols})");
        }
        sb.AppendLine("\n)");
        return sb.ToString();
    }

    public async Task<string> GenerateInsertScriptAsync(string database, string schema, string tableName, int maxRows = 1000)
    {
        var dataResult = await _sql.ExecuteQueryAsync($"SELECT TOP {maxRows} * FROM [{database}].[{schema}].[{tableName}]");
        if (!dataResult.Success || dataResult.Data is null || dataResult.Data.Count == 0) return "-- No data found";

        var colsResult = await GetTableColumnsAsync(database, schema, tableName);
        if (!colsResult.Success || colsResult.Data is null) return "-- Could not retrieve column information";

        var pkResult   = await GetPrimaryKeysAsync(database, schema, tableName);
        var pks        = pkResult.Success && pkResult.Data is not null
            ? pkResult.Data.Select(p => p.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cols = colsResult.Data;
        var sb   = new StringBuilder();
        sb.AppendLine($"-- INSERT script for {schema}.{tableName}");
        sb.AppendLine($"-- Generated: {DateTime.Now:O}");
        sb.AppendLine($"-- Rows: {dataResult.Data.Count}");
        sb.AppendLine();

        foreach (var row in dataResult.Data)
        {
            if (pks.Count > 0)
            {
                var where = string.Join(" AND ", cols.Where(c => pks.Contains(c.ColumnName)).Select(c =>
                {
                    var v = row.GetValueOrDefault(c.ColumnName);
                    return $"{c.ColumnName} {SqlService.SqlFmtFieldWhere(v, SqlService.MapSqlDataType(c.DataType))}";
                }));
                sb.AppendLine($"IF NOT EXISTS( SELECT 1 FROM {schema}.{tableName} WHERE {where} )");
                sb.AppendLine("BEGIN");
            }
            var colNames = string.Join(", ", cols.Select(c => c.ColumnName));
            var values   = string.Join(", ", cols.Select(c =>
                SqlService.SqlFmtField(row.GetValueOrDefault(c.ColumnName), SqlService.MapSqlDataType(c.DataType))));
            string p = pks.Count > 0 ? "\t" : "";
            sb.AppendLine($"{p}INSERT INTO {schema}.{tableName} ({colNames})");
            sb.AppendLine($"{p}SELECT {values}");
            if (pks.Count > 0) sb.AppendLine("END");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
