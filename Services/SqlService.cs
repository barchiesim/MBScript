using Microsoft.Data.SqlClient;
using MBScript.Models;

namespace MBScript.Services;

public class SqlService : IDisposable
{
    private SqlConnection? _connection;
    private bool _disposed;

    // ─── Connection ───────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(DatabaseConfig dbConfig)
    {
        try
        {
            await DisconnectAsync();
            var builder = new SqlConnectionStringBuilder
            {
                DataSource             = dbConfig.Server,
                InitialCatalog         = dbConfig.Database,
                Encrypt                = false,
                TrustServerCertificate = true,
                ConnectTimeout         = 30
            };
            if (dbConfig.IntegratedSecurity)
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID   = dbConfig.User;
                builder.Password = dbConfig.Password;
            }
            _connection = new SqlConnection(builder.ConnectionString);
            await _connection.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connection error: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    // ─── Query helpers ────────────────────────────────────────────────────────

    public async Task<SqlResult<List<T>>> ExecuteQueryAsync<T>(string query, Func<SqlDataReader, T> mapper)
    {
        try
        {
            EnsureConnected();
            await using var cmd    = new SqlCommand(query, _connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<T>();
            while (await reader.ReadAsync())
                results.Add(mapper(reader));
            return SqlResult<List<T>>.Ok(results);
        }
        catch (Exception ex) { return SqlResult<List<T>>.Fail(ex.Message); }
    }

    public async Task<SqlResult<List<Dictionary<string, object?>>>> ExecuteQueryAsync(string query)
    {
        try
        {
            EnsureConnected();
            
            // Divide lo script in batch separati da GO
            var batches = SplitSqlBatches(query);
            var allResults = new List<Dictionary<string, object?>>();
            
            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                
                await using var cmd = new SqlCommand(batch, _connection);
                cmd.CommandTimeout = 300; // 5 minuti per batch lunghi
                
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    allResults.Add(row);
                }
            }
            
            return SqlResult<List<Dictionary<string, object?>>>.Ok(allResults);
        }
        catch (Exception ex) { return SqlResult<List<Dictionary<string, object?>>>.Fail(ex.Message); }
    }
    
    private static List<string> SplitSqlBatches(string script)
    {
        var batches = new List<string>();
        var lines = script.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        var currentBatch = new System.Text.StringBuilder();
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // GO deve essere su una riga da solo (ignora case e spazi)
            if (trimmedLine.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (currentBatch.Length > 0)
                {
                    batches.Add(currentBatch.ToString());
                    currentBatch.Clear();
                }
            }
            else
            {
                currentBatch.AppendLine(line);
            }
        }
        
        // Aggiungi l'ultimo batch se c'è
        if (currentBatch.Length > 0)
            batches.Add(currentBatch.ToString());
        
        return batches;
    }

    public async Task<SqlResult<int>> ExecuteCommandAsync(string command)
    {
        try
        {
            EnsureConnected();
            await using var cmd = new SqlCommand(command, _connection);
            int rows = await cmd.ExecuteNonQueryAsync();
            return SqlResult<int>.Ok(rows, rows);
        }
        catch (Exception ex) { return SqlResult<int>.Fail(ex.Message); }
    }

    public async Task<SqlResult<int>> ExecuteScriptAsync(string script)
    {
        try
        {
            EnsureConnected();
            var batches = SplitSqlBatches(script);
            int total = 0;
            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await using var cmd = new SqlCommand(batch, _connection);
                cmd.CommandTimeout = 600;
                total += await cmd.ExecuteNonQueryAsync();
            }
            return SqlResult<int>.Ok(total, total);
        }
        catch (Exception ex) { return SqlResult<int>.Fail(ex.Message); }
    }

    public async Task<ServerInfo?> GetServerInfoAsync()
    {
        const string q = @"SELECT @@SERVERNAME AS ServerName,
            SERVERPROPERTY('ProductVersion') AS ProductVersion,
            SERVERPROPERTY('ProductLevel')   AS ServicePack,
            SERVERPROPERTY('Edition')        AS Edition";
        var r = await ExecuteQueryAsync<ServerInfo>(q, reader => new ServerInfo
        {
            ServerName     = reader["ServerName"]?.ToString()     ?? "",
            ProductVersion = reader["ProductVersion"]?.ToString() ?? "",
            ServicePack    = reader["ServicePack"]?.ToString()    ?? "",
            Edition        = reader["Edition"]?.ToString()        ?? ""
        });
        return r.Success && r.Data?.Count > 0 ? r.Data[0] : null;
    }

    // ─── Static formatting helpers (equivalent to modSql.bas) ────────────────

    public static string SqlFmt(object? val)
    {
        if (val is null || val == DBNull.Value || (val is string s && string.IsNullOrEmpty(s)))
            return "null";
        return $"'{val.ToString()!.Replace("'", "''")}'";
    }

    public static string SqlFmtName(string name) => name.Contains(' ') ? $"[{name}]" : name;

    public static bool SqlIsEqual(object? v1, object? v2)
    {
        bool n1 = v1 is null || v1 == DBNull.Value;
        bool n2 = v2 is null || v2 == DBNull.Value;
        if (n1 && n2) return true;
        if (n1 || n2) return false;
        return v1!.Equals(v2);
    }

    public static T Nvl<T>(T? val, T def)
    {
        if (val is null) return def;
        if (val is string str && string.IsNullOrWhiteSpace(str)) return def;
        return val;
    }

    public static string SqlFmtField(object? value, SqlDataType dataType)
    {
        if (value is null || value == DBNull.Value) return "NULL";
        switch (dataType)
        {
            case SqlDataType.Decimal: case SqlDataType.BigInt: case SqlDataType.Currency:
            case SqlDataType.Double:  case SqlDataType.Integer: case SqlDataType.Numeric:
            case SqlDataType.Single:  case SqlDataType.SmallInt: case SqlDataType.TinyInt:
            case SqlDataType.UnsignedBigInt: case SqlDataType.UnsignedInt:
            case SqlDataType.UnsignedSmallInt: case SqlDataType.UnsignedTinyInt:
            case SqlDataType.VarNumeric:
                var numStr = value.ToString()!.Replace(',', '.');
                return double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";

            case SqlDataType.Char: case SqlDataType.LongVarChar: case SqlDataType.LongVarWChar:
            case SqlDataType.VarChar: case SqlDataType.VarWChar: case SqlDataType.WChar:
                return $"'{value.ToString()!.Replace("'", "''")}'";

            case SqlDataType.Date: case SqlDataType.DBDate:
            case SqlDataType.DBTime: case SqlDataType.DBTimeStamp:
                if (value is DateTime dt) return $" {{ts '{dt:yyyy-MM-dd HH:mm:ss.fff}'}}";
                if (DateTime.TryParse(value.ToString(), out var dtP))
                    return $" {{ts '{dtP:yyyy-MM-dd HH:mm:ss.fff}'}}";
                return "NULL";

            case SqlDataType.Boolean:
                if (value is bool b) return b ? "1" : "0";
                var bs = value.ToString()!.ToLowerInvariant();
                return (bs == "true" || bs == "1") ? "1" : "0";

            case SqlDataType.Binary: case SqlDataType.VarBinary: case SqlDataType.LongVarBinary:
                if (value is byte[] bytes) return "0x" + Convert.ToHexString(bytes);
                return "NULL";

            default:
                return $"'{value.ToString()!.Replace("'", "''")}'";
        }
    }

    public static string SqlFmtFieldWhere(object? value, SqlDataType dataType)
    {
        var fmt = SqlFmtField(value, dataType);
        return (fmt == "NULL" || string.IsNullOrEmpty(fmt)) ? "IS NULL" : $"= {fmt}";
    }

    public static SqlDataType MapSqlDataType(string sqlType) => sqlType.ToLowerInvariant() switch
    {
        "tinyint"                                                        => SqlDataType.TinyInt,
        "smallint"                                                       => SqlDataType.SmallInt,
        "int"                                                            => SqlDataType.Integer,
        "bigint"                                                         => SqlDataType.BigInt,
        "decimal" or "numeric"                                           => SqlDataType.Decimal,
        "money" or "smallmoney"                                          => SqlDataType.Currency,
        "float"                                                          => SqlDataType.Double,
        "real"                                                           => SqlDataType.Single,
        "char"                                                           => SqlDataType.Char,
        "varchar"                                                        => SqlDataType.VarChar,
        "text"                                                           => SqlDataType.LongVarChar,
        "nchar"                                                          => SqlDataType.WChar,
        "nvarchar"                                                       => SqlDataType.VarWChar,
        "ntext"                                                          => SqlDataType.LongVarWChar,
        "date"                                                           => SqlDataType.DBDate,
        "time"                                                           => SqlDataType.DBTime,
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => SqlDataType.DBTimeStamp,
        "binary"                                                         => SqlDataType.Binary,
        "varbinary"                                                      => SqlDataType.VarBinary,
        "image"                                                          => SqlDataType.LongVarBinary,
        "timestamp" or "rowversion"                                      => SqlDataType.Binary,
        "bit"                                                            => SqlDataType.Boolean,
        "uniqueidentifier"                                               => SqlDataType.GUID,
        "xml"                                                            => SqlDataType.LongVarWChar,
        "sql_variant"                                                    => SqlDataType.Variant,
        _                                                                => SqlDataType.VarChar
    };

    // ─── Internals ────────────────────────────────────────────────────────────

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected to database");
    }

    public void Dispose()
    {
        if (!_disposed) { _connection?.Dispose(); _disposed = true; }
    }
}
