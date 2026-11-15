using System.IO;
using System.Text.Json;

namespace LufiaForge.Core;

/// <summary>
/// Lufia Forge's own settings, the same for every project (Edit, Settings): kept in %APPDATA%\LufiaForge\settings.json.
/// </summary>
public static class AppSettings
{
    private sealed class Data
    {
        public bool Snes9x151Mode { get; set; } = true;
    }

    private static Data? _data;
    private static Data D => _data ??= Load();

    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "settings.json");

    /// <summary>
    /// Snes9x 1.51 mode (on unless turned off): on an 8 MB Lufia II ROM nothing goes in the top 2 MB (banks $40-$7D, file
    /// 0x600000 and up), which Snes9x 1.51 and 1.52 can't read (they show the 2 MB before it there again), and what an
    /// older Lufia Forge put there is moved when a project opens. Off: data may go there again (2 MB more room), for
    /// hacks only played on emulators that read it (a current Snes9x, bsnes, BizHawk).
    /// </summary>
    public static bool Snes9x151Mode
    {
        get => D.Snes9x151Mode;
        set { D.Snes9x151Mode = value; Lufia2.L2Rom.Snes9x151Mode = value; Save(); }
    }

    /// <summary>Hands the settings to the parts of Lufia Forge that use them (once, at start).</summary>
    public static void Init() => Lufia2.L2Rom.Snes9x151Mode = D.Snes9x151Mode;

    private static Data Load()
    {
        try { if (File.Exists(File_)) return JsonSerializer.Deserialize<Data>(File.ReadAllText(File_)) ?? new(); }
        catch (Exception) { }
        return new();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(D, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
