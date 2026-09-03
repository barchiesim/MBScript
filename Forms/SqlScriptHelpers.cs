using MBScript.Models;

namespace MBScript.Forms;

/// <summary>
/// Formattazione di valori e nomi SQL condivisa fra <see cref="MainForm"/> (generazione
/// script INSERT/UPDATE/DELETE) e <see cref="TableGridPanel"/> (editing diretto in griglia):
/// entrambi devono formattare i valori e le WHERE clause allo stesso modo.
/// </summary>
internal static class SqlScriptHelpers
{
    public static string FmtVal(object? val)
    {
        if (val is null || val == DBNull.Value) return "NULL";
        if (val is bool b) return b ? "1" : "0";
        if (val is DateTime dt) return $" {{ts '{dt:yyyy-MM-dd HH:mm:ss.fff}'}}";
        if (val is Guid g) return $"'{g}'";
        if (val is byte[] bytes) return "0x" + Convert.ToHexString(bytes);
        if (val is string s)
        {
            if (s.Length >= 10 && DateTime.TryParse(s, out var dtp))
                return $" {{ts '{dtp:yyyy-MM-dd HH:mm:ss.fff}'}}";
            return $"'{s.Replace("'", "''")}'";
        }
        return Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    public static string FullName(TableInfo t) =>
        t.TableSchema.ToLower() == "dbo"
            ? t.TableName
            : $"{t.TableSchema}.{t.TableName}";

    public static string WhereClause(List<string> keys, Dictionary<string, object?> row) =>
        string.Join(" AND ", keys.Select(c =>
            row.GetValueOrDefault(c) is null or DBNull
                ? $"{c} IS NULL"
                : $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));
}
