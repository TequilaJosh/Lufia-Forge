using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LufiaForge.Modules.Emulator;

/// <summary>
/// Persistent application settings stored in %AppData%\LufiaForge\settings.json.
/// </summary>
public class AppSettings
{
    private static readonly string SettingsDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------
    public string Snes9xPath      { get; set; } = "";
    public string LastRomPath     { get; set; } = "";
    public int    MemoryPollRateMs{ get; set; } = 33;   // ~30 fps
    public bool   AutoLoadRom     { get; set; } = false;

    // -------------------------------------------------------------------------
    // Singleton load / save
    // -------------------------------------------------------------------------
    private static AppSettings? _instance;

    public static AppSettings Load()
    {
        if (_instance != null) return _instance;

        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                _instance = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                return _instance;
            }
        }
        catch { /* corrupt file — fall through to defaults */ }

        _instance = new AppSettings();
        return _instance;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* best-effort */ }
    }
}
