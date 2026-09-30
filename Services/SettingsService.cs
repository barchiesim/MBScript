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

    private static readonly string GridLayoutsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MBScript",
        "gridlayouts.json"
    );

    // Cache in memoria: evita di rileggere il file per ogni scheda tabella aperta
    // e di riscrivere per intero il dizionario ad ogni singola modifica di colonna.
    private static Dictionary<string, List<GridColumnLayout>>? _gridLayoutsCache;

    /// <summary>Disposizione delle colonne salvata per una tabella (schema.tabella,
    /// case-insensitive), o <c>null</c> se non è mai stata personalizzata.</summary>
    public static List<GridColumnLayout>? GetGridLayout(string tableKey)
    {
        _gridLayoutsCache ??= LoadGridLayoutsFromDisk();
        return _gridLayoutsCache.TryGetValue(tableKey, out List<GridColumnLayout>? layout) ? layout : null;
    }

    /// <summary>Salva la disposizione delle colonne di una tabella, sostituendo quella
    /// eventualmente già presente.</summary>
    public static void SaveGridLayout(string tableKey, List<GridColumnLayout> layout)
    {
        _gridLayoutsCache ??= LoadGridLayoutsFromDisk();
        _gridLayoutsCache[tableKey] = layout;

        try
        {
            string? directory = Path.GetDirectoryName(GridLayoutsPath);
            if (directory is not null && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(_gridLayoutsCache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GridLayoutsPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore salvataggio layout griglia: {ex.Message}");
        }
    }

    /// <summary>Elimina la disposizione salvata di una tabella: la prossima volta che
    /// viene aperta usa di nuovo l'ordine/larghezza predefiniti.</summary>
    public static void RemoveGridLayout(string tableKey)
    {
        _gridLayoutsCache ??= LoadGridLayoutsFromDisk();
        if (!_gridLayoutsCache.Remove(tableKey)) return;

        try
        {
            string json = JsonSerializer.Serialize(_gridLayoutsCache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GridLayoutsPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore salvataggio layout griglia: {ex.Message}");
        }
    }

    private static Dictionary<string, List<GridColumnLayout>> LoadGridLayoutsFromDisk()
    {
        try
        {
            if (File.Exists(GridLayoutsPath))
            {
                string json = File.ReadAllText(GridLayoutsPath);
                return JsonSerializer.Deserialize<Dictionary<string, List<GridColumnLayout>>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new Dictionary<string, List<GridColumnLayout>>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore caricamento layout griglia: {ex.Message}");
        }

        return new Dictionary<string, List<GridColumnLayout>>(StringComparer.OrdinalIgnoreCase);
    }

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
