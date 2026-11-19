using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace LufiaForge.Core;

// a project: the rom someone started from and what they changed in it, all in one file (.lfproj, a zip).
// - project.json: name, dates, the starting rom's file name and checksum, the list of changes made, and the project's
//   own map names
// - base.sfc: the rom as it was when the project started (so the project works without it)
// - changes.bin: the bytes that differ from it now ("LFPD", the current length, then runs of offset, length and bytes)
// saving writes a temp file and then swaps it in (the previous save is kept as .bak), so a failed save never leaves a
// broken project
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
    // what was changed, oldest first (from the edits' descriptions)
    public List<string> Changes { get; } = new();
    // map names given in this project (map number -> name, empty = the automatic name even where a name is kept in
    // %AppData% for every project). see Maps.MapNames.ProjectNames
    public Dictionary<int, string> MapNames { get; } = new();
    // a map got renamed since the last save (renames don't change the rom)
    public bool MapNamesChanged { get; set; }
    // names and notes given to flags ("story:105" -> name, note, see Lufia2.L2FlagNotes)
    public Dictionary<string, FlagNote> FlagNotes { get; } = new();
    // a flag's name or note changed since the last save (they don't change the rom)
    public bool FlagNotesChanged { get; set; }

    // the project has changes the rom doesn't hold (map names, flag names)
    public bool NamesChanged => MapNamesChanged || FlagNotesChanged;

    private byte[] _base;

    // the rom as it was when the project started. read only, don't change it
    public byte[] BaseRom => _base;

    private LufiaProject(string path, string name, byte[] baseRom)
    {
        Path = path; Name = name; _base = baseRom;
    }

    // where new projects go by default: Documents\Lufia Forge Projects
    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lufia Forge Projects");

    // starts a project from a rom (as loaded, header removed) and saves it
    public static LufiaProject Create(string path, string name, byte[] rom, string romFile)
    {
        var project = new LufiaProject(path, name, (byte[])rom.Clone())
        {
            Created = DateTime.Now,
            BaseRomFile = System.IO.Path.GetFileName(romFile),
            BaseCrc = CommunityPatches.Crc32(rom),
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        project.Save(rom);
        return project;
    }

    // opens a project, returns it and the rom as it was last saved
    public static (LufiaProject Project, byte[] Rom) Open(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var info = JsonDocument.Parse(Read(zip, "project.json")).RootElement;
        int format = info.TryGetProperty("format", out var formatValue) ? formatValue.GetInt32() : 0;
        if (format > Format) throw new InvalidDataException("This project was saved by a newer Lufia Forge. Update the app to open it.");
        var project = new LufiaProject(path, NameFor(path, info.GetProperty("name").GetString()), Read(zip, "base.sfc"))
        {
            Created = info.TryGetProperty("created", out var created) ? created.GetDateTime() : File.GetCreationTime(path),
            Saved = info.TryGetProperty("saved", out var saved) ? saved.GetDateTime() : File.GetLastWriteTime(path),
            BaseRomFile = info.TryGetProperty("baseRom", out var baseRom) ? baseRom.GetString() ?? "" : "",
            BaseCrc = info.TryGetProperty("baseCrc", out var baseCrc) ? baseCrc.GetUInt32() : 0,
        };
        if (info.TryGetProperty("changes", out var changes))
            foreach (var change in changes.EnumerateArray()) project.Changes.Add(change.GetString() ?? "");
        if (info.TryGetProperty("mapNames", out var mapNames) && mapNames.ValueKind == JsonValueKind.Object)
            foreach (var mapName in mapNames.EnumerateObject())
                if (int.TryParse(mapName.Name, System.Globalization.NumberStyles.HexNumber, null, out int id)) project.MapNames[id] = mapName.Value.GetString() ?? "";
        if (info.TryGetProperty("flagNotes", out var flagNotes) && flagNotes.ValueKind == JsonValueKind.Object)
            foreach (var flagNote in flagNotes.EnumerateObject())
                if (flagNote.Value.ValueKind == JsonValueKind.Object)
                    project.FlagNotes[flagNote.Name] = new FlagNote(flagNote.Value.TryGetProperty("name", out var noteName) ? noteName.GetString() ?? "" : "",
                                                       flagNote.Value.TryGetProperty("note", out var noteText) ? noteText.GetString() ?? "" : "");
        return (project, ApplyDiff(project._base, Read(zip, "changes.bin")));
    }

    // a project's file is named after it (FileNameFor), so a file with some other name was copied or renamed by hand,
    // and then its own file name is the name (the copy of "p2" saved as "p3.lfproj" is "p3")
    public static string NameFor(string path, string? stored)
    {
        string file = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stored)) return file;
        return string.Equals(FileNameFor(stored), file, StringComparison.OrdinalIgnoreCase) ? stored : file;
    }

    // the file name a project with this name gets (characters a file name can't have become _)
    public static string FileNameFor(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

    // saves the project with the rom as it is now
    public void Save(byte[] rom)
    {
        Saved = DateTime.Now;
        string tempFile = Path + ".tmp";
        WriteFile(tempFile, rom);
        try
        {
            if (File.Exists(Path)) File.Replace(tempFile, Path, Path + ".bak");
            else File.Move(tempFile, Path);
        }
        catch
        {
            // File.Replace can fail after it already moved the project to .bak (a virus scanner or backup tool holding
            // the file). then the new save in the temp file is the only copy of the project, so it takes the project's place
            if (!File.Exists(Path) && File.Exists(tempFile))
                try { File.Move(tempFile, Path); } catch { }
            else
                try { File.Delete(tempFile); } catch { }
            throw;
        }
        MapNamesChanged = false;
        FlagNotesChanged = false;
    }

    // saves the project with this rom as another file and leaves the project itself alone (its path, its unsaved
    // state). it's the copy we keep when lufia forge has to close after an error
    public void SaveCopy(string path, byte[] rom)
    {
        string tempFile = path + ".tmp";
        WriteFile(tempFile, rom);
        File.Move(tempFile, path, overwrite: true);
    }

    // writes the project file (a failed write leaves nothing behind)
    private void WriteFile(string file, byte[] rom)
    {
        try { WriteZip(file, rom); }
        catch
        {
            try { File.Delete(file); } catch { }
            throw;
        }
    }

    private void WriteZip(string file, byte[] rom)
    {
        using (var fileStream = File.Create(file))
        using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create))
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
                flagNotes = FlagNotes.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => new { name = kv.Value.Name, note = kv.Value.Note }),
            }, new JsonSerializerOptions { WriteIndented = true });
            Write(zip, "project.json", json);
            Write(zip, "base.sfc", _base);
            Write(zip, "changes.bin", MakeDiff(_base, rom));
        }
    }

    // saves the project under another file (save project as). if that fails the project stays where it was
    public void SaveAs(string path, byte[] rom)
    {
        string oldPath = Path;
        Path = path;
        try { Save(rom); }
        catch { Path = oldPath; throw; }
    }

    // how many bytes differ from the starting rom
    public int ChangedBytes(byte[] rom)
    {
        int changed = Math.Abs(rom.Length - _base.Length);
        for (int i = 0; i < Math.Min(rom.Length, _base.Length); i++) if (rom[i] != _base[i]) changed++;
        return changed;
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"The project file has no {name}.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(data);
    }

    // the changes: runs of bytes that differ from the starting rom

    private static readonly byte[] Magic = "LFPD"u8.ToArray();

    public static byte[] MakeDiff(byte[] from, byte[] to)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(Magic);
        writer.Write(to.Length);
        int i = 0;
        while (i < to.Length)
        {
            if (i < from.Length && from[i] == to[i]) { i++; continue; }
            // a run of changed bytes. short stretches of equal bytes inside it stay in (fewer, longer runs)
            int start = i, end = i + 1, same = 0;
            for (int j = i + 1; j < to.Length; j++)
            {
                if (j < from.Length && from[j] == to[j]) { if (++same > 8) break; }
                else { same = 0; end = j + 1; }
            }
            writer.Write(start);
            writer.Write(end - start);
            writer.Write(to, start, end - start);
            i = end;
        }
        writer.Flush();
        return buffer.ToArray();
    }

    public static byte[] ApplyDiff(byte[] from, byte[] diff)
    {
        using var reader = new BinaryReader(new MemoryStream(diff));
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("The project's changes are damaged.");
        int length = reader.ReadInt32();
        var rom = new byte[length];
        Array.Copy(from, rom, Math.Min(from.Length, length));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            int runOffset = reader.ReadInt32(), runLength = reader.ReadInt32();
            reader.Read(rom, runOffset, runLength);
        }
        return rom;
    }
}

// a flag's name and note (LufiaProject.FlagNotes)
public sealed record FlagNote(string Name, string Note);

// the projects opened lately, newest first (%APPDATA%\LufiaForge\recent_projects.json)
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
    private static string ListPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LufiaForge", "recent_projects.json");

    public static List<Entry> Load()
    {
        try
        {
            if (!File.Exists(ListPath)) return new();
            // a project file copied or renamed by hand shows under its file name (the way it opens, LufiaProject.NameFor)
            return (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(ListPath)) ?? new())
                .Select(e => e with { Name = LufiaProject.NameFor(e.Path, e.Name) }).ToList();
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
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ListPath)!);
            File.WriteAllText(ListPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
