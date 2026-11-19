using LufiaForge.Modules.GameData;
using System.Security.Cryptography;
using LufiaForge.Core.Battle;
using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Cave;

// the Ancient Cave: floors built at random every time one loads (rooms, 2-wide corridors, cliffs, an arrival door,
// stairs down, 8-12 chests), from the Ancient Cave project (ac.asm, see Source).
// two map numbers (free ones, taken the way New map does) alternate as the floors, so every staircase is a real
// map change. both use their own copy of map 8E's data (the Old Cave floor: tileset 6, palette 8, 54x45) as the
// template the generator overwrites in RAM, so editing map 8E can't break the cave. their setup scripts (music,
// two step-on zones, 12 chest spots) and event scripts (stairs: set flag F0, go to the other cave map) get written
// through our own tables.
// hooks:
//   $01:BDA5  22 MapLoadHook          after a map is unpacked: a cave map gets a new floor (was JSL $00A47D)
//   $01:9969  22 PostHeader EA        after the setup script: the floor's encounters and chest contents (was LDA $92 / CMP $078E)
//   $01:C0D4  22 CaveStart            cave-only rom: a new game starts on floor 1 with the four heroes (was LDA #$4F / JSR $BD70)
//   $01:8A12  22 GameOver EA          a game over in the cave: back to the last checkpoint floor (was LDA $1349 / BNE)
//   $00:AB23  22 SaveName EA          the save screen's place name: "Cave" for the cave maps (was LDY #0 / LDA $00. the game
//                                     shows the last town, which the cave never changes)
//   $01:A376, $00:8249                the floor counter (see HudSource)
// $01:C081 runs twice: at power-on for the opening (demo flag $0D9E = FF) and again for New Game after the file
// select ($0D9E = 0, $01:853F). CaveStart only sends the second one to the cave, the opening runs as usual (the
// project's hook sent the power-on opening to the cave too, still flagged as the demo, so any button press later
// brought up the file select).
// RAM: $7F:FF20-$FF27 (floor, active, RNG, spawn), $7F:E000-$EDFF (generator), same as the project
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
    private const byte Version = 23;   // 23: the bytes the cave's map numbers had before (Remove puts them back). 22: the floor 100 battle counts its 4th group
    // block: magic, version, map 1, map 2, options (bit 0 = cave-only New Game, bit 1 = elevator start), save flag, floor 100 flag,
    // the demo's record (+10, see AncientCave.Demo.cs), 1 spare, the 4 floor 100 monsters, the boss formation as it was (4), then the code
    // (+ 20-22: the merchants' story flags, 23 spare, version 21 on. + 24-29: the map numbers' area bytes and the last two bytes of
    // their script table entries from before the cave took them (map 1, map 2), version 23 on)
    private const int Header = 32;
    private const int DemoRecord = 10, MapsBefore = 24;

    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);
    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static bool Matches(RomBuffer rom, int offset, byte[] expected) => offset >= 0 && offset + expected.Length <= rom.Length && rom.ReadBytes(offset, expected.Length).SequenceEqual(expected);

    public sealed record Info(int Map1, int Map2, bool CaveOnly, int Block, int SaveFlag, int FinalFlag, int[] Bosses, int[] OldFormation, bool ElevatorStart = false,
        bool People = false, int[]? ShopFlags = null)
    {
        public CaveMode Mode => CaveOnly ? CaveMode.CaveOnly : ElevatorStart ? CaveMode.ElevatorStart : CaveMode.NormalGame;
    }

    // how the cave is reached
    public enum CaveMode
    {
        // New Game starts on floor 1 with the four heroes at level 1, the rest of the game can't be reached
        CaveOnly,
        // the normal game, the Old Cave's elevator man (map 88) offers the way down
        NormalGame,
        // like the normal game, but New Game starts (after the prologue sets the party up) in the elevator room,
        // whose way out and elevator are closed: the cave is all there is
        ElevatorStart,
    }

    // the cave is in this rom in the given mode
    public static bool IsAppliedAs(RomBuffer rom, CaveMode mode) => Read(rom)?.Mode == mode;

    public static bool IsOriginal(RomBuffer rom) =>
        Matches(rom, MapLoadSite, MapLoadOriginal) && Matches(rom, PostHeaderSite, PostHeaderOriginal) && Matches(rom, NewGameSite, NewGameOriginal) &&
        Matches(rom, GameOverSite, GameOverOriginal) && Matches(rom, ChestSite, ChestOriginal) && Matches(rom, MsgSite, MsgOriginal) &&
        Matches(rom, SaveNameSite, SaveNameOriginal) && Matches(rom, GroupsSite, GroupsOriginal) && Matches(rom, PaletteSite, PaletteOriginal) &&
        Matches(rom, CountLoopSite, CountLoopOriginal) && Matches(rom, SetupLoopSite, SetupLoopOriginal) &&
        Matches(rom, HudOamSite, HudOamOriginal) && Matches(rom, HudNmiSite, HudNmiOriginal) && Matches(rom, GoldSite, GoldOriginal) &&
        Matches(rom, ExpSite, ExpOriginal);

    public static bool IsApplied(RomBuffer rom) => Read(rom) != null;

    // the cave is in the rom, made by an older version of this code (applying it again updates it and keeps its
    // mode, floors' flags and bosses)
    public static bool NeedsUpdate(RomBuffer rom) => Read(rom) is { } info && rom.ReadByte(info.Block + 4) < Version;

    // the cave in this rom, or null
    public static Info? Read(RomBuffer rom)
    {
        if (rom.ReadByte(MapLoadSite) != 0x22) return null;
        int hookOffset = FileOffset(rom.ReadByte(MapLoadSite + 1) | rom.ReadByte(MapLoadSite + 2) << 8 | rom.ReadByte(MapLoadSite + 3) << 16);
        for (int block = hookOffset - 4; block >= Math.Max(0, hookOffset - 0x40); block--)
            if (Matches(rom, block, Magic))
                return new Info(rom.ReadByte(block + 5), rom.ReadByte(block + 6), (rom.ReadByte(block + 7) & 1) != 0, block, rom.ReadByte(block + 8),
                    rom.ReadByte(block + 9), rom.ReadBytes(block + 12, 4).Select(x => (int)x).ToArray(), rom.ReadBytes(block + 16, 4).Select(x => (int)x).ToArray(),
                    (rom.ReadByte(block + 7) & 2) != 0, (rom.ReadByte(block + 7) & 4) != 0,
                    rom.ReadByte(block + 4) >= 21 ? rom.ReadBytes(block + 20, 3).Select(x => (int)x).ToArray() : null);
        return null;
    }

    // adds the cave (or changes its options when it's already there). caveOnly: New Game starts on floor 1
    // with the four heroes at level 1 and nothing else (the rest of the game can't be reached)
    public static void Apply(RomBuffer rom, bool caveOnly) => Apply(rom, caveOnly ? CaveMode.CaveOnly : CaveMode.NormalGame);

    // adds the cave in the given mode (or switches an applied cave to it)
    public static void Apply(RomBuffer rom, CaveMode mode) => Apply(rom, mode, null);

    // adds the cave in the given mode. people: the priest and the merchants (null = as they are, none for a
    // new cave). if it fails the rom is left as it was
    public static void Apply(RomBuffer rom, CaveMode mode, bool? people) => Atomic(rom, () => ApplyCore(rom, mode, people, force: false));

    // builds the cave's code and events again from the rom as it is now (its spell chests follow the learn
    // lists, its chests the items...). does nothing when the cave isn't in
    public static void Refresh(RomBuffer rom)
    {
        if (Read(rom) is { } info) Atomic(rom, () => ApplyCore(rom, info.Mode, info.People, force: true));
    }

    // runs edit. if it throws, the rom gets its bytes from before back (a cave half added
    // or half taken out would leave hooks pointing at nothing)
    private static void Atomic(RomBuffer rom, Action edit)
    {
        var before = rom.ToArray();
        try { edit(); }
        catch (Exception)
        {
            rom.ReplaceAll(before);
            throw;
        }
    }

    private static void ApplyCore(RomBuffer rom, CaveMode mode, bool? people, bool force)
    {
        bool caveOnly = mode == CaveMode.CaveOnly, locked = mode == CaveMode.ElevatorStart;
        var oldCave = Read(rom);
        bool withPeople = people ?? oldCave?.People ?? false;
        if (oldCave == null && !IsOriginal(rom))
            throw new InvalidOperationException("The game's map loading or New Game code ($01:BDA5, $01:9969, $01:C0D4) isn't the original, so the Ancient Cave can't be added safely.");
        if (!force && oldCave != null && oldCave.Mode == mode && oldCave.People == withPeople && rom.ReadByte(oldCave.Block + 4) == Version) return;
        CheckChestFlags(rom, oldCave);
        if (!caveOnly && (!Matches(rom, SaveSite, SaveOriginal) && rom.ReadByte(SaveSite) != 0x22 || !Matches(rom, LoadSite, LoadOriginal) && rom.ReadByte(LoadSite) != 0x22))
            throw new InvalidOperationException("The game's save / load code ($00:922B, $00:931D) isn't the original, so the cave's party backup can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) rom.Expand(MapWriter.ExpandedSize);

        int map1, map2;
        // the story flag that tells the arrival door to offer a save (one nothing else in the game uses)
        int saveFlag = oldCave?.SaveFlag ?? FreeFlag(rom, -1);
        int finalFlag = oldCave != null && oldCave.FinalFlag != 0 && oldCave.FinalFlag != oldCave.SaveFlag ? oldCave.FinalFlag : FreeFlag(rom, saveFlag);
        // floor 100: the four Sinistrals (made once), and the boss formation as it was (kept so it can go back)
        // (a cave taken out while monsters were added after its own could leave them in, then they get used again)
        int[] bosses = oldCave != null && oldCave.Bosses.Any(b => b != 0) ? oldCave.Bosses : LeftBosses(rom) ?? MakeBosses(rom);
        int[] oldFormation = oldCave != null && oldCave.Bosses.Any(b => b != 0) ? oldCave.OldFormation : Encounters.BossMonsters(rom, BossFormation);
        Encounters.SetBossMonsters(rom, BossFormation, bosses);
        int[] shopFlags = ShopFlags(rom, saveFlag, finalFlag);
        // what the two map numbers had in the area table and their script table entries' last two bytes (Remove puts them back)
        byte[] mapsBefore = oldCave != null && rom.ReadByte(oldCave.Block + 4) >= 23 ? rom.ReadBytes(oldCave.Block + MapsBefore, 6) : new byte[6];
        byte demo = oldCave != null ? rom.ReadByte(oldCave.Block + DemoRecord) : (byte)0;
        if (oldCave != null)
        {
            map1 = oldCave.Map1; map2 = oldCave.Map2;
            WriteEvents(rom, map1, map2, saveFlag, finalFlag, caveOnly, EventScript.BlockOffset(rom, map1));
            WriteEvents(rom, map2, map1, saveFlag, finalFlag, caveOnly, EventScript.BlockOffset(rom, map2));
            // (the cave's copy of map 8E and setup scripts again, so an older cave gets the people)
            ResourceWriter.Save(rom, rom.ReadByte(MapFactory.SelectTable + map1) + 0xB2, CaveTemplate(rom), allowExpand: true);
            foreach (int map in new[] { map1, map2 }) SetupScripts.Replace(rom, map, SetupScript(map, saveFlag, shopFlags, withPeople));
        }
        else
        {
            var template = CaveTemplate(rom);
            var freeIds = MapFactory.FreeIds(rom);
            if (freeIds.Count < 2) throw new InvalidOperationException("The Ancient Cave needs two free map numbers and this ROM has fewer left (each New map takes one).");
            // the project's numbers when they're free, otherwise the first free ones
            (map1, map2) = freeIds.Contains(0x85) && freeIds.Contains(0x86) ? (0x85, 0x86) : (freeIds[0], freeIds[1]);
            mapsBefore = new[] { rom.ReadByte(MapFactory.AreaTable + map1), rom.ReadByte(MapFactory.AreaTable + map2),
                                 rom.ReadByte(MapFactory.ScriptTable + map1 * 5 + 3), rom.ReadByte(MapFactory.ScriptTable + map1 * 5 + 4),
                                 rom.ReadByte(MapFactory.ScriptTable + map2 * 5 + 3), rom.ReadByte(MapFactory.ScriptTable + map2 * 5 + 4) };
            int resource = ResourceIds.Add(rom, template);
            foreach (var (map, other) in new[] { (map1, map2), (map2, map1) })
            {
                rom.WriteByte(MapFactory.SelectTable + map, (byte)(resource - 0xB2));
                rom.WriteByte(MapFactory.AreaTable + map, rom.ReadByte(MapFactory.AreaTable + TemplateMap));
                SetupScripts.Replace(rom, map, SetupScript(map, saveFlag, shopFlags, withPeople));
                WriteEvents(rom, map, other, saveFlag, finalFlag, caveOnly, 0);
            }
        }

        var freeSpace = ExpansionSpace.Load(rom);
        if (oldCave != null) freeSpace.Free(oldCave.Block);
        var probe = Assemble(rom, map1, map2, saveFlag, finalFlag, caveOnly, locked, shopFlags).Build(0x808000);
        int blockOffset = freeSpace.AllocateInOneBank(Header + probe.Length, ExpansionSpace.Kind.Patch, 32);
        freeSpace.Save();
        int blockSnes = SnesAddress(blockOffset);
        var asm = Assemble(rom, map1, map2, saveFlag, finalFlag, caveOnly, locked, shopFlags);
        rom.WriteBytes(blockOffset, Magic);
        rom.WriteBytes(blockOffset + 4, new[] { Version, (byte)map1, (byte)map2, (byte)((caveOnly ? 1 : 0) | (locked ? 2 : 0) | (withPeople ? 4 : 0)), (byte)saveFlag, (byte)finalFlag, demo, (byte)0 });
        rom.WriteBytes(blockOffset + 12, bosses.Select(b => (byte)b).Concat(oldFormation.Select(b => (byte)b)).ToArray());
        rom.WriteBytes(blockOffset + 20, shopFlags.Select(f => (byte)f).Append((byte)0).ToArray());
        rom.WriteBytes(blockOffset + MapsBefore, mapsBefore.Concat(new byte[Header - MapsBefore - 6]).ToArray());
        rom.WriteBytes(blockOffset + Header, asm.Build(blockSnes + Header));
        byte[] Jsl(string label) { int target = blockSnes + Header + asm.Offset(label); return new byte[] { 0x22, (byte)target, (byte)(target >> 8), (byte)(target >> 16) }; }
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
        rom.WriteBytes(ExpSite, Jsl("ExpHook"));
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
        rom.WriteBytes(CountLoopSite, Jsl("CountLoop")); rom.WriteByte(CountLoopSite + 4, 0xEA);
        rom.WriteBytes(SetupLoopSite, Jsl("SetupLoop")); rom.WriteByte(SetupLoopSite + 4, 0xEA);
        rom.FixChecksum();
    }

    // takes the cave out: hooks back, its code, maps (data copy, scripts) and numbers freed, and the floor 100
    // monsters, their pictures and the map copy's resource id taken out again (when nothing was added after them).
    // if it fails the rom is left as it was
    public static void Remove(RomBuffer rom) => Atomic(rom, () => RemoveCore(rom));

    private static void RemoveCore(RomBuffer rom)
    {
        var info = Read(rom);
        if (info == null) return;
        byte[] mapsBefore = rom.ReadByte(info.Block + 4) >= 23 ? rom.ReadBytes(info.Block + MapsBefore, 6) : new byte[6];
        int mapResource = LufiaMap.MapDataResource(rom, info.Map1);
        rom.WriteBytes(MapLoadSite, MapLoadOriginal);
        rom.WriteBytes(PostHeaderSite, PostHeaderOriginal);
        rom.WriteBytes(NewGameSite, NewGameOriginal);
        rom.WriteBytes(GameOverSite, GameOverOriginal);
        rom.WriteBytes(ChestSite, ChestOriginal);
        rom.WriteBytes(MsgSite, MsgOriginal);
        rom.WriteBytes(SaveNameSite, SaveNameOriginal);
        rom.WriteBytes(GroupsSite, GroupsOriginal);
        rom.WriteBytes(GoldSite, GoldOriginal);
        rom.WriteBytes(ExpSite, ExpOriginal);
        rom.WriteBytes(HudOamSite, HudOamOriginal);
        rom.WriteBytes(HudNmiSite, HudNmiOriginal);
        if (rom.ReadByte(SaveSite) == 0x22) rom.WriteBytes(SaveSite, SaveOriginal);
        if (!rom.ReadBytes(LoadSite, 4).SequenceEqual(LoadOriginal)) rom.WriteBytes(LoadSite, LoadOriginal);
        RemoveEntrance(rom);
        RemoveStart(rom);
        rom.WriteBytes(PaletteSite, PaletteOriginal);
        rom.WriteBytes(CountLoopSite, CountLoopOriginal);
        rom.WriteBytes(SetupLoopSite, SetupLoopOriginal);
        if (info.Bosses.Any(b => b != 0)) Encounters.SetBossMonsters(rom, BossFormation, info.OldFormation);
        var freeSpace = ExpansionSpace.Load(rom);
        freeSpace.Free(info.Block);
        // the map copy's resource id goes when it's the last one (its stream gets freed either way)
        if (!ResourceIds.RemoveLast(rom, mapResource))
        {
            int dataOffset = LufiaCompression.ResourceOffset(rom, mapResource);
            if (freeSpace.At(dataOffset) is { } entry && entry.Start == dataOffset && entry.Kind is ExpansionSpace.Kind.Resource or ExpansionSpace.Kind.MapData) freeSpace.Free(dataOffset);
        }
        foreach (int map in new[] { info.Map1, info.Map2 })
        {
            int block = EventScript.BlockOffset(rom, map);
            if (freeSpace.At(block) is { Kind: ExpansionSpace.Kind.ScriptBlock } scriptEntry && scriptEntry.Start == block) freeSpace.Free(block);
        }
        freeSpace.Save();
        foreach (var (map, index) in new[] { (info.Map1, 0), (info.Map2, 1) })
        {
            SetupScripts.Forget(rom, map);
            rom.WriteByte(MapFactory.SelectTable + map, 0);
            rom.WriteUInt16Le(MapFactory.SetupTable + map * 2, 0xFFFF);
            rom.WriteBytes(MapFactory.ScriptTable + map * 5, new byte[] { 0xFF, 0xFF, 0xFF, mapsBefore[2 + 2 * index], mapsBefore[3 + 2 * index] });
            rom.WriteByte(MapFactory.AreaTable + map, mapsBefore[index]);
        }
        if (info.Bosses.Any(b => b != 0)) RemoveBosses(rom, info.Bosses);
        rom.FixChecksum();
    }

    // what the cave needs from the rom

    // map 8E's data the way the generator expects it: it writes the floor's zones, chests and arrival points at fixed
    // places in the loaded map ($7F:133F counts, $7F:151C arrivals, $7F:1570 zones, $7F:1588 objects)
    private static byte[] TemplateData(RomBuffer rom)
    {
        if (!LufiaMap.IsValidMap(rom, TemplateMap, out var data, out _) || data == null)
            throw new InvalidOperationException("Map 8E (the Old Cave floor the Ancient Cave is built on) isn't in this ROM.");
        if (!SHA256.HashData(data).AsSpan().SequenceEqual(Convert.FromHexString(TemplateHash)))
            throw new InvalidOperationException("Map 8E (the Old Cave floor) was changed, and the Ancient Cave's floor builder needs its original layout. Revert map 8E to add the cave.");
        return data;
    }

    // SHA-256 of the original map 8E data (unpacked)
    private const string TemplateHash = "AFE6A9F8D88C575F065DD3355E3E3FEBB8293D9C5AA318FB30E1412D550F71B8";

    // the cave's chests use opened-flags 3C0-3DF (cleared on every new floor) and it keeps its floor and checkpoint
    // in the bytes of 3E0-3EF (saved with the game), so no other map can use them
    private static void CheckChestFlags(RomBuffer rom, Info? cave)
    {
        foreach (var map in MapCatalog.Scan(rom))
        {
            if (cave != null && (map.MapId == cave.Map1 || map.MapId == cave.Map2)) continue;
            int setupOffset = SetupScripts.Offset(rom, map.MapId);
            int baseFlag = rom.ReadUInt16Le(setupOffset);
            if (baseFlag != 0xFFFF && baseFlag + 32 > ChestFlagBase && baseFlag < ChestFlagBase + 64)
                throw new InvalidOperationException($"Map {map.MapId:X2} uses chest flags around {ChestFlagBase:X3}, which the Ancient Cave needs.");
        }
    }

    // a cave map's setup script: chest flag base, music 06, encounters (set per floor), its own events,
    // zone 0 = arrival door (event 1), zone 1 = stairs (event 2), MaxChests chests
    public const int MaxChests = 12;   // the generator places 8-12 (!MINCHESTS / !MAXCHESTS), map 8E has 30 spots
    private static byte[] SetupScript(int map, int saveFlag, int[] shopFlags, bool people)
    {
        var script = new List<byte> { ChestFlagBase & 0xFF, ChestFlagBase >> 8, 0x02, 0x06, 0x0D, 0x00, 0x01, (byte)map, 0x81, 0xFF, 0x82, 0xFF };
        for (int i = 0; i < MaxChests; i++) script.AddRange(new byte[] { (byte)(0xC1 + i), 0x94, 0xFC, (byte)i });
        if (people) script.AddRange(PeopleSetup(script.Count, saveFlag, shopFlags));   // (ours) the priest and the merchants
        script.Add(0x00);
        return script.ToArray();
    }

    // events: 0 does nothing. 1 (stepping into the arrival door) opens the save menu on a checkpoint floor
    // (saveFlag, set by the generator). 2 (the stairs) sets flag F0 ("went down") and goes to the
    // other cave map. 3 the priest, 4-6 the merchants (talking to characters 2-5 runs events 3-6). 7 on: the spell
    // chests' spells. oldBlock: the map's block now (gets freed, 0 = none)
    private static void WriteEvents(RomBuffer rom, int map, int other, int saveFlag, int finalFlag, bool caveOnly, int oldBlock)
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
        foreach (var spell in spells) events.Add(SpellEvent(rom, spell, potion));   // 4 + k: spell k of the pool
        var block = new List<byte>();
        int eventAt = events.Count * 2;
        foreach (var eventBytes in events) { block.Add((byte)eventAt); block.Add((byte)(eventAt >> 8)); eventAt += eventBytes.Length; }
        foreach (var eventBytes in events) block.AddRange(eventBytes);
        var freeSpace = ExpansionSpace.Load(rom);
        if (oldBlock > 0 && freeSpace.At(oldBlock) is { Kind: ExpansionSpace.Kind.ScriptBlock } entry && entry.Start == oldBlock) freeSpace.Free(oldBlock);
        int blockOffset = freeSpace.Allocate(block.Count, ExpansionSpace.Kind.ScriptBlock, map, EventScript.ScriptLimit);
        freeSpace.Save();
        rom.WriteBytes(blockOffset, block.ToArray());
        int pointer = blockOffset - 0x18000;
        rom.WriteBytes(MapFactory.ScriptTable + map * 5, new byte[] { (byte)pointer, (byte)(pointer >> 8), (byte)(pointer >> 16), 0x00, 0x00 });
    }

    // the cave's monster groups (pirates left out), easiest first: by the median attack of their monsters, with
    // the median EXP and the EXP of an average fight
    private sealed record CaveGroup(double Exp, double Attack, double FightExp, int Group);

    private static List<CaveGroup> CaveGroups(RomBuffer rom)
    {
        static double Median(IEnumerable<double> values) { var sorted = values.OrderBy(x => x).ToList(); return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2; }
        double Stat(int monster, int offset) => rom.ReadUInt16Le(MonsterTable.RecordOffset(rom, monster) + offset);
        var rows = new List<CaveGroup>();
        for (int group = 1; group <= Encounters.GroupCount(rom); group++)
        {
            var subgroups = Encounters.GroupSubgroups(rom, group).Select(s => Encounters.SubgroupMonsters(rom, s).Where(m => m != 0xFF).ToList()).Where(l => l.Count > 0).ToList();
            var monsters = subgroups.SelectMany(l => l).ToList();
            if (monsters.Count == 0) continue;
            if (monsters.Any(m => MonsterName(rom, m).Contains("Pirate", StringComparison.OrdinalIgnoreCase))) continue;
            // (a fight's EXP counts rare treasure monsters at most 5000, far above their group's)
            rows.Add(new CaveGroup(Median(monsters.Select(m => Stat(m, 27))), Median(monsters.Select(m => Stat(m, 20))),
                subgroups.Average(l => l.Sum(m => Math.Min(Stat(m, 27), 5000))), group));
        }
        if (rows.Count == 0) throw new InvalidOperationException("This ROM has no encounter groups to fill the cave with.");
        return rows.OrderBy(r => r.Attack).ThenBy(r => r.Exp).ThenBy(r => r.Group).ToList();
    }

    // how hard a floor's monsters can hit: their median attack at most this share of the Hero's HP at the level
    // the party should have by then (in the normal game the party meets a group at about 0.65-0.75), a little more
    // the deeper the floor (0.70 on floor 1, 0.85 on floor 100)
    private static double Difficulty(int floor) => 0.70 + 0.0015 * floor;
    private const int FightsPerFloor = 4;

    // the group (index into CaveGroups) for each floor 1-100. the party starts at level 1 and only levels
    // up in the cave, so the floors follow the level it should have: we play through a run of FightsPerFloor fights a
    // floor (EXP x ExpMultiplier), and a floor only moves on to harder groups (at most 2 a floor) while their median
    // attack is at most Difficulty x the Hero's HP at that level. where the levels come slowly, every 4th floor of the
    // same group gets the next one if it's within 0.12 more, and the last floors catch up to the hardest groups if the
    // run is behind (at least the ((floor - 1) / 99)^3 share of them). once there, the floors take turns with the 3
    // hardest groups
    private static int[] GroupIndexByFloor(RomBuffer rom, List<CaveGroup> rows)
    {
        var hero = LevelGrowth.Simulate(LevelGrowth.Read(rom, 0));
        var index = new int[100];
        long exp = 0;
        int groupIndex = 0, sameCount = 0;
        for (int floor = 1; floor <= 100; floor++)
        {
            int hp = hero.Last(l => l.ExpToReach <= exp).Stats[0];
            int previous = groupIndex;
            for (int steps = 0; steps < 2 && groupIndex + 1 < rows.Count && rows[groupIndex + 1].Attack <= Difficulty(floor) * hp; steps++) groupIndex++;
            groupIndex = Math.Max(groupIndex, (int)Math.Round((rows.Count - 1) * Math.Pow((floor - 1) / 99.0, 3)));
            sameCount = groupIndex == previous ? sameCount + 1 : 0;
            int floorIndex = groupIndex;
            if (sameCount % 4 == 3 && groupIndex + 1 < rows.Count && rows[groupIndex + 1].Attack <= (Difficulty(floor) + 0.12) * hp) floorIndex = groupIndex + 1;
            if (groupIndex == rows.Count - 1) floorIndex = Math.Max(0, groupIndex - floor % 3);     // the deepest floors take turns with the top 3
            index[floor - 1] = floorIndex;
            exp += (long)(rows[floorIndex].FightExp * FightsPerFloor * ExpMultiplier);
        }
        return index;
    }

    // encounter group for each floor 1-100 (see GroupIndexByFloor)
    public static byte[] EncountersByFloor(RomBuffer rom)
    {
        var rows = CaveGroups(rom);
        return GroupIndexByFloor(rom, rows).Select(i => (byte)rows[i].Group).ToArray();
    }

    // from $01:C0D4 (M = 8, X = 16, inside $01:C081), in place of LDA #$4F / JSR $BD70
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

    // from $01:8A12 (M = 8) after a game over: in a cave run, the last checkpoint floor (a new layout), with the
    // game's usual revival of the leader and half the gold. anywhere else as before ($1349: game over return point)
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

    // the cave's colours: tileset 6's four palettes (the way the game's caves use them), 25 floors each
    public static readonly byte[] Themes = { 0x06, 0x07, 0x09, 0x08 };

    // palette for floors 1-128 (index floor - 1), 25 floors per theme
    private static byte[] FloorPalettes() => Enumerable.Range(0, 128).Select(i => Themes[Math.Min(3, i / 25)]).ToArray();

    // from $00:AB23 (M = 8, X = 16): $00 = the save's map. the name goes to $190D, ending with 0
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

    private static string MonsterName(RomBuffer rom, int monster)
    {
        var nameBytes = rom.ReadBytes(MonsterTable.RecordOffset(rom, monster), 10);
        return new string(nameBytes.TakeWhile(c => c is >= 0x20 and < 0x7F).Select(c => (char)c).ToArray());
    }

    // the stairs: set flag F0 ("went down") and go to the other cave map. on floor 100 (finalFlag,
    // set by the generator) they start the battle with the four Sinistrals instead. after the victory the game gets
    // marked completed and the stairs lead back to floor 1 for a new run (the story's staff roll, map 2D event 13,
    // needs the story's setup, run from the cave it leaves the hero on the world map)
    private static byte[] StairsEvent(RomBuffer rom, int other, int finalFlag, bool caveOnly)
    {
        var script = new List<byte> { 0x04, (byte)finalFlag, 10, 0x00, 0x06, 0xF0, 0x4C, (byte)other, 0x01, 0x00 };
        // battle, refresh the map and wait a moment like the game's own events do, then the leader speaks (a plain text
        // box after an event's own battle gets drawn as a one-line strip, so the game's events always use a speaker there)
        script.AddRange(new byte[] { 0x1F, BossFormation, 0x08, 0x84 });
        foreach (var line in new[] { "All four[NL]Sinistrals fell!", "We conquered[NL]the Ancient Cave!" })
        {
            script.Add(0x89);                               // the leader speaks (one box each, 04 ends a box)
            var text = Modules.TextEditor.TextDecoder.Encode(line, rom).ToList();
            if (text.Count > 0 && text[^1] == 0x00) text.RemoveAt(text.Count - 1);
            script.AddRange(text);
            script.Add(0x04);
        }
        if (caveOnly)
        {
            script.AddRange(new byte[] { 0x50, 0x1B });     // mark the game as completed
            script.AddRange(new byte[] { 0x06, 0xF0, 0x4C, (byte)other, 0x01, 0x00 });   // down the stairs: past floor 100 the generator starts again at floor 1
        }
        else script.AddRange(new byte[] { 0x4C, ElevatorMap, ReturnArrival, 0x00 });   // the normal game: back to the elevator room, the real party again
        return script.ToArray();
    }

    // the arrival door: on a checkpoint floor (saveFlag) the save menu. in the normal game it then
    // asks whether to leave the cave (back to the elevator room, the real party again)
    private static byte[] DoorEvent(RomBuffer rom, int saveFlag, bool caveOnly)
    {
        if (caveOnly) return new byte[] { 0x05, (byte)saveFlag, 0x05, 0x00, 0x4A, 0x00 };   // if the save flag isn't set go to the end, save menu, end
        byte[] Text(string text)
        {
            var bytes = Modules.TextEditor.TextDecoder.Encode(text, rom).ToList();
            if (bytes.Count > 0 && bytes[^1] == 0x00) bytes.RemoveAt(bytes.Count - 1);
            bytes.Add(0x04);
            return bytes.ToArray();
        }
        var question = new List<byte> { 0x89 }; question.AddRange(Text("Leave the Ancient Cave?"));   // (the leader asks, a step-on event has no one talking)
        var answers = new List<byte> { 0x0C }; answers.AddRange(Text(" Yes.[NL] No."));
        int ask = 5, leave = ask + 8 + question.Count + answers.Count + 1, stay = leave + 4;
        var script = new List<byte> { 0x05, (byte)saveFlag, (byte)ask, 0x00, 0x4A,           // checkpoint floors: the save menu first
                                 0x03, 0x03, (byte)leave, 0x00, (byte)stay, 0x00, (byte)stay, 0x00 };
        script.AddRange(question);
        script.AddRange(answers);
        script.Add(0x00);                                                           // (the menu runs at this end, then goes to the answer's target)
        script.AddRange(new byte[] { 0x4C, ElevatorMap, ReturnArrival, 0x00 });   // leave
        script.Add(0x00);                                                           // stay
        return script.ToArray();
    }

    // a story flag no event, setup script or game code uses (the highest one like that), other than not
    private static int FreeFlag(RomBuffer rom, int not)
    {
        StoryFlags.BuildIndex(rom, MapCatalog.Scan(rom).Select(m => m.MapId).ToList());
        for (int flag = 0xEF; flag >= 0; flag--) if (flag != not && StoryFlags.IsFree(rom, flag)) return flag;
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
        a.Source(ExpSource);
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
        a.Label("LootTier").B(LootTiers(rom));
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
