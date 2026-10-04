namespace LufiaForge.Modules.GameData;

/// <summary>
/// File offsets (headerless US ROM, 0-based) of the game data tables edited by the Game Data module.
/// Originally documented by the L1ME "Lufia 1 Multi Editor" v1.2b (jce3000gt, 2014) and re-verified
/// against the US ROM (CRC32 5E1AA1A6).
/// </summary>
public static class GameDataOffsets
{
    // ── Characters ──────────────────────────────────────────────────────────
    // 8 records x 29 bytes: Hero, Lufia, Aguro, Jerin, Maxim, Selan, Guy, Artea
    //   +0 level   +1 ?   +2 HP   +4 MP   +6 AGL   +8 INT   +10 STR   +12 MGR (u16 each)
    //   +14 EXP (u24)   +17..+22 ?   +23 weapon +24 armor +25 shield +26 helmet +27 shoes +28 ring
    public const int CharacterTable  = 0x46D25;
    public const int CharacterStride = 29;
    public const int CharacterCount  = 8;

    // 7 names x 5 chars (Lufia, Aguro, Jerin, Maxim, Selan, Guy, Artea). The Hero is named by the player.
    public const int CharacterNames    = 0x46E8E;
    public const int CharacterNameLen  = 5;

    // ── Character spell lists: (level, spell) pairs, FF terminated. Level 0 = known from start.
    public const int HeroSpellList  = 0x46E0D;   // 17 entries
    public const int LufiaSpellList = 0x46E30;   // 24 entries
    public const int JerinSpellList = 0x46E61;   // 22 entries

    // ── Items ───────────────────────────────────────────────────────────────
    // 256 x u16 pointers, relative to the table start.
    //   +0 name (12)  +12 type  +13 equip slot  +14 equip flags (hi nibble = who, lo nibble = target)
    //   +15 icon      +16 price (u16)           +18..+20 ?   +21.. property/effect data (00 terminated)
    public const int ItemPointerTable = 0x55800;
    public const int ItemCount        = 256;
    public const int ItemNameLen      = 12;

    // ── Monsters ────────────────────────────────────────────────────────────
    // u16 pointers relative to the table start.
    //   +0 name (10)  +10 type/flags  +12 weakness  +14 graphic  +15 palette
    //   +16 HP  +20 ATP  +22 DFP (u16)   +24 M.DEF  +25 AGL  +26 MGR (u8)
    //   +27 EXP (u16)  +30 GP (u16)  +32 item dropped  +33 drop rate (/255)
    public const int MonsterPointerTable = 0x5800;
    public const int MonsterCount        = 165;
    public const int MonsterNameLen      = 10;

    // ── Shops: u16 pointers that are direct file offsets. Record = type, items..., 00
    public const int ShopPointerTable = 0xEC5B;
    public const int ShopCount        = 86;

    // ── Final (prologue) party's items: 10 "give item" script commands: 3E item qty
    public const int FinalPartyItems     = 0x1BDE9;
    public const int FinalPartyItemCount = 10;

    // ── Spells: variable-length records with no pointer table, so the offsets are listed.
    //   +0 name (8)  +8 usage  +10 target  +11 MP  +20 effect  +21 sub-type  +22.. effect parameters
    public const int SpellNameLen = 8;
    public static readonly int[] SpellOffsets =
    {
        0xFE354, 0xFE368, 0xFE382, 0xFE39C, 0xFE3B6, 0xFE3D0, 0xFE3EA, 0xFE404,
        0xFE41E, 0xFE438, 0xFE452, 0xFE46C, 0xFE486, 0xFE4A0, 0xFE4BA, 0xFE4D4,
        0xFE4EE, 0xFE506, 0xFE51E, 0xFE536, 0xFE54E, 0xFE567, 0xFE580, 0xFE598,
        0xFE5B0, 0xFE5C7, 0xFE5E2, 0xFE5FD, 0xFE615, 0xFE630, 0xFE64B, 0xFE666,
        0xFE681, 0xFE699, 0xFE6B1, 0xFE6CA, 0xFE6E3, 0xFE6FC, 0xFE715, 0xFE72E,
        0xFE746, 0xFE75E, 0xFE77A, 0xFE796, 0xFE7AE, 0xFE7C6, 0xFE7DD, 0xFE7F4,
        0xFE80B, 0xFE822, 0xFE83B, 0xFE853, 0xFE86B, 0xFE885, 0xFE89F, 0xFE8B9,
    };

    // ── Misc game settings ──────────────────────────────────────────────────
    public const int WalkSpeed        = 0x9829;  // 0x10 normal, 0x20 fast
    public const int WalkSpeedPatch   = 0x9A49;  // 4 bytes: 0A 0A 0A 0A normal, EA EA A9 20 fast
    public const int ShipSpeed        = 0xB1AD;  // 0x20 normal, 0x40 fast
    public const int AirshipSpeed     = 0xB1C9;  // 0x40 normal, 0x80 fast
    public const int SwampDamage      = 0x96B4;  // u16 damage per step
    public const int MaxLevelDisplay  = 0x40AB5;
    public const int MaxLevelActual   = 0x46636;
    public const int InternalTitle    = 0x7FC0;
    public const int InternalTitleLen = 21;
    public const int RegionByte       = 0x7FD9;

    public static readonly byte[] WalkPatchNormal = { 0x0A, 0x0A, 0x0A, 0x0A };
    public static readonly byte[] WalkPatchFast   = { 0xEA, 0xEA, 0xA9, 0x20 };
}
