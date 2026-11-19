using LufiaForge.Core.Maps;
using LufiaForge.Modules.TextEditor;

namespace LufiaForge.Core.Cave;

public static partial class AncientCave
{
    // people on some floors: a priest (the church menu: revive, cure, save) beside the arrival door on the save floors
    // (11, 21 ... 91), and a merchant on floors 25, 50 and 75 (one of the game's item shops that suits the depth).
    // the cave's copy of map 8E gets 6 characters. talking to character n runs event n + 1 of the map's script block
    // (seen in game), and events 1 and 2 are the arrival door and the stairs (step-on areas 0 and 1), so characters 0
    // and 1 are never shown. 2 is the priest (event 3), 3-5 the three merchants (events 4-6). the setup script shows
    // the priest when the save flag is set and merchant k when shop flag k is set, the generator sets the flags and
    // puts them on open floor by the door
    public const int PriestNpc = 2, FirstShopNpc = 3, CaveNpcCount = 6;

    // the cave has the priest and the merchants (it's an option, see SetPeople)
    public static bool HasPeople(RomBuffer rom) => Read(rom)?.People == true;

    // puts the priest and the merchants in (or takes them out of) the cave that's in the rom
    public static void SetPeople(RomBuffer rom, bool on)
    {
        var info = Read(rom) ?? throw new InvalidOperationException("Add one of the Ancient Cave patches first.");
        Apply(rom, info.Mode, on);
    }
    public static readonly int[] ShopFloors = { 25, 50, 75 };
    // the game's item shops used on ShopFloors (Hi Potion / Stone Cure..., the special
    // arrows and Hi Magic, Ex Potion / Ex Magic)
    public static readonly int[] CaveShops = { 0x18, 0x29, 0x3D };
    private const int PriestSprite = 0x0E, MerchantSprite = 0x08;   // (the white robe some churches use, and the most common shopkeeper)

    // map 8E's data (checked) with the cave's 7 characters added (section C)
    private static byte[] CaveTemplate(RomBuffer rom)
    {
        var original = TemplateData(rom);
        var map = LufiaMap.Load(rom, TemplateMap);
        for (int i = 0; i < CaveNpcCount; i++)
            map.Npcs.Add(new MapNpc { Index = i, Sprite = i == PriestNpc ? PriestSprite : i >= FirstShopNpc ? MerchantSprite : PriestSprite, Flags = 0x20, X = 0, Y = 0, BoxX1 = 0, BoxY1 = 0, BoxX2 = 1, BoxY2 = 1 });
        var data = map.BuildData();
        var (objectBlock, offsets) = Layout(data);
        var (originalBlock, originalOffsets) = Layout(original);
        if (objectBlock != originalBlock || offsets[1] != originalOffsets[1] || data.Length < original.Length)
            throw new InvalidOperationException("Adding the cave's people to its copy of map 8E moved its arrival points; the floor builder can't use it.");
        return data;
    }

    // a map's object block offset and its 8 section offsets (relative to the block)
    private static (int Ob, int[] Offsets) Layout(byte[] data)
    {
        int objectBlock = data[0x30] | data[0x31] << 8 | data[0x32] << 16 | data[0x33] << 24;
        return (objectBlock, Enumerable.Range(0, 8).Select(i => data[objectBlock + 8 + 2 * i] | data[objectBlock + 9 + 2 * i] << 8).ToArray());
    }

    // where the generator finds things in the loaded copy (unpacked to $7F:0000): arrival points, the
    // characters, step-on areas and spots
    private static string LayoutDefines(byte[] data)
    {
        var (objectBlock, offsets) = Layout(data);
        int At(int section) => 0x7F0000 + objectBlock + offsets[section];
        return $"!ENTRANCES = ${At(1):X6}\n!NPCS = ${At(2):X6}\n!ZONES = ${At(3):X6}\n!OBJS = ${At(4):X6}\n";
    }

    // three story flags no event, setup script or game code uses (not the cave's save and floor 100 ones)
    private static int[] ShopFlags(RomBuffer rom, int saveFlag, int finalFlag)
    {
        StoryFlags.BuildIndex(rom, MapCatalog.Scan(rom).Select(m => m.MapId).ToList());
        var list = new List<int>();
        for (int flag = 0xEF; flag >= 0 && list.Count < 3; flag--)
            if (flag != saveFlag && flag != finalFlag && StoryFlags.IsFree(rom, flag)) list.Add(flag);
        if (list.Count < 3) throw new InvalidOperationException("Not enough unused story flags for the cave's merchants.");
        return list.ToArray();
    }

    private static string FlagDefines(int[] shopFlags) => string.Concat(shopFlags.Select((f, k) =>
        $"!SHOPBYTE{k} = ${0x7E1296 + f / 8:X6}\n!SHOPBIT{k} = ${1 << (f % 8):X2}\n"));

    // the setup script's part for the people: the priest when the save flag is set, merchant k when shop flag k
    // is (05 ff: test the flag, 04 ww: skip the next command when it isn't set, 06 is the opposite test). start = where
    // it starts in the script (jumps count from the script's start)
    private static byte[] PeopleSetup(int start, int saveFlag, int[] shopFlags)
    {
        var bytes = new List<byte>();
        foreach (var (flag, npc) in new[] { (saveFlag, PriestNpc) }.Concat(shopFlags.Select((f, k) => (f, FirstShopNpc + k))))
        {
            int next = start + bytes.Count + 6;
            bytes.AddRange(new byte[] { 0x05, (byte)flag, 0x04, (byte)next, (byte)(next >> 8), (byte)(SetupScripts.CharacterOn + npc) });
        }
        return bytes.ToArray();
    }

    private static byte[] EventText(RomBuffer rom, string text)
    {
        var bytes = TextDecoder.Encode(text, rom).ToList();
        if (bytes.Count > 0 && bytes[^1] == 0x00) bytes.RemoveAt(bytes.Count - 1);
        bytes.Add(0x04);
        return bytes.ToArray();
    }

    // event 3: the priest's church menu (revive, cure, save)
    private static byte[] PriestEvent(RomBuffer rom)
    {
        var script = new List<byte> { 0x0C };
        script.AddRange(EventText(rom, "Rest a while, travelers."));
        script.AddRange(new byte[] { 0x1E, 0x00 });
        return script.ToArray();
    }

    // events 4-6: merchant's shop
    private static byte[] ShopEvent(RomBuffer rom, int merchant)
    {
        var script = new List<byte> { 0x0C };
        script.AddRange(EventText(rom, "Even down here, a merchant[NL]has to make a living."));
        script.AddRange(new byte[] { 0x1C, (byte)CaveShops[merchant], 0x00 });
        return script.ToArray();
    }

    // the generator's part: shop flags for the floor (set along with the floor 100 flag), and PlaceNpcs (after the
    // doorways: the priest and the merchants on open floor near the arrival spot, cells marked taken like chests)
    private const string NpcSource = """
        SetShopFlags:               ; M = 8: shop flag k on floor 25 / 50 / 75 only
            LDA !SHOPBYTE0
            AND.b #!SHOPBIT0^$FF
            STA !SHOPBYTE0
            LDA !SHOPBYTE1
            AND.b #!SHOPBIT1^$FF
            STA !SHOPBYTE1
            LDA !SHOPBYTE2
            AND.b #!SHOPBIT2^$FF
            STA !SHOPBYTE2
            LDA !AC_FLOOR
            CMP.b #25
            BNE +
            LDA !SHOPBYTE0
            ORA.b #!SHOPBIT0
            STA !SHOPBYTE0
            RTS
        +   CMP.b #50
            BNE +
            LDA !SHOPBYTE1
            ORA.b #!SHOPBIT1
            STA !SHOPBYTE1
            RTS
        +   CMP.b #75
            BNE +
            LDA !SHOPBYTE2
            ORA.b #!SHOPBIT2
            STA !SHOPBYTE2
        +   RTS

        NpcSpots:                   ; (dx, dy) from the arrival spot, nearest first
            db 2,0, -2,0, 2,1, -2,1, 1,2, -1,2, 3,0, -3,0, 2,2, -2,2, 3,1, -3,1, 0,3, 3,2, -3,2, 1,3, -1,3
        !NPCSPOTS = 34              ; (17 spots x 2 bytes)

        PlaceNpcs:                  ; DB = $7F, M = 8, X = 16: the priest (character 2) and the merchants (3-5)
            LDA.b #$00
            STA !npcn
            LDX.w #$0000
        .try
            CPX.w #!NPCSPOTS
            BCC +
            RTS
        +   PHX
            REP #$20
            LDA.l NpcSpots,x        ; x = spawn x + dx
            AND.w #$00FF
            CMP.w #$0080
            BCC +
            ORA.w #$FF00
        +   CLC
            ADC !AC_SPAWNX
            STA !t0
            LDA.l NpcSpots+1,x
            AND.w #$00FF
            CMP.w #$0080
            BCC +
            ORA.w #$FF00
        +   CLC
            ADC !AC_SPAWNY
            STA !t1
            LDA !t0                 ; inside the map
            CMP.w #!MAPW
            BCS .bad
            LDA !t1
            CMP.w #!MAPH
            BCS .bad
            JSR FAddr
            SEP #$20
            LDA !FGRID,x
            CMP.b #$01
            BNE .bad8
            LDA.b #$02              ; taken (no chest next to it either)
            STA !FGRID,x
            LDA !npcn
            BNE .shops
            LDA.b #2                ; the priest
            JSR PutNpc
            BRA .found
        .shops
            LDA.b #3
            JSR PutNpc
            LDA.b #4
            JSR PutNpc
            LDA.b #5
            JSR PutNpc
        .found
            LDA !npcn
            INC A
            STA !npcn
            PLX
            CMP.b #2
            BCC .next
            RTS
        .bad
            SEP #$20
        .bad8
            PLX
        .next
            INX
            INX
            JMP .try

        PutNpc:                     ; A = character: its record's place and box = (!t0, !t1)
            PHX
            REP #$20
            AND.w #$00FF
            STA !npcx               ; x 14
            ASL #3
            SEC
            SBC !npcx
            ASL
            TAX
            LDA !t0
            STA.l !NPCS+2,x
            STA.l !NPCS+6,x
            INC A
            STA.l !NPCS+10,x
            LDA !t1
            STA.l !NPCS+4,x
            STA.l !NPCS+8,x
            INC A
            STA.l !NPCS+12,x
            SEP #$20
            PLX
            RTS
        """;
}
