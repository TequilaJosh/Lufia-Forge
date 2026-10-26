using System.IO;

namespace LufiaForge.Core;

/// <summary>
/// What a ROM is: the clean US game, a hack Lufia Forge knows (Lufia Restored; Frue, Spekkio, Kureji Lufia and The
/// Wrath of Foomy), or another hack, and which parts of the game that hack changed. It compares the ROM with a
/// fingerprint of the clean US ROM shipped with Lufia Forge (Fingerprints/lufiaN.bin: a 16-bit hash, the low half of
/// a CRC32, of every 64 bytes; no game data). The same fingerprint tells which bytes of the areas Lufia Forge treats as
/// free were put there by another hack (<see cref="ForeignRanges"/>), so they are never reused.
/// </summary>
public static class RomIdentity
{
    public const int Chunk = 64;

    private static readonly Dictionary<GameKind, ushort[]?> Prints = new();

    /// <summary>The clean ROM's fingerprint for a game, or null.</summary>
    public static ushort[]? Print(GameKind game)
    {
        lock (Prints)
        {
            if (Prints.TryGetValue(game, out var p)) return p;
            string name = game == GameKind.Lufia2 ? "Fingerprints.lufia2.bin" : game == GameKind.Lufia1 ? "Fingerprints.lufia1.bin" : "";
            using var s = name.Length == 0 ? null : typeof(RomIdentity).Assembly.GetManifestResourceStream(name);
            if (s == null) return Prints[game] = null;
            using var ms = new MemoryStream(); s.CopyTo(ms);
            var b = ms.ToArray();
            var u = new ushort[b.Length / 2];
            Buffer.BlockCopy(b, 0, u, 0, u.Length * 2);
            return Prints[game] = u;
        }
    }

    private static ushort Hash(byte[] data, int chunk)
    {
        int at = chunk * Chunk, len = Math.Min(Chunk, data.Length - at);
        return len <= 0 ? (ushort)0 : (ushort)(CommunityPatches.Crc32(data.AsSpan(at, len).ToArray()) & 0xFFFF);
    }

    /// <summary>Whether the 64-byte chunk holding <paramref name="at"/> differs from the clean game (or lies past it).</summary>
    public static bool Changed(byte[] data, GameKind game, int at)
    {
        var p = Print(game);
        int c = at / Chunk;
        return p == null || c >= p.Length || Hash(data, c) != p[c];
    }

    /// <summary>The parts of [start, end) where <paramref name="data"/> differs from the clean game, in whole chunks.</summary>
    public static List<(int Start, int End)> ChangedRanges(byte[] data, GameKind game, int start, int end)
    {
        var list = new List<(int, int)>();
        if (Print(game) == null) { list.Add((start, end)); return list; }
        for (int c = start / Chunk; c * Chunk < end; c++)
        {
            if (!Changed(data, game, c * Chunk)) continue;
            int s = Math.Max(start, c * Chunk), e = Math.Min(end, c * Chunk + Chunk);
            if (list.Count > 0 && list[^1].Item2 == s) list[^1] = (list[^1].Item1, e); else list.Add((s, e));
        }
        return list;
    }

    /// <summary>The bytes of a free area another hack put there: the chunks of the ROM the project started from (or the
    /// ROM as it was opened) that differ from the clean game. Lufia Forge's own blocks in the area are known by their
    /// records and counted apart by the callers.</summary>
    public static List<(int Start, int End)> ForeignRanges(RomBuffer rom, int start, int end) =>
        rom.Original is { } o && o.Length >= end ? ChangedRanges(o, rom.Game, start, end) : new();

    // ── what the ROM is ──────────────────────────────────────────────────────────

    public sealed record Report(string What, List<string> ChangedParts, double ChangedShare);

    /// <summary>The parts of each game, for saying what a hack changed (file offsets, end exclusive).</summary>
    private static readonly (int Start, int End, string Name)[] Lufia2Parts =
    {
        (0x000000, 0x038000, "game code"), (0x038000, 0x078000, "events, map names and menu text"), (0x078000, 0x084000, "dungeon puzzles"),
        (0x084000, 0x088000, "sound driver"), (0x088000, 0x08B8B5, "map info and credits"), (0x08B8B5, 0x0A8000, "treasure and other data"),
        (0x0A8000, 0x0AFA5B, "battle animations"), (0x0AFA5B, 0x0B05C0, "spells and IP attack effects"), (0x0B05C0, 0x0B4F69, "monsters"),
        (0x0B4F69, 0x0B8000, "items"), (0x0B8000, 0x0BB62C, "descriptions"), (0x0BB62C, 0x0BC53D, "heroes and encounters"),
        (0x0BC53D, 0x0C0000, "capsule monsters, shops and battle backgrounds"), (0x0C0000, 0x0CDE6C, "instrument samples"),
        (0x0CDE6C, 0x138000, "fonts, palettes, sprites and names"), (0x138000, 0x27F000, "songs, maps and graphics (packed files)"),
        (0x27F000, 0x280000, "map sprite and map lists"),
    };

    private static readonly (int Start, int End, string Name)[] Lufia1Parts =
    {
        (0x000000, 0x018000, "game code and monsters"), (0x018000, 0x040000, "events and map setups"), (0x040000, 0x050000, "battle data and code"),
        (0x050000, 0x060000, "font, text, items and descriptions"), (0x060000, 0x100000, "maps, graphics and music (resources)"),
    };

    /// <summary>What a ROM is, from its bytes (best given the ROM a project started from).</summary>
    public static Report Identify(byte[] data, GameKind game, RomBuffer? asBuffer = null)
    {
        var rom = asBuffer ?? new RomBuffer(data, "");
        uint clean = game == GameKind.Lufia2 ? CommunityPatches.Lufia2CleanCrc : CommunityPatches.CleanCrc;
        int cleanSize = game == GameKind.Lufia2 ? 0x280000 : 0x100000;
        if (data.Length == cleanSize && CommunityPatches.Crc32(data) == clean) return new("the clean US game", new(), 0);
        string? known = null;
        if (game == GameKind.Lufia2 && CommunityPatches.Lufia2HackIn(rom) is { } id)
            known = id switch { "frue" => "Frue Lufia v8.2", "spekkio" => "Spekkio Lufia v8.2", "kureji" => "Kureji Lufia v8.2", "foomy" => "The Wrath of Foomy v8.2", _ => id };
        if (game == GameKind.Lufia1 && CommunityPatches.RestoredApplied(rom)) known = "Lufia Restored v3.2";
        var p = Print(game);
        if (p == null) return new(known ?? "a changed ROM", new(), 0);
        var parts = game == GameKind.Lufia2 ? Lufia2Parts : Lufia1Parts;
        int changed = 0, total = Math.Min(p.Length, data.Length / Chunk);
        var byPart = new Dictionary<string, int>();
        for (int c = 0; c < total; c++)
        {
            if (Hash(data, c) == p[c]) continue;
            changed++;
            // (a chunk on the edge of two parts counts for both)
            int at = c * Chunk;
            var hit = parts.Where(x => at < x.End && at + Chunk > x.Start).Select(x => x.Name).DefaultIfEmpty("other");
            foreach (var part in hit) byPart[part] = byPart.GetValueOrDefault(part) + 1;
        }
        var list = byPart.OrderByDescending(k => k.Value).Select(k => k.Key).ToList();
        if (data.Length > cleanSize) list.Add($"{(data.Length - cleanSize) / 1024} KB added after the game");
        double share = total == 0 ? 0 : changed / (double)total;
        string what = known ?? (changed == 0 ? "the US game (only grown)" : share < 0.002 ? "the US game with small changes (another hack or earlier edits)" : "a hack Lufia Forge doesn't know by name");
        return new(what, list, share);
    }

    // ── which game, when the title was changed ───────────────────────────────────

    /// <summary>Bytes of each game's own code that hacks leave alone (the event and setup interpreters, the battle
    /// script interpreter's entry), for telling the game apart when a hack renamed it.</summary>
    private static readonly (GameKind Game, int At, string Hex)[] Signatures =
    {
        (GameKind.Lufia1, 0x98AF, "20 40 c7 c9 20 b0 0a 0a 85 00 64 01"),
        (GameKind.Lufia1, 0xC2A0, "30 a5 1a 0a 0a 65 1a a8 b9 00 82 8d"),
        (GameKind.Lufia2, 0x4A14, "4c 9d b3 9d d4 9d d5 9d fa 9d 45 9e"),
        (GameKind.Lufia2, 0x2B452, "8b 08 c2 30 48 da 5a e2 20 9c 60 0a"),
    };

    /// <summary>The game whose code the ROM holds (both of its signatures), or Unknown.</summary>
    public static GameKind ByCode(byte[] rom)
    {
        foreach (var game in new[] { GameKind.Lufia2, GameKind.Lufia1 })
        {
            var sigs = Signatures.Where(s => s.Game == game).ToList();
            if (sigs.All(s =>
            {
                var b = s.Hex.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();
                return s.At + b.Length <= rom.Length && rom.AsSpan(s.At, b.Length).SequenceEqual(b);
            })) return game;
        }
        return GameKind.Unknown;
    }
}
