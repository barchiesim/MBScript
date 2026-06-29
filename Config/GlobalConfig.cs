using Microsoft.Extensions.Configuration;
using MBScript.Models;

namespace MBScript.Config;

public sealed class GlobalConfig
{
    private static GlobalConfig? _instance;
    private static readonly object _lock = new();

    public DatabaseConfig DbConfig { get; private set; }

    public string DbNome  { get; set; } = "";
    public string Filtro  { get; set; } = "";
    public string Escludi { get; set; } = "";

    private GlobalConfig()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        DbConfig = new DatabaseConfig
        {
            Server            = config["Database:Server"]   ?? Environment.GetEnvironmentVariable("SQL_SERVER")   ?? "localhost",
            User              = config["Database:User"]     ?? Environment.GetEnvironmentVariable("SQL_USER")     ?? "",
            Password          = config["Database:Password"] ?? Environment.GetEnvironmentVariable("SQL_PASSWORD") ?? "",
            Database          = config["Database:Database"] ?? Environment.GetEnvironmentVariable("SQL_DATABASE") ?? "master",
            IntegratedSecurity = bool.TryParse(
                config["Database:IntegratedSecurity"] ?? Environment.GetEnvironmentVariable("INTEGRATED_SECURITY"),
                out var intSec) && intSec
        };
    }

    public static GlobalConfig Instance
    {
        get
        {
            if (_instance is null)
                lock (_lock) { _instance ??= new GlobalConfig(); }
            return _instance;
        }
    }

    public void SetDatabaseConfig(DatabaseConfig config) => DbConfig = config;
}
