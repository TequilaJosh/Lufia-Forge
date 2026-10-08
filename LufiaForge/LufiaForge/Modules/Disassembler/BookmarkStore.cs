using System.IO;
using System.Text.Json;

namespace LufiaForge.Modules.Disassembler;

/// <summary>
/// Persists address bookmarks (labels) to a sidecar file next to the ROM.
/// Filename: <c>&lt;rom-filename&gt;.lfbookmarks.json</c>
///
/// Bookmarks are automatically overlaid onto the disassembly: any decoded address
/// with a matching bookmark shows its label as a comment.
/// </summary>
public sealed class BookmarkStore
{
    private readonly Dictionary<int, Bookmark> _map = new();
    private string? _filePath;

    // -------------------------------------------------------------------------
    // Load / Save
    // -------------------------------------------------------------------------

    /// <summary>
    /// Associate this store with a ROM file path and load any existing bookmarks.
    /// </summary>
    public void Open(string romFilePath)
    {
        _filePath = Path.ChangeExtension(romFilePath, null) + ".lfbookmarks.json";
        _map.Clear();

        if (!File.Exists(_filePath)) return;

        try
        {
            string json = File.ReadAllText(_filePath);
            var list    = JsonSerializer.Deserialize<List<Bookmark>>(json);
            if (list == null) return;
            foreach (var b in list)
                _map[b.SnesAddress] = b;
        }
        catch
        {
            // Corrupt file — start fresh, don't crash
        }
    }

    /// <summary>Persist the current bookmark set to disk.</summary>
    public void Save()
    {
        if (_filePath == null) return;
        try
        {
            string json = JsonSerializer.Serialize(_map.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { /* non-fatal */ }
    }

    // -------------------------------------------------------------------------
    // CRUD
    // -------------------------------------------------------------------------

    public void Add(int snesAddress, string label, string? comment = null, string colorHex = "#C8942A")
    {
        _map[snesAddress] = new Bookmark
        {
            SnesAddress = snesAddress,
            Label       = label,
            Comment     = comment,
            ColorHex    = colorHex,
        };
        Save();
    }

    public void Remove(int snesAddress)
    {
        _map.Remove(snesAddress);
        _regions = null;
        Save();
    }

    public Bookmark? GetByAddress(int snesAddress)
        => _map.TryGetValue(snesAddress, out var b) ? b : null;

    public IReadOnlyList<Bookmark> GetAll()
        => _map.Values.OrderBy(b => b.SnesAddress).ToList();

    public bool HasAddress(int snesAddress)
        => _map.ContainsKey(snesAddress);

    // -------------------------------------------------------------------------
    // Data regions
    // -------------------------------------------------------------------------

    private List<(int Start, int End, int Snes)>? _regions;

    private List<(int Start, int End, int Snes)> Regions => _regions ??= _map.Values
        .Where(b => b.DataLength > 0)
        .Select(b => { int fo = LufiaForge.Core.Lufia1Constants.SnesAddressToFileOffset(b.SnesAddress); return (fo, fo + b.DataLength, b.SnesAddress); })
        .Where(r => r.Item1 >= 0)
        .OrderBy(r => r.Item1).ToList();

    /// <summary>The data region (file offsets, end exclusive) containing <paramref name="fileOffset"/>, or null.</summary>
    public (int Start, int End)? DataRegionAtFile(int fileOffset)
    {
        foreach (var r in Regions)
        {
            if (r.Start > fileOffset) break;
            if (fileOffset < r.End) return (r.Start, r.End);
        }
        return null;
    }

    /// <summary>Mark bytes as data. Joins a region that ends right before them or starts right after them.</summary>
    public void MarkData(int snesAddress, int length)
    {
        int fo = LufiaForge.Core.Lufia1Constants.SnesAddressToFileOffset(snesAddress);
        if (fo < 0 || length <= 0) return;
        var before = Regions.FirstOrDefault(r => r.End == fo);
        var after = Regions.FirstOrDefault(r => r.Start == fo + length);
        Bookmark target;
        if (before.End == fo && before.End != 0 && _map.TryGetValue(before.Snes, out var prev))
        {
            prev.DataLength += length;
            target = prev;
        }
        else
        {
            target = new Bookmark { SnesAddress = snesAddress, Label = $"DATA_{snesAddress:X6}", Comment = "Marked as data", ColorHex = "#C0392B", DataLength = length };
            _map[snesAddress] = target;
        }
        if (after.Start == fo + length && after.End != 0 && _map.TryGetValue(after.Snes, out var next) && next != target)
        {
            target.DataLength += next.DataLength;
            _map.Remove(after.Snes);
        }
        _regions = null;
        Save();
    }

    // -------------------------------------------------------------------------
    // Annotation helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Apply bookmark labels to a list of decoded lines as inline comments.
    /// Lines whose SNES address matches a bookmark get the label prepended to their comment.
    /// </summary>
    public void AnnotateLines(IEnumerable<DisassemblyLine> lines)
    {
        foreach (var line in lines)
        {
            if (!_map.TryGetValue(line.SnesAddress, out var bm)) continue;

            string bookmarkComment = $"; {bm.Label}";
            if (!string.IsNullOrEmpty(bm.Comment))
                bookmarkComment += $"  ({bm.Comment})";

            // Prepend bookmark label; keep existing comment (e.g. REP/SEP annotation)
            line.Comment = string.IsNullOrEmpty(line.Comment)
                ? bookmarkComment
                : $"{bookmarkComment}  {line.Comment}";
        }
    }
}
