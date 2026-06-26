namespace MBScript.Models;

public class DatabaseConfig
{
    public string Server { get; set; } = "localhost";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "master";
    public bool IntegratedSecurity { get; set; } = false;
}
