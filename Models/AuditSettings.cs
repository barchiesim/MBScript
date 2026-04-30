namespace PBScriptNew.Models;

public class AuditSettings
{
    public string AuditFilter { get; set; } = "and (SUBSTRING(name,3,1) = '_')";
    public string AuditExclude { get; set; } = "and name NOT IN ('NUMBERS', 'FW_ASYNC_SCHEDULER', 'FW_PROCESSING_DETAILS')";
    // Persisted UI settings
    public string TableSearch { get; set; } = string.Empty;
    public bool DefaultConditionalUpdate { get; set; } = false;
    // Last connected server/database
    public string LastServer { get; set; } = string.Empty;
    public string LastDatabase { get; set; } = string.Empty;
    // Last connected user
    public string LastUser { get; set; } = string.Empty;
}
