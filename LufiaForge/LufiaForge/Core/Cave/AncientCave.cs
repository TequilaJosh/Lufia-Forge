using System.Security.Cryptography;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Cave;

/// <summary>
/// The Ancient Cave: floors built at random every time one loads (rooms, 2-wide corridors, cliffs, an arrival door,
/// stairs down, 8-12 chests), from the Ancient Cave project (ac.asm, see <see cref="Source"/>).
/// <para>Two map numbers (free ones, taken like New map does) alternate as the floors, so every staircase is a real
/// map change. Both use their own copy of map 8E's data (the Old Cave floor: tileset 6, palette 8, 54x45) as the
/// template the generator overwrites in RAM, so editing map 8E can't break the cave. Their setup scripts (music,
/// two step-on zones, 12 chest spots) and event scripts (stairs: set flag F0, go to the other cave map) are written
/// through Lufia Forge's own tables.</para>
/// Hooks:
/// <code>
/// $01:BDA5  22 MapLoadHook          after a map is unpacked: a cave map gets a new floor (was JSL $00A47D)
/// $01:9969  22 PostHeader EA        after the setup script: the floor's encounters and chest contents (was LDA $92 / CMP $078E)
/// $01:C0D4  22 CaveStart            cave-only ROM: a new game starts on floor 1 with the four heroes (was LDA #$4F / JSR $BD70)
/// $01:8A12  22 GameOver EA          a game over in the cave: back to the last checkpoint floor (was LDA $1349 / BNE)
/// $00:AB23  22 SaveName EA          the save screen's place name: "Cave" for the cave maps (was LDY #0 / LDA $00; the game
///                                   shows the last town, which the cave never changes)
/// $01:A376, $00:8249                the floor counter (see <see cref="HudSource"/>)
/// </code>
/// $01:C081 runs twice: at power-on for the opening (demo flag $0D9E = FF) and again for New Game after the file
/// select ($0D9E = 0, $01:853F). CaveStart sends only the second one to the cave; the opening runs as usual (the
/// project's hook sent the power-on opening to the cave too, still flagged as the demo, so any button press later
/// brought up the file select).
/// RAM: $7F:FF20-$FF27 (floor, active, RNG, spawn), $7F:E000-$EDFF (generator), as in the project.
/// </summary>
public static partial class AncientCave
{
    public const int TemplateMap = 0x8E;
    public const int ChestFlagBase = 0x3C0;

    private const int MapLoadSite = 0x0BDA5;   // $01:BDA5
    private const int PostHeaderSite = 0x09969; // $01:9969
    private const int NewGameSite = 0x0C0D4;   // $01:C0D4
    private const int GameOverSite = 0x08A12;  // $01:8A12
    private const int SaveNameSite = 0x02B23;  // $00:AB23
    private static readonly byte[] MapLoadOriginal = { 0x22, 0x7D, 0xA4, 0x00 };
    private static readonly byte[] PostHeaderOriginal = { 0xA5, 0x92, 0xCD, 0x8E, 0x07 };
    private static readonly byte[] NewGameOriginal = { 0xA9, 0x4F, 0x20, 0x70, 0xBD, 0x20, 0xB4 };
    private static readonly byte[] GameOverOriginal = { 0xAD, 0x49, 0x13, 0xD0, 0x24 };
    private static readonly byte[] SaveNameOriginal = { 0xA0, 0x00, 0x00, 0xA5, 0x00 };
    private static readonly byte[] Magic = "LFAC"u8.ToArray();
    private const byte Version = 21;
    // block: magic, version, map 1, map 2, options (bit 0 = cave-only New Game, bit 1 = elevator start), save flag, floor 100 flag, 2 spare,
    // the 4 floor 100 monsters, the boss formation as it was (4); then the code
    private const int Header = 24;   // (+ 20-22: the merchants' story flags, 23 spare; version 21 on)

    private static int FileOffset(int snes) => (snes >> 16 & 0x7F) * 0x8000 + (snes & 0x7FFF);
    private static int SnesAddress(int file) => (file / 0x8000 | 0x80) << 16 | (file % 0x8000 | 0x8000);
    private static bool Matches(RomBuffer rom, int at, byte[] b) => at >= 0 && at + b.Length <= rom.Length && rom.ReadBytes(at, b.Length).SequenceEqual(b);

    public sealed record Info(int Map1, int Map2, bool CaveOnly, int Block, int SaveFlag, int FinalFlag, int[] Bosses, int[] OldFormation, bool ElevatorStart = false,
        bool People = false, int[]? ShopFlags = null)
    {
        public CaveMode Mode => CaveOnly ? CaveMode.CaveOnly : ElevatorStart ? CaveMode.ElevatorStart : CaveMode.NormalGame;
    }

    /// <summary>How the cave is reached.</summary>
    public enum CaveMode
    {
        /// <summary>New Game starts on floor 1 with the four heroes at level 1; the rest of the game can't be reached.</summary>
        CaveOnly,
        /// <summary>The normal game; the Old Cave's elevator man (map 88) offers the way down.</summary>
        NormalGame,
        /// <summary>As the normal game, but New Game starts (after the prologue sets the party up) in the elevator room,
        /// whose way out and elevator are closed: the cave is all there is.</summary>
        ElevatorStart,
    }

    /// <summary>The cave is in this ROM in the given mode.</summary>
    public static bool IsAppliedAs(RomBuffer rom, CaveMode mode) => Read(rom)?.Mode == mode;

    public static bool IsOriginal(RomBuffer rom) =>
        Matches(rom, MapLoadSite, MapLoadOriginal) && Matches(rom, PostHeaderSite, PostHeaderOriginal) && Matches(rom, NewGameSite, NewGameOriginal) &&
        Matches(rom, GameOverSite, GameOverOriginal) && Matches(rom, ChestSite, ChestOriginal) && Matches(rom, MsgSite, MsgOriginal) &&
        Matches(rom, SaveNameSite, SaveNameOriginal) && Matches(rom, GroupsSite, GroupsOriginal) && Matches(rom, PaletteSite, PaletteOriginal) &&
        Matches(rom, HudOamSite, HudOamOriginal) && Matches(rom, HudNmiSite, HudNmiOriginal) && Matches(rom, GoldSite, GoldOriginal);

    public static bool IsApplied(RomBuffer rom) => Read(rom) != null;

    /// <summary>The cave in this ROM, or null.</summary>
    public static Info? Read(RomBuffer rom)
    {
        if (rom.ReadByte(MapLoadSite) != 0x22) return null;
        int hook = FileOffset(rom.ReadByte(MapLoadSite + 1) | rom.ReadByte(MapLoadSite + 2) << 8 | rom.ReadByte(MapLoadSite + 3) << 16);
        for (int b = hook - 4; b >= Math.Max(0, hook - 0x40); b--)
            if (Matches(rom, b, Magic))
                return new Info(rom.ReadByte(b + 5), rom.ReadByte(b + 6), (rom.ReadByte(b + 7) & 1) != 0, b, rom.ReadByte(b + 8),
                    rom.ReadByte(b + 9), rom.ReadBytes(b + 12, 4).Select(x => (int)x).ToArray(), rom.ReadBytes(b + 16, 4).Select(x => (int)x).ToArray(),
                    (rom.ReadByte(b + 7) & 2) != 0, (rom.ReadByte(b + 7) & 4) != 0,
                    rom.ReadByte(b + 4) >= 21 ? rom.ReadBytes(b + 20, 3).Select(x => (int)x).ToArray() : null);
        return null;
    }

    /// <summary>
    /// Adds the cave (or changes its options when it's there). <paramref name="caveOnly"/>: New Game starts on floor 1
    /// with the four heroes at level 1 and nothing else (the rest of the game can't be reached).
    /// </summary>
    public static void Apply(RomBuffer rom, bool caveOnly) => Apply(rom, caveOnly ? CaveMode.CaveOnly : CaveMode.NormalGame);

    /// <summary>Adds the cave in the given mode (or switches an applied cave to it).</summary>
    public static void Apply(RomBuffer rom, CaveMode mode) => Apply(rom, mode, null);

    /// <summary>Adds the cave in the given mode; <paramref name="people"/>: the priest and the merchants (null = as
    /// they are, none for a new cave).</summary>
    public static void Apply(RomBuffer rom, CaveMode mode, bool? people)
    {
        bool caveOnly = mode == CaveMode.CaveOnly, locked = mode == CaveMode.ElevatorStart;
        var old = Read(rom);
        bool withPeople = people ?? old?.People ?? false;
        if (old == null && !IsOriginal(rom))
            throw new InvalidOperationException("The game's map loading or New Game code ($01:BDA5, $01:9969, $01:C0D4) isn't the original, so the Ancient Cave can't be added safely.");
        if (old != null && old.Mode == mode && old.People == withPeople && rom.ReadByte(old.Block + 4) == Version) return;
        CheckChestFlags(rom, old);
        if (!caveOnly && (!Matches(rom, SaveSite, SaveOriginal) && rom.ReadByte(SaveSite) != 0x22 || !Matches(rom, LoadSite, LoadOriginal) && rom.ReadByte(LoadSite) != 0x22))
            throw new InvalidOperationException("The game's save / load code ($00:922B, $00:931D) isn't the original, so the cave's party backup can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);

        int map1, map2;
        // the story flag that tells the arrival door to offer a save (one nothing else in the game uses)
        int saveFlag = old?.SaveFlag ?? FreeFlag(rom, -1);
        int finalFlag = old != null && old.FinalFlag != 0 && old.FinalFlag != old.SaveFlag ? old.FinalFlag : FreeFlag(rom, saveFlag);
        // floor 100: the four Sinistrals (made once), the boss formation as it was (kept to put back)
        int[] bosses = old != null && old.Bosses.Any(b => b != 0) ? old.Bosses : MakeBosses(rom);
        int[] oldFormation = old != null && old.Bosses.Any(b => b != 0) ? old.OldFormation : Encounters.BossMonsters(rom, BossFormation);
        Encounters.SetBossMonsters(rom, BossFormation, bosses);
        int[] shopFlags = ShopFlags(rom, saveFlag, finalFlag);
        if (old != null)
        {
            map1 = old.Map1; map2 = old.Map2;
            WriteEvents(rom, map1, map2, saveFlag, finalFlag, caveOnly, EventScript.BlockOffset(rom, map1));
            WriteEvents(rom, map2, map1, saveFlag, finalFlag, caveOnly, EventScript.BlockOffset(rom, map2));
            // (the cave's copy of map 8E and setup scripts again: an older cave gets the people)
            ResourceWriter.Save(rom, rom.ReadByte(MapFactory.SelectTable + map1) + 0xB2, CaveTemplate(rom), allowExpand: true);
            foreach (int m in new[] { map1, map2 }) SetupScripts.Replace(rom, m, SetupScript(m, saveFlag, shopFlags, withPeople));
        }
        else
        {
            var template = CaveTemplate(rom);
            var free = MapFactory.FreeIds(rom);
            if (free.Count < 2) throw new InvalidOperationException("The Ancient Cave needs two free map numbers and this ROM has fewer left (each New map takes one).");
            // the project's numbers when they're free, else the first free ones
            (map1, map2) = free.Contains(0x85) && free.Contains(0x86) ? (0x85, 0x86) : (free[0], free[1]);
            int res = ResourceIds.Add(rom, template);
            foreach (var (m, other) in new[] { (map1, map2), (map2, map1) })
            {
                rom.WriteByte(MapFactory.SelectTable + m, (byte)(res - 0xB2));
                rom.WriteByte(MapFactory.AreaTable + m, rom.ReadByte(MapFactory.AreaTable + TemplateMap));
                SetupScripts.Replace(rom, m, SetupScript(m, saveFlag, shopFlags, withPeople));
                WriteEvents(rom, m, other, saveFlag, finalFlag, caveOnly, 0);
            }
        }

        var space = ExpansionSpace.Load(rom);
        if (old != null) space.Free(old.Block);
        var probe = Assemble(rom, map1, map2, saveFlag, finalFlag, caveOnly, locked, shopFlags).Build(0x808000);
        int at = space.AllocateInOneBank(Header + probe.Length, ExpansionSpace.Kind.Patch, 32);
        space.Save();
        int snes = SnesAddress(at);
        var asm = Assemble(rom, map1, map2, saveFlag, finalFlag, caveOnly, locked, shopFlags);
        rom.WriteBytes(at, Magic);
        rom.WriteBytes(at + 4, new[] { Version, (byte)map1, (byte)map2, (byte)((caveOnly ? 1 : 0) | (locked ? 2 : 0) | (withPeople ? 4 : 0)), (byte)saveFlag, (byte)finalFlag, (byte)0, (byte)0 });
        rom.WriteBytes(at + 12, bosses.Select(b => (byte)b).Concat(oldFormation.Select(b => (byte)b)).ToArray());
        rom.WriteBytes(at + 20, shopFlags.Select(f => (byte)f).Append((byte)0).ToArray());
        rom.WriteBytes(at + Header, asm.Build(snes + Header));
        byte[] Jsl(string label) { int a = snes + Header + asm.Offset(label); return new byte[] { 0x22, (byte)a, (byte)(a >> 8), (byte)(a >> 16) }; }
        rom.WriteBytes(MapLoadSite, Jsl("MapLoadHook"));
        rom.WriteBytes(PostHeaderSite, Jsl("PostHeader")); rom.WriteByte(PostHeaderSite + 4, 0xEA);
        rom.WriteBytes(NewGameSite, NewGameOriginal);
        if (caveOnly) rom.WriteBytes(NewGameSite, Jsl("CaveStart"));
        rom.WriteBytes(GameOverSite, Jsl("GameOver")); rom.WriteByte(GameOverSite + 4, 0xEA);
        rom.WriteBytes(ChestSite, Jsl("ChestHook")); rom.WriteBytes(ChestSite + 4, new byte[] { 0xEA, 0xEA });
        rom.WriteBytes(MsgSite, Jsl("MsgHook")); rom.WriteBytes(MsgSite + 4, new byte[] { 0xEA, 0xEA });
        rom.WriteBytes(SaveNameSite, Jsl("SaveName")); rom.WriteByte(SaveNameSite + 4, 0xEA);
        rom.WriteBytes(GroupsSite, Jsl("GroupCount"));
        rom.WriteBytes(GoldSite, Jsl("GoldHook")); rom.WriteByte(GoldSite + 4, 0xEA);
        rom.WriteBytes(HudOamSite, Jsl("HudOam")); rom.WriteByte(HudOamSite + 4, 0xEA);
        rom.WriteBytes(HudNmiSite, Jsl("HudNmi"));
        if (caveOnly)
        {
            if (rom.ReadByte(SaveSite) == 0x22) rom.WriteBytes(SaveSite, SaveOriginal);
            if (rom.ReadByte(LoadSite) == 0x22 && !rom.ReadBytes(LoadSite, 4).SequenceEqual(LoadOriginal)) rom.WriteBytes(LoadSite, LoadOriginal);
            RemoveEntrance(rom);
            RemoveStart(rom);
        }
        else
        {
            rom.WriteBytes(SaveSite, Jsl("SaveHook")); rom.WriteByte(SaveSite + 4, 0xEA);
            rom.WriteBytes(LoadSite, Jsl("LoadHook"));
            AddEntrance(rom, map1, locked, finalFlag);
            if (locked) AddStart(rom, finalFlag); else RemoveStart(rom);
        }
        rom.WriteBytes(PaletteSite, Jsl("PaletteSlot")); rom.WriteBytes(PaletteSite + 4, new byte[] { 0xEA, 0xEA, 0xEA, 0xEA });
        rom.FixChecksum();
    }

    /// <summary>Takes the cave out: hooks back, its code, maps (data copy, scripts) and numbers freed.</summary>
    public static void Remove(RomBuffer rom)
    {
        var info = Read(rom);
        if (info == null) return;
        rom.WriteBytes(MapLoadSite, MapLoadOriginal);
        rom.WriteBytes(PostHeaderSite, PostHeaderOriginal);
        rom.WriteBytes(NewGameSite, NewGameOriginal);
        rom.WriteBytes(GameOverSite, GameOverOriginal);
        rom.WriteBytes(ChestSite, ChestOriginal);
        rom.WriteBytes(MsgSite, MsgOriginal);
        rom.WriteBytes(SaveNameSite, SaveNameOriginal);
        rom.WriteBytes(GroupsSite, GroupsOriginal);
        rom.WriteBytes(GoldSite, GoldOriginal);
        rom.WriteBytes(HudOamSite, HudOamOriginal);
        rom.WriteBytes(HudNmiSite, HudNmiOriginal);
        if (rom.ReadByte(SaveSite) == 0x22) rom.WriteBytes(SaveSite, SaveOriginal);
        if (!rom.ReadBytes(LoadSite, 4).SequenceEqual(LoadOriginal)) rom.WriteBytes(LoadSite, LoadOriginal);
        RemoveEntrance(rom);
        RemoveStart(rom);
        rom.WriteBytes(PaletteSite, PaletteOriginal);
        if (info.Bosses.Any(b => b != 0)) Encounters.SetBossMonsters(rom, BossFormation, info.OldFormation);
        var space = ExpansionSpace.Load(rom);
        space.Free(info.Block);
        int data = LufiaCompression.ResourceOffset(rom, LufiaMap.MapDataResource(rom, info.Map1));
        if (space.At(data) is { } d && d.Start == data && d.Kind is ExpansionSpace.Kind.Resource or ExpansionSpace.Kind.MapData) space.Free(data);
        foreach (int m in new[] { info.Map1, info.Map2 })
        {
            int block = EventScript.BlockOffset(rom, m);
            if (space.At(block) is { Kind: ExpansionSpace.Kind.ScriptBlock } b && b.Start == block) space.Free(block);
        }
        space.Save();
        foreach (int m in new[] { info.Map1, info.Map2 })
        {
            SetupScripts.Forget(rom, m);
            rom.WriteByte(MapFactory.SelectTable + m, 0);
            rom.WriteUInt16Le(MapFactory.SetupTable + m * 2, 0xFFFF);
            rom.WriteBytes(MapFactory.ScriptTable + m * 5, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        }
        rom.FixChecksum();
    }

    // ── what the cave needs from the ROM ──

    /// <summary>
    /// Map 8E's data as the generator expects it: it writes the floor's zones, chests and arrival points at fixed
    /// places in the loaded map ($7F:133F counts, $7F:151C arrivals, $7F:1570 zones, $7F:1588 objects).
    /// </summary>
    private static byte[] TemplateData(RomBuffer rom)
    {
        if (!LufiaMap.IsValidMap(rom, TemplateMap, out var data, out _) || data == null)
            throw new InvalidOperationException("Map 8E (the Old Cave floor the Ancient Cave is built on) isn't in this ROM.");
        if (!SHA256.HashData(data).AsSpan().SequenceEqual(Convert.FromHexString(TemplateHash)))
            throw new InvalidOperationException("Map 8E (the Old Cave floor) was changed, and the Ancient Cave's floor builder needs its original layout. Revert map 8E to add the cave.");
        return data;
    }

    /// <summary>SHA-256 of the original map 8E data (unpacked).</summary>
    private const string TemplateHash = "AFE6A9F8D88C575F065DD3355E3E3FEBB8293D9C5AA318FB30E1412D550F71B8";

    /// <summary>The cave's chests use opened-flags 3C0-3DF (cleared on every new floor) and keeps its floor and checkpoint
    /// in the bytes of 3E0-3EF (saved with the game): no other map may use them.</summary>
    private static void CheckChestFlags(RomBuffer rom, Info? cave)
    {
        foreach (var m in MapCatalog.Scan(rom))
        {
            if (cave != null && (m.MapId == cave.Map1 || m.MapId == cave.Map2)) continue;
            int s = SetupScripts.Offset(rom, m.MapId);
            int baseFlag = rom.ReadUInt16Le(s);
            if (baseFlag != 0xFFFF && baseFlag + 32 > ChestFlagBase && baseFlag < ChestFlagBase + 64)
                throw new InvalidOperationException($"Map {m.MapId:X2} uses chest flags around {ChestFlagBase:X3}, which the Ancient Cave needs.");
        }
    }

    /// <summary>A cave map's setup script: chest flag base, music 06, encounters (set per floor), its own events,
    /// zone 0 = arrival door (event 1), zone 1 = stairs (event 2), <see cref="MaxChests"/> chests.</summary>
    public const int MaxChests = 12;   // the generator places 8-12 (!MINCHESTS / !MAXCHESTS); map 8E has 30 spots
    private static byte[] SetupScript(int map, int saveFlag, int[] shopFlags, bool people)
    {
        var b = new List<byte> { ChestFlagBase & 0xFF, ChestFlagBase >> 8, 0x02, 0x06, 0x0D, 0x00, 0x01, (byte)map, 0x81, 0xFF, 0x82, 0xFF };
        for (int i = 0; i < MaxChests; i++) b.AddRange(new byte[] { (byte)(0xC1 + i), 0x94, 0xFC, (byte)i });
        if (people) b.AddRange(PeopleSetup(b.Count, saveFlag, shopFlags));   // (Lufia Forge) the priest and the merchants
        b.Add(0x00);
        return b.ToArray();
    }

    /// <summary>Events: 0 does nothing; 1 (stepping into the arrival door) opens the save menu on a checkpoint floor
    /// (<paramref name="saveFlag"/>, set by the generator); 2 (the stairs) sets flag F0 ("went down") and goes to the
    /// other cave map; 3 the priest, 4-6 the merchants (talking to characters 2-5 runs events 3-6); 7 on the spell chests' spells. <paramref name="old"/>: the map's block now (freed, 0 = none).</summary>
    private static void WriteEvents(RomBuffer rom, int map, int other, int saveFlag, int finalFlag, bool caveOnly, int old)
    {
        var events = new List<byte[]>
        {
            new byte[] { 0x00 },                                               // 0: nothing
            DoorEvent(rom, saveFlag, caveOnly),                                // 1: the arrival door: save (checkpoints), leave (normal game)
            StairsEvent(rom, other, finalFlag, caveOnly),                      // 2: set flag F0, go to the other cave map (floor 100: the Sinistrals)
            PriestEvent(rom),                                                  // 3: the priest (character 2): church menu
            ShopEvent(rom, 0), ShopEvent(rom, 1), ShopEvent(rom, 2),          // 4-6: the merchants of floors 25 / 50 / 75
        };
        var spells = Spells(rom);
        int potion = ChestPotion(rom);
        foreach (var sp in spells) events.Add(SpellEvent(rom, sp, potion));   // 4 + k: spell k of the pool
        var block = new List<byte>();
        int at0 = events.Count * 2;
        foreach (var e in events) { block.Add((byte)at0); block.Add((byte)(at0 >> 8)); at0 += e.Length; }
        foreach (var e in events) block.AddRange(e);
        var space = ExpansionSpace.Load(rom);
        if (old > 0 && space.At(old) is { Kind: ExpansionSpace.Kind.ScriptBlock } b && b.Start == old) space.Free(old);
        int at = space.Allocate(block.Count, ExpansionSpace.Kind.ScriptBlock, map, EventScript.ScriptLimit);
        space.Save();
        rom.WriteBytes(at, block.ToArray());
        int t = at - 0x18000;
        rom.WriteBytes(MapFactory.ScriptTable + map * 5, new byte[] { (byte)t, (byte)(t >> 8), (byte)(t >> 16), 0x00, 0x00 });
    }

    /// <summary>
    /// Encounter group for each floor 1-100: the ROM's groups sorted by the median EXP of their monsters (groups with
    /// pirates left out), spread over the 100 floors, as the project's groups.py does.
    /// </summary>
    public static byte[] EncountersByFloor(RomBuffer rom)
    {
        var rows = new List<(double Median, int Group)>();
        for (int g = 1; g <= Encounters.GroupCount(rom); g++)
        {
            var monsters = Encounters.GroupSubgroups(rom, g).SelectMany(s => Encounters.SubgroupMonsters(rom, s)).Where(m => m != 0xFF).ToList();
            if (monsters.Count == 0) continue;
            if (monsters.Any(m => MonsterName(rom, m).Contains("Pirate", StringComparison.OrdinalIgnoreCase))) continue;
            var exp = monsters.Select(m => (double)rom.ReadUInt16Le(MonsterTable.RecordOffset(rom, m) + 27)).OrderBy(x => x).ToList();
            double median = exp.Count % 2 == 1 ? exp[exp.Count / 2] : (exp[exp.Count / 2 - 1] + exp[exp.Count / 2]) / 2;
            rows.Add((median, g));
        }
        if (rows.Count == 0) throw new InvalidOperationException("This ROM has no encounter groups to fill the cave with.");
        rows = rows.OrderBy(r => r.Median).ThenBy(r => r.Group).ToList();
        return Enumerable.Range(0, 100).Select(i => (byte)rows[Math.Min(rows.Count - 1, i * rows.Count / 100)].Group).ToArray();
    }

    /// <summary>From $01:C0D4 (M = 8, X = 16, inside $01:C081), in place of LDA #$4F / JSR $BD70.</summary>
    private const string StartSource = """
        CaveStart:
            PLA                     ; drop the JSL's return address
            PLA
            PLA
            LDA $7E0D9E
            BNE .opening            ; FF: power-on, the opening plays as usual
            JSL CaveNewGame         ; 0: New Game from the file select -> floor 1
            JML $01C13C             ; carry on like Continue (map $1345, position $134A/C)
        .opening
            PEA $C0D8               ; $BD70's RTS comes back to $01:C0D9 (JSR $BAB4), as before
            LDA.b #$4F
            JML $01BD70
        """;

    /// <summary>From $01:8A12 (M = 8) after a game over: in a cave run, the last checkpoint floor (a new layout), with the
    /// game's usual revival of the leader and half the gold; elsewhere as before ($1349: game-over return point).</summary>
    private const string GameOverSource = """
        GameOver:
            PLA                     ; drop the JSL's return address
            PLA
            PLA
            LDA !AC_INCAVE
            BEQ .normal
            LDA !AC_CHECK
            BNE +
            LDA.b #$01
        +   STA !AC_FLOOR
            LDA $7E1296+$1E
            AND.b #$FE              ; not "went down the stairs"
            STA $7E1296+$1E
            LDA $7E077F
            CMP.b #!CAVEMAP
            BEQ +
            LDA.b #!CAVEMAP
            BRA ++
        +   LDA.b #!CAVEMAP2
        ++  STA $7E0D4C             ; the other cave map (a warp to the same map wouldn't build a new floor)
            LDA.b #$00
            STA $7E0D4B             ; arrival point 0
            JML $018A20
        .normal
            LDA $7E1349
            BNE +
            JML $018A17
        +   JML $018A3B
        """;

    /// <summary>The cave's colours: tileset 6's four palettes (as the game's caves use them), 25 floors each.</summary>
    public static readonly byte[] Themes = { 0x06, 0x07, 0x09, 0x08 };

    /// <summary>Palette for floors 1-128 (index floor - 1): 25 floors per theme.</summary>
    private static byte[] FloorPalettes() => Enumerable.Range(0, 128).Select(i => Themes[Math.Min(3, i / 25)]).ToArray();

    /// <summary>From $00:AB23 (M = 8, X = 16): $00 = the save's map. The name goes to $190D, ending with 0.</summary>
    private const string SaveNameSource = """
        SaveName:
            LDA $00
            JSR IsCaveMap
            BEQ .cave
            LDY.w #$0000            ; as before: LDY #$0000 / LDA $00, then back at $00:AB28
            LDA $00
            RTL
        .cave
            PLA                     ; drop the JSL's return address and leave through $00:AB63 (RTS)
            PLA
            PLA
            LDX.w #$0000
        -   LDA.l CaveName,x
            STA $7E190D,x
            INX
            CPX.w #5
            BCC -
            JML $00AB63
        CaveName:
            db "Cave", 0
        """;

    private static string MonsterName(RomBuffer rom, int m)
    {
        var b = rom.ReadBytes(MonsterTable.RecordOffset(rom, m), 10);
        return new string(b.TakeWhile(c => c is >= 0x20 and < 0x7F).Select(c => (char)c).ToArray());
    }

    /// <summary>
    /// The stairs: set flag F0 ("went down") and go to the other cave map. On floor 100 (<paramref name="finalFlag"/>,
    /// set by the generator) they start the battle with the four Sinistrals instead; after the victory the game is
    /// marked completed and the stairs lead back to floor 1 for a new run (the story's staff roll, map 2D event 13,
    /// needs the story's setup: run from the cave it leaves the hero on the world map).
    /// </summary>
    private static byte[] StairsEvent(RomBuffer rom, int other, int finalFlag, bool caveOnly)
    {
        var e = new List<byte> { 0x04, (byte)finalFlag, 10, 0x00, 0x06, 0xF0, 0x4C, (byte)other, 0x01, 0x00 };
        // battle; refresh the map and wait a moment, as the game's own events do; then the leader speaks (a plain text
        // box after an event's own battle is drawn as a one-line strip: the game's events always use a speaker there)
        e.AddRange(new byte[] { 0x1F, BossFormation, 0x08, 0x84 });
        foreach (var line in new[] { "All four[NL]Sinistrals fell!", "We conquered[NL]the Ancient Cave!" })
        {
            e.Add(0x89);                               // the leader speaks (one box each; 04 ends a box)
            var text = Modules.TextEditor.TextDecoder.Encode(line, rom).ToList();
            if (text.Count > 0 && text[^1] == 0x00) text.RemoveAt(text.Count - 1);
            e.AddRange(text);
            e.Add(0x04);
        }
        if (caveOnly)
        {
            e.AddRange(new byte[] { 0x50, 0x1B });     // mark the game as completed
            e.AddRange(new byte[] { 0x06, 0xF0, 0x4C, (byte)other, 0x01, 0x00 });   // down the stairs: past floor 100 the generator starts again at floor 1
        }
        else e.AddRange(new byte[] { 0x4C, ElevatorMap, ReturnArrival, 0x00 });   // the normal game: back to the elevator room, the real party again
        return e.ToArray();
    }

    /// <summary>
    /// The arrival door: on a checkpoint floor (<paramref name="saveFlag"/>) the save menu; in the normal game it then
    /// asks whether to leave the cave (back to the elevator room, the real party again).
    /// </summary>
    private static byte[] DoorEvent(RomBuffer rom, int saveFlag, bool caveOnly)
    {
        if (caveOnly) return new byte[] { 0x05, (byte)saveFlag, 0x05, 0x00, 0x4A, 0x00 };   // if the save flag isn't set, go to the end; save menu; end
        byte[] Text(string t)
        {
            var b = Modules.TextEditor.TextDecoder.Encode(t, rom).ToList();
            if (b.Count > 0 && b[^1] == 0x00) b.RemoveAt(b.Count - 1);
            b.Add(0x04);
            return b.ToArray();
        }
        var question = new List<byte> { 0x89 }; question.AddRange(Text("Leave the Ancient Cave?"));   // (the leader asks: a step-on event has no one talking)
        var answers = new List<byte> { 0x0C }; answers.AddRange(Text(" Yes.[NL] No."));
        int ask = 5, leave = ask + 8 + question.Count + answers.Count + 1, stay = leave + 4;
        var e = new List<byte> { 0x05, (byte)saveFlag, (byte)ask, 0x00, 0x4A,           // checkpoint floors: the save menu first
                                 0x03, 0x03, (byte)leave, 0x00, (byte)stay, 0x00, (byte)stay, 0x00 };
        e.AddRange(question);
        e.AddRange(answers);
        e.Add(0x00);                                                           // (the menu runs at this end, then goes to the answer's target)
        e.AddRange(new byte[] { 0x4C, ElevatorMap, ReturnArrival, 0x00 });   // leave
        e.Add(0x00);                                                           // stay
        return e.ToArray();
    }

    /// <summary>A story flag no event, setup script or game code uses (the highest such one), other than <paramref name="not"/>.</summary>
    private static int FreeFlag(RomBuffer rom, int not)
    {
        StoryFlags.BuildIndex(rom, MapCatalog.Scan(rom).Select(m => m.MapId).ToList());
        for (int f = 0xEF; f >= 0; f--) if (f != not && StoryFlags.IsFree(rom, f)) return f;
        throw new InvalidOperationException("Every story flag is in use: the Ancient Cave needs one for its save points.");
    }

    private static Asm65816 Assemble(RomBuffer rom, int map1, int map2, int saveFlag, int finalFlag, bool caveOnly, bool locked, int[] shopFlags)
    {
        var a = new Asm65816();
        a.Source($"!CAVEMAP = ${map1:X2}\n!CAVEMAP2 = ${map2:X2}\n!SAVEBYTE = ${0x7E1296 + saveFlag / 8:X6}\n!SAVEBIT = ${1 << (saveFlag % 8):X2}\n" +
                 $"!FINALBYTE = ${0x7E1296 + finalFlag / 8:X6}\n!FINALBIT = ${1 << (finalFlag % 8):X2}\n" +
                 $"!CHESTPOTION = ${ChestPotion(rom):X2}\n!FIRSTSPELLEVENT = ${FirstSpellEvent:X2}\n" +
                 LayoutDefines(CaveTemplate(rom)) + FlagDefines(shopFlags) + Source);
        a.Source(NpcSource);
        a.Source(GoldSource);
        a.Source(StartSource);
        a.Source(GameOverSource);
        a.Source(SpellSource);
        a.Source(SaveNameSource);
        a.Source(BossSource);
        a.Source(NormalSource);
        a.Source(HudSource);
        a.Label("CaveOnlyFlag").B((byte)(caveOnly ? 1 : 0));
        a.Label("LockFlag").B((byte)(locked ? 1 : 0));
        a.Source(Level1Source);
        a.Source(LootSource);
        a.Label("ChestLoot").B(ChestLoot(rom));
        a.Label("LootTier").B(LootTiers());
        a.Label("RepeatItems").B(RepeatItems(rom));
        a.Label("SpellCount").B((byte)Spells(rom).Count);
        a.Label("SpellPool").B(SpellPool(rom));
        a.Label("FloorPalette").B(FloorPalettes());
        a.Label("EncounterByFloor").B(EncountersByFloor(rom));
        a.Label("Level1Data").B(Level1Data(rom));
        a.Label("FloorTiles").B(FloorTiles());
        return a;
    }
}
