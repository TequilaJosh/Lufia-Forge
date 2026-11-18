using System.IO;
using System.Text.Json;

namespace LufiaForge.Core;

// the rom files we write next to a project for bizhawk (play copies), and the check that writing one never
// overwrites a file that isn't one (like the rom the project started from, when the project got saved next to it
// under the same name). every play copy we write is remembered (path, size and crc32, in
// %APPDATA%\LufiaForge\play_copies.json). a file at the project's play path that isn't remembered, or changed since,
// is someone else's and we leave it alone, the copy goes to "<project>.play.sfc" then.
// a play copy written before we remembered them still counts as one if it can't be anything else: no copier header,
// written after the project was made, and not the project's starting rom (bizhawk keys its in-game saves to the rom's
// name, so those projects keep playing the same file)
public static class PlayCopies
{
    private sealed record Entry(string Path, long Length, uint Crc);

    private const int Max = 200;

    // where the list is kept (tests point it somewhere else)
    public static string StoreFile { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "play_copies.json");

    // the file a project's play copy goes to: preferred (<project>.sfc) when nothing is there or it's the project's play
    // copy, otherwise the first "<project>.play.sfc" (".play-2.sfc", ...) that is
    public static string Target(string preferred, byte[] baseRom, DateTime projectCreated)
    {
        if (IsPlayCopy(preferred, baseRom, projectCreated)) return preferred;
        string folder = System.IO.Path.GetDirectoryName(preferred) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(preferred), extension = System.IO.Path.GetExtension(preferred);
        for (int copyNumber = 1; ; copyNumber++)
        {
            string path = System.IO.Path.Combine(folder, $"{name}.play{(copyNumber > 1 ? "-" + copyNumber : "")}{extension}");
            if (!File.Exists(path) || IsRemembered(path)) return path;
        }
    }

    // whether we can write the play copy to path (see the notes at the top)
    public static bool IsPlayCopy(string path, byte[] baseRom, DateTime projectCreated)
    {
        try
        {
            if (!File.Exists(path)) return true;
            if (Find(path) != null) return IsRemembered(path);   // remembered but changed since, not ours anymore
            var info = new FileInfo(path);
            if (info.Length % 1024 == 512 || info.LastWriteTime < projectCreated) return false;
            return !File.ReadAllBytes(path).AsSpan().SequenceEqual(baseRom);
        }
        catch { return false; }
    }

    // the file is a play copy we wrote and nothing changed it since
    public static bool IsRemembered(string path)
    {
        try
        {
            var entry = Find(path);
            if (entry == null || !File.Exists(path)) return false;
            var data = File.ReadAllBytes(path);
            return data.LongLength == entry.Length && CommunityPatches.Crc32(data) == entry.Crc;
        }
        catch { return false; }
    }

    // remembers the file we just wrote as a play copy
    public static void Remember(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            string fullPath = System.IO.Path.GetFullPath(path);
            var list = Load().Where(e => !string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Insert(0, new Entry(fullPath, data.LongLength, CommunityPatches.Crc32(data)));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StoreFile)!);
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(list.Take(Max).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static Entry? Find(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        return Load().FirstOrDefault(e => string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));
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
