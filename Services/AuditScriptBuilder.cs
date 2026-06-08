using System.Text;
using PBScriptNew.Models;

namespace PBScriptNew.Services;

public static class AuditScriptBuilder
{
    public static string BuildInitScript(string sourceDb, string auditDb, IDictionary<string, List<ColumnInfo>> tableColumns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Script Inizializzazione Database Audit");
        sb.AppendLine($"-- Database origine: {sourceDb}  →  Audit: {auditDb}");
        sb.AppendLine($"-- Data: {DateTime.Now}  Tabelle: {tableColumns.Count}");
        sb.AppendLine();
        sb.AppendLine("USE master;");
        sb.AppendLine($"IF EXISTS (SELECT * FROM sys.databases WHERE name = '{auditDb}')");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    ALTER DATABASE [{auditDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        sb.AppendLine($"    DROP DATABASE [{auditDb}];");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        sb.AppendLine($"CREATE DATABASE [{auditDb}];");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine($"USE [{sourceDb}];");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine("DECLARE @sql NVARCHAR(MAX) = '';");
        sb.AppendLine("SELECT @sql = @sql + 'DROP TRIGGER ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(tr.name) + '; '");
        sb.AppendLine("FROM sys.triggers tr INNER JOIN sys.tables t ON tr.parent_id = t.object_id WHERE tr.name LIKE 'tr_AUDIT%';");
        sb.AppendLine("IF @sql <> '' BEGIN EXEC sp_executesql @sql; END");
        sb.AppendLine("GO");
        sb.AppendLine();
        foreach (var (tbl, cols) in tableColumns)
        {
            AppendTrigger(sb, tbl, auditDb, "INSERT", "I", "NEW", "inserted", cols);
            AppendTriggerUpdate(sb, tbl, auditDb, cols);
            AppendTrigger(sb, tbl, auditDb, "DELETE", "D", "OLD", "deleted", cols);
        }
        sb.AppendLine($"PRINT 'Inizializzazione completata: {tableColumns.Count * 3} trigger creati'");
        return sb.ToString();
    }

    public static string BuildRemoveScript(string sourceDb)
    {
        var auditDb = $"{sourceDb}_UPD";
        return $@"-- Rimozione Sistema Audit  {DateTime.Now}
USE {sourceDb};
DECLARE @sql NVARCHAR(MAX) = '';
SELECT @sql = @sql + 'DROP TRIGGER ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(tr.name) + '; '
FROM sys.triggers tr INNER JOIN sys.tables t ON tr.parent_id = t.object_id WHERE tr.name LIKE 'tr_AUDIT%';
IF @sql <> '' BEGIN EXEC sp_executesql @sql; END
GO
USE master;
IF EXISTS (SELECT * FROM sys.databases WHERE name = '{auditDb}')
BEGIN
    ALTER DATABASE [{auditDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{auditDb}];
END
PRINT 'Sistema di audit rimosso';
";
    }

    public static string BuildActivateScript(string sourceDb) => $@"-- Attivazione Trigger Audit  {DateTime.Now}
USE [{sourceDb}];
GO
DECLARE @sql NVARCHAR(MAX) = '';
SELECT @sql = @sql + 'ENABLE TRIGGER ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(tr.name)
    + ' ON ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name) + '; '
FROM sys.triggers tr INNER JOIN sys.tables t ON tr.parent_id = t.object_id
WHERE tr.name LIKE 'tr_AUDIT%' AND tr.is_disabled = 1;
IF @sql <> '' BEGIN EXEC sp_executesql @sql; PRINT 'Trigger attivati'; END
ELSE PRINT 'Nessun trigger da attivare';
GO
";

    public static string BuildDeactivateScript(string sourceDb) => $@"-- Disattivazione Trigger Audit  {DateTime.Now}
USE [{sourceDb}];
GO
PRINT 'Inizio - Trigger disattivati'
DECLARE @sql NVARCHAR(MAX) = '';
SELECT @sql = @sql + 'DISABLE TRIGGER ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(tr.name)
    + ' ON ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name) + '; '
FROM sys.triggers tr INNER JOIN sys.tables t ON tr.parent_id = t.object_id
WHERE tr.name LIKE 'tr_AUDIT%' AND tr.is_disabled = 0;
IF @sql <> '' BEGIN EXEC sp_executesql @sql; PRINT 'Trigger disattivati'; END
ELSE PRINT 'Nessun trigger da disattivare';
PRINT 'Fine - Trigger disattivati'
GO
";

    private static (string selectTop, string colList) BuildColExpressions(List<ColumnInfo> columns)
    {
        var cols = columns.Where(c => c.DataType != "timestamp" && c.DataType != "rowversion").ToList();
        var selectTop = string.Join(", ", cols.Select(c =>
            c.IsIdentity
                ? $"CAST([{c.ColumnName}] AS {c.DataType.ToUpper()}) AS [{c.ColumnName}]"
                : $"[{c.ColumnName}]"));
        var colList = string.Join(", ", cols.Select(c => $"[{c.ColumnName}]"));
        return (selectTop, colList);
    }

    private static void AppendTrigger(StringBuilder sb, string tbl, string auditDb, string dmlEvent, string tipoCmd, string tipoDato, string source, List<ColumnInfo> columns)
    {
        var (selectTop, colList) = BuildColExpressions(columns);
        sb.AppendLine($"CREATE TRIGGER tr_AUDIT_{tbl}_{dmlEvent} ON [{tbl}] AFTER {dmlEvent} AS");
        sb.AppendLine("BEGIN");
        sb.AppendLine("    SET NOCOUNT ON;");
        sb.AppendLine("    DECLARE @dt DATETIME, @guid UNIQUEIDENTIFIER, @host VARCHAR(254), @user VARCHAR(254), @app VARCHAR(254);");
        sb.AppendLine("    SET @dt=GETDATE(); SET @guid=NEWID(); SET @host=HOST_NAME(); SET @user=SYSTEM_USER; SET @app=APP_NAME();");
        sb.AppendLine($"    SELECT TOP 0 {selectTop} INTO #src FROM [{tbl}];");
        sb.AppendLine($"    INSERT INTO #src SELECT {colList} FROM {source};");
        sb.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM {auditDb}.dbo.sysobjects WHERE name = '{tbl}')");
        sb.AppendLine($"        SELECT CAST('{tipoCmd}' AS CHAR(1)) AS dba_tipo_comando, CAST('{tipoDato}' AS CHAR(3)) AS dba_tipo_dato, @host AS dba_macchina, @user AS dba_utente, @dt AS dba_data, @app AS dba_applicazione, @guid AS dba_guid, CAST(1 AS INT) AS dba_progupd, {colList} INTO {auditDb}..[{tbl}] FROM #src;");
        sb.AppendLine("    ELSE");
        sb.AppendLine($"        INSERT INTO {auditDb}..[{tbl}] (dba_tipo_comando, dba_tipo_dato, dba_macchina, dba_utente, dba_data, dba_applicazione, dba_guid, dba_progupd, {colList}) SELECT CAST('{tipoCmd}' AS CHAR(1)), CAST('{tipoDato}' AS CHAR(3)), @host, @user, @dt, @app, @guid, 1, {colList} FROM #src;");
        sb.AppendLine("    DROP TABLE #src;");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        sb.AppendLine();
    }

    private static void AppendTriggerUpdate(StringBuilder sb, string tbl, string auditDb, List<ColumnInfo> columns)
    {
        var (selectTop, colList) = BuildColExpressions(columns);
        sb.AppendLine($"CREATE TRIGGER tr_AUDIT_{tbl}_UPDATE ON [{tbl}] AFTER UPDATE AS");
        sb.AppendLine("BEGIN");
        sb.AppendLine("    SET NOCOUNT ON;");
        sb.AppendLine("    DECLARE @dt DATETIME, @guid UNIQUEIDENTIFIER, @host VARCHAR(254), @user VARCHAR(254), @app VARCHAR(254);");
        sb.AppendLine("    SET @dt=GETDATE(); SET @guid=NEWID(); SET @host=HOST_NAME(); SET @user=SYSTEM_USER; SET @app=APP_NAME();");
        sb.AppendLine($"    SELECT TOP 0 {selectTop} INTO #ins FROM [{tbl}];");
        sb.AppendLine($"    INSERT INTO #ins SELECT {colList} FROM inserted;");
        sb.AppendLine($"    SELECT TOP 0 {selectTop} INTO #del FROM [{tbl}];");
        sb.AppendLine($"    INSERT INTO #del SELECT {colList} FROM deleted;");
        sb.AppendLine($"    IF NOT EXISTS (SELECT 1 FROM {auditDb}.dbo.sysobjects WHERE name = '{tbl}')");
        sb.AppendLine("    BEGIN");
        sb.AppendLine($"        SELECT CAST('U' AS CHAR(1)) AS dba_tipo_comando, CAST('OLD' AS CHAR(3)) AS dba_tipo_dato, @host AS dba_macchina, @user AS dba_utente, @dt AS dba_data, @app AS dba_applicazione, @guid AS dba_guid, CAST(1 AS INT) AS dba_progupd, {colList} INTO {auditDb}..[{tbl}] FROM #del;");
        sb.AppendLine($"        INSERT INTO {auditDb}..[{tbl}] (dba_tipo_comando, dba_tipo_dato, dba_macchina, dba_utente, dba_data, dba_applicazione, dba_guid, dba_progupd, {colList}) SELECT CAST('U' AS CHAR(1)), CAST('NEW' AS CHAR(3)), @host, @user, @dt, @app, @guid, 1, {colList} FROM #ins;");
        sb.AppendLine("    END");
        sb.AppendLine("    ELSE");
        sb.AppendLine($"        INSERT INTO {auditDb}..[{tbl}] (dba_tipo_comando, dba_tipo_dato, dba_macchina, dba_utente, dba_data, dba_applicazione, dba_guid, dba_progupd, {colList}) SELECT CAST('U' AS CHAR(1)), CAST('OLD' AS CHAR(3)), @host, @user, @dt, @app, @guid, 1, {colList} FROM #del UNION ALL SELECT CAST('U' AS CHAR(1)), CAST('NEW' AS CHAR(3)), @host, @user, @dt, @app, @guid, 1, {colList} FROM #ins;");
        sb.AppendLine("    DROP TABLE #ins; DROP TABLE #del;");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        sb.AppendLine();
    }
}
