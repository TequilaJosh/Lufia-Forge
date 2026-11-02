using System.IO;
using System.Text.Json;

namespace LufiaForge.Core;

/// <summary>
/// The ROM files Lufia Forge writes next to a project for BizHawk (play copies), and the check that writing one never
/// overwrites a file that isn't one: the ROM the project started from, say, when the project was saved next to it under
/// the same name. Every play copy written is remembered (path, size and CRC32, in %APPDATA%\LufiaForge\play_copies.json).
/// A file at the project's play path that isn't remembered, or changed since, is someone else's and is left alone: the
/// copy goes to "&lt;project&gt;.play.sfc" then. A play copy written before Lufia Forge remembered them still counts as
/// one when it can't be anything else: no copier header, written after the project was made, and not the project's
/// starting ROM (BizHawk keys its in-game saves to the ROM's name, so those projects keep playing the same file).
/// </summary>
public static class PlayCopies
{
    private sealed record Entry(string Path, long Length, uint Crc);

    private const int Max = 200;

    /// <summary>Where the list is kept (tests point it somewhere else).</summary>
    public static string StoreFile { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "play_copies.json");

    /// <summary>The file a project's play copy goes to: <paramref name="preferred"/> (&lt;project&gt;.sfc) when nothing
    /// is there or it is the project's play copy, else the first "&lt;project&gt;.play.sfc" (".play-2.sfc", ...) that is.</summary>
    public static string Target(string preferred, byte[] baseRom, DateTime projectCreated)
    {
        if (IsPlayCopy(preferred, baseRom, projectCreated)) return preferred;
        string dir = System.IO.Path.GetDirectoryName(preferred) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(preferred), ext = System.IO.Path.GetExtension(preferred);
        for (int n = 1; ; n++)
        {
            string path = System.IO.Path.Combine(dir, $"{name}.play{(n > 1 ? "-" + n : "")}{ext}");
            if (!File.Exists(path) || IsRemembered(path)) return path;
        }
    }

    /// <summary>Whether Lufia Forge may write the play copy to <paramref name="path"/> (see the class notes).</summary>
    public static bool IsPlayCopy(string path, byte[] baseRom, DateTime projectCreated)
    {
        try
        {
            if (!File.Exists(path)) return true;
            if (Find(path) != null) return IsRemembered(path);   // remembered, but changed since: not ours any more
            var info = new FileInfo(path);
            if (info.Length % 1024 == 512 || info.LastWriteTime < projectCreated) return false;
            return !File.ReadAllBytes(path).AsSpan().SequenceEqual(baseRom);
        }
        catch { return false; }
    }

    /// <summary>The file is a play copy Lufia Forge wrote and nothing changed it since.</summary>
    public static bool IsRemembered(string path)
    {
        try
        {
            var e = Find(path);
            if (e == null || !File.Exists(path)) return false;
            var data = File.ReadAllBytes(path);
            return data.LongLength == e.Length && CommunityPatches.Crc32(data) == e.Crc;
        }
        catch { return false; }
    }

    /// <summary>Remembers the file just written as a play copy.</summary>
    public static void Remember(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            string full = System.IO.Path.GetFullPath(path);
            var list = Load().Where(e => !string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, new Entry(full, data.LongLength, CommunityPatches.Crc32(data)));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StoreFile)!);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(list.Take(Max).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static Entry? Find(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        return Load().FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    private static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StoreFile)) ?? new();
        }
        catch { return new(); }
    }
}
