using System.Text.Json;
using MBScript.Models;

namespace MBScript.Services;

public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MBScript",
        "settings.json"
    );

    public static AuditSettings LoadAuditSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AuditSettings>(json) ?? new AuditSettings();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore caricamento impostazioni: {ex.Message}");
        }
        
        return new AuditSettings();
    }

    public static void SaveAuditSettings(AuditSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (directory != null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore salvataggio impostazioni: {ex.Message}");
        }
    }
}
