using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace LufiaForge.Core;

/// <summary>
/// A project: the ROM someone started from and what they changed in it, kept in one file (.lfproj, a zip).
/// <list type="bullet">
/// <item><c>project.json</c>: name, dates, the starting ROM's file name and checksum, the list of changes made, and the
/// project's own map names.</item>
/// <item><c>base.sfc</c>: the ROM as it was when the project started (so the project works without it).</item>
/// <item><c>changes.bin</c>: the bytes that differ from it now ("LFPD", the current length, then runs of
/// offset, length and bytes).</item>
/// </list>
/// Saving writes a temporary file and then swaps it in (the previous save is kept as .bak), so a failed save never
/// leaves a broken project.
/// </summary>
public sealed class LufiaProject
{
    public const string Extension = ".lfproj";
    private const int Format = 1;

    public string Path { get; private set; }
    public string Name { get; set; }
    public DateTime Created { get; private set; }
    public DateTime Saved { get; private set; }
    public string BaseRomFile { get; private set; } = "";
    public uint BaseCrc { get; private set; }
    /// <summary>What was changed, oldest first (from the edits' descriptions).</summary>
    public List<string> Changes { get; } = new();
    /// <summary>Map names given in this project (map number → name; empty = the automatic name even where a name is kept
    /// in %AppData% for every project). See <see cref="Maps.MapNames.ProjectNames"/>.</summary>
    public Dictionary<int, string> MapNames { get; } = new();
    /// <summary>A map was renamed since the last save (renames don't change the ROM).</summary>
    public bool MapNamesChanged { get; set; }

    private byte[] _base;

    private LufiaProject(string path, string name, byte[] baseRom)
    {
        Path = path; Name = name; _base = baseRom;
    }

    /// <summary>The folder new projects go in by default: Documents\Lufia Forge Projects.</summary>
    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lufia Forge Projects");

    /// <summary>Starts a project from a ROM (as loaded, header removed) and saves it.</summary>
    public static LufiaProject Create(string path, string name, byte[] rom, string romFile)
    {
        var p = new LufiaProject(path, name, (byte[])rom.Clone())
        {
            Created = DateTime.Now,
            BaseRomFile = System.IO.Path.GetFileName(romFile),
            BaseCrc = CommunityPatches.Crc32(rom),
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        p.Save(rom);
        return p;
    }

    /// <summary>Opens a project: returns it and the ROM as it was last saved.</summary>
    public static (LufiaProject Project, byte[] Rom) Open(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var info = JsonDocument.Parse(Read(zip, "project.json")).RootElement;
        int format = info.TryGetProperty("format", out var f) ? f.GetInt32() : 0;
        if (format > Format) throw new InvalidDataException("This project was saved by a newer Lufia Forge. Update the app to open it.");
        var p = new LufiaProject(path, info.GetProperty("name").GetString() ?? System.IO.Path.GetFileNameWithoutExtension(path), Read(zip, "base.sfc"))
        {
            Created = info.TryGetProperty("created", out var c) ? c.GetDateTime() : File.GetCreationTime(path),
            Saved = info.TryGetProperty("saved", out var s) ? s.GetDateTime() : File.GetLastWriteTime(path),
            BaseRomFile = info.TryGetProperty("baseRom", out var b) ? b.GetString() ?? "" : "",
            BaseCrc = info.TryGetProperty("baseCrc", out var k) ? k.GetUInt32() : 0,
        };
        if (info.TryGetProperty("changes", out var ch))
            foreach (var e in ch.EnumerateArray()) p.Changes.Add(e.GetString() ?? "");
        if (info.TryGetProperty("mapNames", out var mn) && mn.ValueKind == JsonValueKind.Object)
            foreach (var e in mn.EnumerateObject())
                if (int.TryParse(e.Name, System.Globalization.NumberStyles.HexNumber, null, out int id)) p.MapNames[id] = e.Value.GetString() ?? "";
        return (p, ApplyDiff(p._base, Read(zip, "changes.bin")));
    }

    /// <summary>Saves the project with the ROM as it is now.</summary>
    public void Save(byte[] rom)
    {
        Saved = DateTime.Now;
        string tmp = Path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = Format,
                name = Name,
                created = Created,
                saved = Saved,
                app = typeof(LufiaProject).Assembly.GetName().Version?.ToString(),
                baseRom = BaseRomFile,
                baseCrc = BaseCrc,
                changes = Changes,
                mapNames = MapNames.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("X2"), kv => kv.Value),
            }, new JsonSerializerOptions { WriteIndented = true });
            Write(zip, "project.json", json);
            Write(zip, "base.sfc", _base);
            Write(zip, "changes.bin", MakeDiff(_base, rom));
        }
        if (File.Exists(Path)) File.Replace(tmp, Path, Path + ".bak");
        else File.Move(tmp, Path);
        MapNamesChanged = false;
    }

    /// <summary>Saves the project under another file (Save Project As).</summary>
    public void SaveAs(string path, byte[] rom)
    {
        Path = path;
        Save(rom);
    }

    /// <summary>How many bytes differ from the starting ROM.</summary>
    public int ChangedBytes(byte[] rom)
    {
        int n = Math.Abs(rom.Length - _base.Length);
        for (int i = 0; i < Math.Min(rom.Length, _base.Length); i++) if (rom[i] != _base[i]) n++;
        return n;
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name) ?? throw new InvalidDataException($"The project file has no {name}.");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        s.Write(data);
    }

    // ── the changes: runs of bytes that differ from the starting ROM ──

    private static readonly byte[] Magic = "LFPD"u8.ToArray();

    public static byte[] MakeDiff(byte[] from, byte[] to)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(to.Length);
        int i = 0;
        while (i < to.Length)
        {
            if (i < from.Length && from[i] == to[i]) { i++; continue; }
            // a run of changed bytes; short stretches of equal bytes inside it are kept in (fewer, longer runs)
            int start = i, end = i + 1, same = 0;
            for (int j = i + 1; j < to.Length; j++)
            {
                if (j < from.Length && from[j] == to[j]) { if (++same > 8) break; }
                else { same = 0; end = j + 1; }
            }
            w.Write(start);
            w.Write(end - start);
            w.Write(to, start, end - start);
            i = end;
        }
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] ApplyDiff(byte[] from, byte[] diff)
    {
        using var r = new BinaryReader(new MemoryStream(diff));
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("The project's changes are damaged.");
        int length = r.ReadInt32();
        var rom = new byte[length];
        Array.Copy(from, rom, Math.Min(from.Length, length));
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            int at = r.ReadInt32(), n = r.ReadInt32();
            r.Read(rom, at, n);
        }
        return rom;
    }
}

/// <summary>The projects opened lately, newest first (%APPDATA%\LufiaForge\recent_projects.json).</summary>
public static class RecentProjects
{
    public sealed record Entry(string Path, string Name, DateTime Opened)
    {
        public bool Exists => File.Exists(Path);
        public string When => Opened.Date == DateTime.Today ? "today " + Opened.ToString("HH:mm")
                            : Opened.Date == DateTime.Today.AddDays(-1) ? "yesterday " + Opened.ToString("HH:mm")
                            : Opened.ToString("yyyy-MM-dd HH:mm");
    }

    private const int Max = 12;
    private static string File_ => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "recent_projects.json");

    public static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(File_)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(File_)) ?? new();
        }
        catch { return new(); }
    }

    public static void Add(string path, string name)
    {
        var list = Load().Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, new Entry(path, name, DateTime.Now));
        Write(list.Take(Max).ToList());
    }

    public static void Remove(string path) =>
        Write(Load().Where(e => !string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).ToList());

    private static void Write(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
