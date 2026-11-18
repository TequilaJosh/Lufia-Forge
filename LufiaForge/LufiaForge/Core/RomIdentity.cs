using System.IO;

namespace LufiaForge.Core;

// what a rom is: the clean us game, a hack we know (lufia restored, frue, spekkio, kureji lufia and the wrath of foomy),
// or some other hack, and which parts of the game that hack changed. it compares the rom with a fingerprint of the
// clean us rom that ships with lufia forge (Fingerprints/lufiaN.bin: a 16-bit hash, the low half of a crc32, of every
// 64 bytes, no game data). the same fingerprint tells which bytes of the areas we treat as free were put there by
// another hack (ForeignRanges), so they never get reused
public static class RomIdentity
{
    public const int Chunk = 64;

    private static readonly Dictionary<GameKind, ushort[]?> Prints = new();

    // the clean rom's fingerprint for a game, or null
    public static ushort[]? Print(GameKind game)
    {
        lock (Prints)
        {
            if (Prints.TryGetValue(game, out var print)) return print;
            string name = game == GameKind.Lufia2 ? "Fingerprints.lufia2.bin" : game == GameKind.Lufia1 ? "Fingerprints.lufia1.bin" : "";
            using var stream = name.Length == 0 ? null : typeof(RomIdentity).Assembly.GetManifestResourceStream(name);
            if (stream == null) return Prints[game] = null;
            using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var hashes = new ushort[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, hashes, 0, hashes.Length * 2);
            return Prints[game] = hashes;
        }
    }

    private static ushort Hash(byte[] data, int chunk)
    {
        int offset = chunk * Chunk, length = Math.Min(Chunk, data.Length - offset);
        return length <= 0 ? (ushort)0 : (ushort)(CommunityPatches.Crc32(data.AsSpan(offset, length).ToArray()) & 0xFFFF);
    }

    // whether the 64-byte chunk with offset at in it differs from the clean game (or is past its end)
    public static bool Changed(byte[] data, GameKind game, int at)
    {
        var print = Print(game);
        int chunk = at / Chunk;
        return print == null || chunk >= print.Length || Hash(data, chunk) != print[chunk];
    }

    // the parts of [start, end) where data differs from the clean game, in whole chunks
    public static List<(int Start, int End)> ChangedRanges(byte[] data, GameKind game, int start, int end)
    {
        var list = new List<(int, int)>();
        if (Print(game) == null) { list.Add((start, end)); return list; }
        for (int chunk = start / Chunk; chunk * Chunk < end; chunk++)
        {
            if (!Changed(data, game, chunk * Chunk)) continue;
            int rangeStart = Math.Max(start, chunk * Chunk), rangeEnd = Math.Min(end, chunk * Chunk + Chunk);
            if (list.Count > 0 && list[^1].Item2 == rangeStart) list[^1] = (list[^1].Item1, rangeEnd); else list.Add((rangeStart, rangeEnd));
        }
        return list;
    }

    // the bytes in a free area that another hack put there: the chunks of the rom the project started from (or the rom
    // as it was opened) that differ from the clean game. our own blocks in the area are known from their records and
    // the callers count them separately
    public static List<(int Start, int End)> ForeignRanges(RomBuffer rom, int start, int end) =>
        rom.Original is { } original && original.Length >= end ? ChangedRanges(original, rom.Game, start, end) : new();

    // what the rom is

    public sealed record Report(string What, List<string> ChangedParts, double ChangedShare);

    // the parts of each game, for saying what a hack changed (file offsets, end exclusive)
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

    // the parts of a game's rom, for naming where a change is (file offsets, end exclusive)
    public static IReadOnlyList<(int Start, int End, string Name)> Parts(GameKind game) => game == GameKind.Lufia2 ? Lufia2Parts : Lufia1Parts;

    private static readonly (int Start, int End, string Name)[] Lufia1Parts =
    {
        (0x000000, 0x018000, "game code and monsters"), (0x018000, 0x040000, "events and map setups"), (0x040000, 0x050000, "battle data and code"),
        (0x050000, 0x060000, "font, text, items and descriptions"), (0x060000, 0x100000, "maps, graphics and music (resources)"),
    };

    // what a rom is, from its bytes (best to pass the rom a project started from)
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
        var print = Print(game);
        if (print == null) return new(known ?? "a changed ROM", new(), 0);
        var parts = game == GameKind.Lufia2 ? Lufia2Parts : Lufia1Parts;
        int changed = 0, total = Math.Min(print.Length, data.Length / Chunk);
        var byPart = new Dictionary<string, int>();
        for (int chunk = 0; chunk < total; chunk++)
        {
            if (Hash(data, chunk) == print[chunk]) continue;
            changed++;
            // a chunk on the edge of two parts counts for both
            int offset = chunk * Chunk;
            var hit = parts.Where(x => offset < x.End && offset + Chunk > x.Start).Select(x => x.Name).DefaultIfEmpty("other");
            foreach (var part in hit) byPart[part] = byPart.GetValueOrDefault(part) + 1;
        }
        var list = byPart.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList();
        if (data.Length > cleanSize) list.Add($"{(data.Length - cleanSize) / 1024} KB added after the game");
        double share = total == 0 ? 0 : changed / (double)total;
        string what = known ?? (changed == 0 ? "the US game (only grown)" : share < 0.002 ? "the US game with small changes (another hack or earlier edits)" : "a hack Lufia Forge doesn't know by name");
        return new(what, list, share);
    }

    // which game it is when the title got changed

    // bytes of each game's own code that hacks leave alone (the event and setup interpreters, the battle script
    // interpreter's entry), to tell the games apart when a hack renamed it
    private static readonly (GameKind Game, int At, string Hex)[] Signatures =
    {
        (GameKind.Lufia1, 0x98AF, "20 40 c7 c9 20 b0 0a 0a 85 00 64 01"),
        (GameKind.Lufia1, 0xC2A0, "30 a5 1a 0a 0a 65 1a a8 b9 00 82 8d"),
        (GameKind.Lufia2, 0x4A14, "4c 9d b3 9d d4 9d d5 9d fa 9d 45 9e"),
        (GameKind.Lufia2, 0x2B452, "8b 08 c2 30 48 da 5a e2 20 9c 60 0a"),
    };

    // the game whose code the rom has (both of its signatures), or Unknown
    public static GameKind ByCode(byte[] rom)
    {
        foreach (var game in new[] { GameKind.Lufia2, GameKind.Lufia1 })
        {
            var signatures = Signatures.Where(s => s.Game == game).ToList();
            if (signatures.All(s =>
            {
                var bytes = s.Hex.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();
                return s.At + bytes.Length <= rom.Length && rom.AsSpan(s.At, bytes.Length).SequenceEqual(bytes);
            })) return game;
        }
        return GameKind.Unknown;
    }
}
