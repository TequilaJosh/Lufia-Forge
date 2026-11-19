using LufiaForge.Core.Maps;

namespace LufiaForge.Core.Battle;

// pictures behind the fights. lufia 1 has no battle backgrounds: a fight scrolls the field map to 0, redraws it and
// darkens its colours, then puts the monsters (sprites) and the windows on top. after the fight the game reloads the
// map's tiles, layout and colours by itself, so a picture can take the map's place for as long as the fight lasts.
//
// the screen in a fight (mode 1):
// - BG2 = the map's ground layer (layout in RAM at $7E:3000, 64 x 32, video memory $0800). the right half is a clean
//   copy of the left that the fight puts back when a window closes, and spell colour flashes tint the darkened
//   colour copy at $7E:FF00
// - BG1 = its upper layer (the world map's clouds, $7E:2000, video memory $0000)
// both draw tiles from video memory $1000 (512 tiles, tile 0x200 and up are the window frames the fight loads at
// $3000). colours 32-127 (palettes 2-7) are the map's, 0-31 the windows and text, 128-255 the monsters.
//
// the hook runs once the fight's opening is over: the stretch effect is done (it waits for frame 1C), the map's
// colours are faded down (the fade runs every other frame from the colour copy at $7E:033F, so colours put in
// earlier get faded over) and the windows are drawn:
//   $08:81AF  22 Hook EA EA   (was LDA #$00 / STA $7EDA09)
// it looks up the current map ($077F) in a 256-byte table. for a background it rewrites the layouts in RAM (map
// tiles replaced, window tiles kept) and, with the screen blanked for a frame, copies the picture's tiles, both
// layouts and its colours in.
//
// the colour fade buffer $7E:FF00 holds the darkened map colours and the end of the fight fades back up by adding
// its steps onto them. so the hook keeps them at $7F:FE00 (the game never writes there) and a third hook puts them
// back before that fade, with the screen off until the map is back:
//   $08:81F2  22 Exit EA EA   (was LDA #$02 / STA $C2 / STZ $B7)
//
// the game doesn't reload the map's tiles after a fight (a fight never touched them), so a second hook does that
// where the field code carries on after the fight. it uses the map loader's own routines ($01:BE2E unpacks the
// map's tiles to $7E:9000, $01:BE57 copies them in with the screen off and refills the colour copy), reached
// through the RTL at $01:AF2C:
//   $01:894E  22 Leave EA     (was LDA #$00 / STA $0548)
//
// our block: the code, the map table (256 bytes: background number from 1, 0 = none), 64 3-byte pointers
// and 64 16-byte names. background data (one bank each): word tile bytes, 96 colours (palettes 2-7), 32 x 28 layout
// words, then the tiles
public static partial class BattleBackgrounds
{
    public const int Width = 256, Height = 224;
    public const int MaxTiles = 512, Palettes = 6, FirstPalette = 2;
    private const int Site = 0x401AF;                    // $08:81AF
    private static readonly byte[] Original = { 0xA9, 0x00, 0x8F, 0x09, 0xDA, 0x7E };
    private const int LeaveSite = 0x894E;                // $01:894E
    private const int ExitSite = 0x401F2;                // $08:81F2
    private static readonly byte[] ExitOriginal = { 0xA9, 0x02, 0x85, 0xC2, 0x64, 0xB7 };
    private static readonly byte[] LeaveOriginal = { 0xA9, 0x00, 0x8D, 0x48, 0x05 };
    public const int MaxBackgrounds = 64, NameLength = 16;
    private const int MapTable = 0x200, PointerTable = 0x300, NameTable = PointerTable + 3 * MaxBackgrounds, BlockSize = NameTable + NameLength * MaxBackgrounds;
    private const int PaletteAt = 2, LayoutAt = PaletteAt + Palettes * 32, TilesAt = LayoutAt + 32 * 28 * 2;

    private static int SnesAddress(int fileOffset) => (fileOffset / 0x8000 | 0x80) << 16 | (fileOffset % 0x8000 | 0x8000);
    private static int FileOffset(int snesAddress) => (snesAddress >> 16 & 0x7F) * 0x8000 + (snesAddress & 0x7FFF);

    private static string HookSource(int maps, int pointers) => $$"""
        !MAPS = ${{maps:X6}}
        !PTRS = ${{pointers:X6}}
        Hook:                       ; M = 8, X = 16, DB = 08, D = 0
            LDA.b #$00
            STA $7EDA09             ; (replaced)
            PHP
            PHB
            REP #$30
            LDA $077F
            AND.w #$00FF
            TAX
            SEP #$20
            LDA !MAPS,x
            BNE .have
            PLB
            PLP
            RTL
        .have
            REP #$30
            PEI ($00)
            PEI ($02)
            AND.w #$00FF
            DEC
            STA $00
            ASL
            CLC
            ADC $00
            TAX
            LDA !PTRS,x
            STA $00
            LDA !PTRS+1,x
            STA $01
            PEA $7E7E
            PLB
            PLB
            ; ground layer: map tiles become the picture (rows 0-27) or empty, windows stay; in both halves, as
            ; the fight keeps a clean copy of the screen in the right half and puts it back when a window closes
            LDX.w #$0000
        .ground
            LDA $3000,x
            AND.w #$03FF
            CMP.w #$0200
            BCS .keepGround
            TXA
            AND.w #$07FF
            CMP.w #$0700
            BCS .empty
            CLC
            ADC.w #{{LayoutAt}}
            TAY
            LDA [$00],y
            BRA +
        .empty
            LDA.w #$0000
        +   STA $3000,x
        .keepGround
            INX
            INX
            CPX.w #$1000
            BCC .ground
            ; upper layer: emptied, windows stay
            LDX.w #$0000
        .upper
            LDA $2000,x
            AND.w #$03FF
            CMP.w #$0200
            BCS +
            STZ $2000,x
        +   INX
            INX
            CPX.w #$1000
            BCC .upper
            PEA $0000
            PLB
            PLB
            SEP #$20
            LDA $38                 ; just after a refresh
        -   CMP $38
            BEQ -
            LDA.b #$80              ; screen off while the copies run
            STA $2100
            STA $2115
            LDA.b #$01
            STA $4350
            LDA.b #$18
            STA $4351
            ; tiles
            LDX.w #$1000
            STX $2116
            REP #$20
            LDA $00
            CLC
            ADC.w #{{TilesAt}}
            STA $4352
            LDA [$00]
            STA $4355
            SEP #$20
            LDA $02
            STA $4354
            LDA.b #$20
            STA $420B
            ; upper layer
            LDX.w #$0000
            STX $2116
            LDX.w #$2000
            STX $4352
            LDA.b #$7E
            STA $4354
            LDX.w #$1000
            STX $4355
            LDA.b #$20
            STA $420B
            ; ground layer
            LDX.w #$0800
            STX $2116
            LDX.w #$3000
            STX $4352
            LDX.w #$1000
            STX $4355
            LDA.b #$20
            STA $420B
            ; colours 32-127
            LDA.b #$20
            STA $2121
            STZ $4350
            LDA.b #$22
            STA $4351
            REP #$20
            LDA $00
            CLC
            ADC.w #{{PaletteAt}}
            STA $4352
            LDA.w #$00C0
            STA $4355
            SEP #$20
            LDA $02
            STA $4354
            LDA.b #$20
            STA $420B
            ; and in the colour fade buffer ($7E:FF00, which spell flashes tint), the darkened map colours there
            ; kept at $7F:FE00 for the fade back up at the end
            REP #$30
            LDX.w #$0000            ; (the darkened map colours kept for the fade back up)
        -   LDA $7EFF40,x
            STA $7FFE00,x
            INX
            INX
            CPX.w #$00C0
            BCC -
            LDY.w #{{PaletteAt}}
        -   LDA [$00],y
            TYX
            STA ${{0x7EFF40 - PaletteAt:X6}},x
            INY
            INY
            CPY.w #{{PaletteAt + 0xC0}}
            BCC -
            SEP #$20
            LDA $38                 ; screen back on at the next refresh
        -   CMP $38
            BEQ -
            LDA.b #$0F
            STA $2100
            REP #$30
            PLA
            STA $02
            PLA
            STA $00
            PLB
            PLP
            RTL

        Exit:                       ; the fight is over, before the fade back up: M = 8, X = 16, DB = 08
            LDA.b #$02
            STA $C2
            STZ $B7                 ; (replaced)
            PHP
            PHB
            REP #$30
            LDA $077F
            AND.w #$00FF
            TAX
            SEP #$20
            LDA !MAPS,x
            BEQ +
            REP #$30                ; the darkened map colours back in the fade buffer, which the fade adds its
            LDX.w #$0000            ; steps back onto
        -   LDA $7FFE00,x
            STA $7EFF40,x
            INX
            INX
            CPX.w #$00C0
            BCC -
            SEP #$20
            LDA.b #$80              ; screen off through the fade (it would show the picture in the map's
            STA $002100             ; colours); on again once the map is back
        +   PLB
            PLP
            RTL

        Leave:                      ; just after the fight, D = 0
            LDA.b #$00
            STA $0548               ; (replaced)
            PHP
            PHB
            SEP #$20
            REP #$10
            PEA $0000
            PLB
            PLB
            REP #$30
            LDA $077F
            AND.w #$00FF
            TAX
            SEP #$20
            LDA !MAPS,x
            BEQ +
            JSL Unpack              ; the map's tiles again
            JSL Upload
            SEP #$20
            LDA $38                 ; screen back on (the loader leaves it off) at the next refresh
        -   CMP $38
            BEQ -
            LDA.b #$0F
            STA $2100
        +   PLB
            PLP
            RTL
        Unpack:
            PEA $AF2B               ; its RTS goes to the RTL at $01:AF2C
            JML $01BE2E
        Upload:
            PEA $AF2B
            JML $01BE57
        """;

    // the hook is in
    public static bool IsApplied(RomBuffer rom) => rom.ReadByte(Site) == 0x22;

    private static int Block(RomBuffer rom) => FileOffset(rom.ReadByte(Site + 1) | rom.ReadByte(Site + 2) << 8 | rom.ReadByte(Site + 3) << 16);

    // a battle background the way Lufia Forge keeps it: a name and the picture
    public sealed record Background(string Name, Converted Picture);

    // the backgrounds in the rom and the map table (map number to background number from 1, 0 = none).
    // empty and all zeros when the hooks aren't in
    public static (List<Background> Backgrounds, byte[] Maps) Read(RomBuffer rom)
    {
        var list = new List<Background>();
        var maps = new byte[256];
        if (!IsApplied(rom)) return (list, maps);
        int block = Block(rom);
        maps = rom.ReadBytes(block + MapTable, 256);
        for (int i = 0; i < MaxBackgrounds; i++)
        {
            int snesAddress = ReadPointer(rom, block, i);
            if (snesAddress == 0) break;
            int dataOffset = FileOffset(snesAddress);
            int size = rom.ReadUInt16Le(dataOffset);
            var colours = Enumerable.Range(0, Palettes * 16).Select(c => rom.ReadUInt16Le(dataOffset + PaletteAt + c * 2)).ToArray();
            var layout = Enumerable.Range(0, TilesX * TilesY).Select(t => rom.ReadUInt16Le(dataOffset + LayoutAt + t * 2)).ToArray();
            var tiles = rom.ReadBytes(dataOffset + TilesAt, size);
            int paletteCount = Enumerable.Range(0, Palettes).Count(p => Enumerable.Range(1, 15).Any(c => colours[p * 16 + c] != 0));
            var nameBytes = rom.ReadBytes(block + NameTable + i * NameLength, NameLength);
            string name = new string(nameBytes.TakeWhile(c => c != 0 && c != 0xFF).Select(c => (char)c).ToArray()).Trim();
            list.Add(new Background(name.Length > 0 ? name : $"Background {i + 1}", new Converted(tiles, layout, colours, size / 32, Math.Max(1, paletteCount))));
        }
        return (list, maps);
    }

    private static int ReadPointer(RomBuffer rom, int block, int index) =>
        rom.ReadByte(block + PointerTable + index * 3) | rom.ReadByte(block + PointerTable + index * 3 + 1) << 8 | rom.ReadByte(block + PointerTable + index * 3 + 2) << 16;

    // puts the backgrounds in (replacing what was there). maps is the map table, map number to
    // background number from 1 (0 = the darkened map like the game does). with no backgrounds the hooks come out
    public static void Apply(RomBuffer rom, IReadOnlyList<Background> backgrounds, byte[] maps)
    {
        if (backgrounds.Count > MaxBackgrounds) throw new InvalidOperationException($"At most {MaxBackgrounds} battle backgrounds.");
        if (backgrounds.Count == 0) { Remove(rom); return; }
        if (!IsApplied(rom) && (!rom.ReadBytes(Site, Original.Length).SequenceEqual(Original) || !rom.ReadBytes(ExitSite, ExitOriginal.Length).SequenceEqual(ExitOriginal)
            || !rom.ReadBytes(LeaveSite, LeaveOriginal.Length).SequenceEqual(LeaveOriginal)))
            throw new InvalidOperationException("The battle setup ($08:81AF), the end of a fight ($08:81F2) or the field code after a fight ($01:894E) isn't the original, so battle backgrounds can't be added safely.");
        if (rom.Length < MapWriter.ExpandedSize) throw new InvalidOperationException("Battle backgrounds need the expanded ROM (2 MB).");
        FreeAll(rom);
        var freeSpace = ExpansionSpace.Load(rom);
        int block = freeSpace.AllocateInOneBank(BlockSize, ExpansionSpace.Kind.Patch, 0);
        var pointers = new byte[3 * MaxBackgrounds];
        var names = new byte[NameLength * MaxBackgrounds];
        for (int i = 0; i < backgrounds.Count; i++)
        {
            var picture = backgrounds[i].Picture;
            var data = new byte[TilesAt + picture.Tiles.Length];
            data[0] = (byte)picture.Tiles.Length; data[1] = (byte)(picture.Tiles.Length >> 8);
            for (int c = 0; c < Palettes * 16; c++) { data[PaletteAt + c * 2] = (byte)picture.Colours[c]; data[PaletteAt + c * 2 + 1] = (byte)(picture.Colours[c] >> 8); }
            for (int t = 0; t < picture.Layout.Length; t++) { data[LayoutAt + t * 2] = (byte)picture.Layout[t]; data[LayoutAt + t * 2 + 1] = (byte)(picture.Layout[t] >> 8); }
            picture.Tiles.CopyTo(data, TilesAt);
            int dataOffset = freeSpace.AllocateInOneBank(data.Length, ExpansionSpace.Kind.Patch, 0);
            rom.WriteBytes(dataOffset, data);
            int snesAddress = SnesAddress(dataOffset);
            pointers[i * 3] = (byte)snesAddress; pointers[i * 3 + 1] = (byte)(snesAddress >> 8); pointers[i * 3 + 2] = (byte)(snesAddress >> 16);
            var nameBytes = backgrounds[i].Name.Where(c => c >= ' ' && c < (char)0x7F).Take(NameLength).Select(c => (byte)c).ToArray();
            nameBytes.CopyTo(names, i * NameLength);
        }
        freeSpace.Save();
        var table = new byte[256];
        for (int map = 0; map < 256 && map < maps.Length; map++) table[map] = maps[map] <= backgrounds.Count ? maps[map] : (byte)0;
        int snesBlock = SnesAddress(block);
        var asm = new Asm65816();
        asm.Source(HookSource(snesBlock + MapTable, snesBlock + PointerTable));
        var code = asm.Build(snesBlock);
        if (code.Length > MapTable) throw new InvalidOperationException("The battle background code doesn't fit.");
        rom.WriteBytes(block, code);
        rom.WriteBytes(block + MapTable, table);
        rom.WriteBytes(block + PointerTable, pointers);
        rom.WriteBytes(block + NameTable, names);
        rom.WriteBytes(Site, new byte[] { 0x22, (byte)snesBlock, (byte)(snesBlock >> 8), (byte)(snesBlock >> 16), 0xEA, 0xEA });
        int exitAddress = snesBlock + asm.Offset("Exit");
        rom.WriteBytes(ExitSite, new byte[] { 0x22, (byte)exitAddress, (byte)(exitAddress >> 8), (byte)(exitAddress >> 16), 0xEA, 0xEA });
        int leaveAddress = snesBlock + asm.Offset("Leave");
        rom.WriteBytes(LeaveSite, new byte[] { 0x22, (byte)leaveAddress, (byte)(leaveAddress >> 8), (byte)(leaveAddress >> 16), 0xEA });
    }

    // takes the backgrounds out: the game's code goes back, our block and the pictures get freed
    public static void Remove(RomBuffer rom)
    {
        if (!IsApplied(rom)) return;
        FreeAll(rom);
        rom.WriteBytes(Site, Original);
        rom.WriteBytes(ExitSite, ExitOriginal);
        rom.WriteBytes(LeaveSite, LeaveOriginal);
    }

    // frees the block and every picture it points to (the hooks stay until they get rewritten)
    private static void FreeAll(RomBuffer rom)
    {
        if (!IsApplied(rom)) return;
        int block = Block(rom);
        var freeSpace = ExpansionSpace.Load(rom);
        for (int i = 0; i < MaxBackgrounds; i++)
        {
            int snesAddress = ReadPointer(rom, block, i);
            if (snesAddress == 0) break;
            freeSpace.Free(FileOffset(snesAddress));
        }
        freeSpace.Free(block);
        freeSpace.Save();
    }
}
