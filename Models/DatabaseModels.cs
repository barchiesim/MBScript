namespace PBScriptNew.Models;

public class DatabaseInfo
{
    public string Name { get; set; } = "";
    public int DatabaseId { get; set; }
    public DateTime CreateDate { get; set; }
    public string StateDesc { get; set; } = "";
}

public class TableInfo
{
    public string TableSchema { get; set; } = "";
    public string TableName { get; set; } = "";
    public string TableType { get; set; } = "";
}

public class ColumnInfo
{
    public string ColumnName { get; set; } = "";
    public string DataType { get; set; } = "";
    public int? CharacterMaximumLength { get; set; }
    public string IsNullable { get; set; } = "YES";
    public string? ColumnDefault { get; set; }
    public int OrdinalPosition { get; set; }
    public bool IsIdentity { get; set; }
}

public class PrimaryKeyInfo
{
    public string ConstraintName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public int OrdinalPosition { get; set; }
}

public class ForeignKeyInfo
{
    public string ConstraintName { get; set; } = "";
    public string ColumnName { get; set; } = "";
    public string ReferencedTableSchema { get; set; } = "";
    public string ReferencedTableName { get; set; } = "";
    public string ReferencedColumnName { get; set; } = "";
}

public class IndexInfo
{
    public string IndexName { get; set; } = "";
    public string IndexType { get; set; } = "";
    public bool IsUnique { get; set; }
    public string ColumnName { get; set; } = "";
    public int OrdinalPosition { get; set; }
}

public class ServerInfo
{
    public string ServerName { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string ServicePack { get; set; } = "";
    public string Edition { get; set; } = "";
}

public class ScriptGenerationOptions
{
    public bool IncludeSchema { get; set; } = true;
    public bool IncludeIfExists { get; set; } = true;
    public bool IncludeConstraints { get; set; } = true;
    public bool IncludeForeignKeys { get; set; } = true;
    public bool IncludeIndexes { get; set; } = true;
    public bool FormatOutput { get; set; } = true;
    public int BatchSize { get; set; } = 1000;
}
