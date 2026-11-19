using System.IO;
using System.Text.Json;

namespace LufiaForge.Core;

// lufia forge's own settings, the same for every project (Edit, Settings). kept in %APPDATA%\LufiaForge\settings.json
public static class AppSettings
{
    private sealed class Data
    {
        public bool Snes9x151Mode { get; set; } = true;
    }

    private static Data? _data;
    private static Data Settings => _data ??= Load();

    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "settings.json");

    // snes9x 1.51 mode, on unless turned off. on an 8 MB lufia 2 rom nothing goes in the top 2 MB (banks $40-$7D, file
    // 0x600000 and up) because snes9x 1.51 and 1.52 can't read it (they show the 2 MB before it there again), and
    // whatever an older lufia forge put there gets moved when a project opens.
    // off = data can go there again (2 MB more room), for hacks only played on emulators that read it
    // (a current snes9x, bsnes, bizhawk)
    public static bool Snes9x151Mode
    {
        get => Settings.Snes9x151Mode;
        set { Settings.Snes9x151Mode = value; Lufia2.L2Rom.Snes9x151Mode = value; Save(); }
    }

    // hands the settings to the parts of lufia forge that use them, once at start
    public static void Init() => Lufia2.L2Rom.Snes9x151Mode = Settings.Snes9x151Mode;

    private static Data Load()
    {
        try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<Data>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception) { }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
