# The Dutch Lufia II, and Dutch letters for Lufia 1

## The Dutch ROM

The Dutch Lufia II is a translation built on the European release. Its header says "LUFIA FOR DUTCH", country 07, and it is 3 MB. SHA1 2CBB1A60656C6571ECB0B0CD8AE8E46609F37514.

The European game ("LUFIA II", country 02) already differs from the US game in its code banks. The Dutch hack then moved most of its data again. So Lufia Forge doesn't edit either of them as they are. It puts the Dutch text into a Lufia II (USA) ROM, or into a hack built on one, and every page then edits that ROM as usual.

There are two ways to do this:

- **New project from the Dutch ROM.** File, New Project from a ROM, then pick the Dutch `.sfc` or the `.zip`. Lufia Forge asks for the Lufia II (USA) ROM, or a Frue Lufia ROM, to put the text into.
- **Patch Manager, Lufia II, Dutch translation.** This puts the text into the open project. To use it on a hack, add the hack first (Frue Lufia, for example), then this.

The game stays the US one, so it runs at 60 Hz, and a hack keeps its fixes. Edit, Undo takes the import back.

### What comes over

| Part | How |
| --- | --- |
| Dialogue: 5,455 lines | Each event is paired by list, id and order, and its lines get the Dutch text. The target's own commands stay. 7 events split their lines differently in Dutch, so those are copied whole. |
| Dialogue font | The Dutch font goes into the added space, and the glyph code is changed the way the Dutch ROM changes it. The window blank at $82:90FB draws 20 instead of 10. |
| Menu font | The Dutch glyphs for 10-7F are copied to $9F:8500. |
| Names | Items, spells, monsters, IP attacks, monster attacks, capsule attacks and map names. |
| Descriptions | All of them, packed with the Dutch description word list, which moves to the added space. |

The menus and battle messages stay English. They are strings inside the game's code.

### Where the Dutch ROM keeps things

| What | Dutch ROM | US game |
| --- | --- | --- |
| Map header table (events, start-up scripts, map names) | $A7:8000 (0x138000); its u24 values count from bank $A7 | $87:8000 |
| Dictionary | 0x76A00, with Dutch words | 0x76A00 |
| Dialogue font | $A1:B222, from character 10 | $9A:F970, from 20 |
| Item names | 0xF5ED9 (12 letters each) | 0xF47E8 |
| Spell table | $B3:FA5B | $95:FA5B |
| Monster table | $B4:85C0 | $96:85C0 |
| IP attack table | $84:8F20 | $84:8F10 |
| Monster attack names | $85:F2C7 | $85:F2D7 |
| Capsule attack names | $A5:E100 | $A5:DF00 |
| Descriptions | $B5:8000; word list $9E:C7E8 | $97:8000; word list $9E:DDCC |

`L2Dutch.View` copies the header table to $87:8000 and moves its values on by 0x20 banks. Lufia Forge's own readers then read the Dutch events and names unchanged.

### The Dutch letters

| Code | Letter | Code | Letter |
| --- | --- | --- | --- |
| 10-1F | À Á È É Ë Ï Ò Ó Ö Ù Ú Ü à á è é | `"` | ë |
| `#` | ï | `$` | ò |
| `&` | ó | `(` | ö |
| `)` | ù | `*` | ú |
| `;` | ü | `<` | ä |
| `=` | Ä | `>` | ì |
| `\` | ñ | `^` | Ñ |
| `` ` `` | ¿ | `|` | ß |
| `~` | ê | 7F | ¡ |
| `[` `]` | draw `(` `)` | | |

With this font in, the editors show and take the letters themselves (`L2Charset`). Descriptions can't use 10-1F, because those codes are dictionary words there. In the Dutch ROM's own description reader, the dictionary codes start at 0D and the second byte counts from 1.

### Repacking the events

A translation changes every map, so the import doesn't move one map at a time. `L2EventBlocks.RepackAll` builds every block again: "PH", the six lists, the key data, then each script once. The blocks go back into the room the game's blocks take ($87-$8E), never across a bank's end. Only what doesn't fit goes into the event space. The US game needs 1-2 banks for it, and the ROM grows to 3 MB.

A block that can't be laid out again stays where it is. Frue Lufia has two, each with a script whose steps overlap. Their lines are written in place when they fit, and 21 lines stay English.

The name lists are repacked only where the game keeps them one after another. Frue keeps some elsewhere, so there each name is written in its own place when it fits.

### Checked

- **In the game, US:** the opening plays in Dutch in Tia's shop and goes on as usual. Every Dutch letter shows in a dialogue box. A shop shows the Dutch item names, the menu font and the description, with the window title clean.
- **In the game, Frue Lufia:** the opening plays in Dutch.
- **Read back:** every line, name and description matches the Dutch ROM.

## Dutch letters for Lufia 1

There is no Dutch Lufia 1. Game Data, Game Settings, Dutch has two things:

- **Dutch letters.** é è ë ê ï ö ü á à ó ú É take the places of # $ % * ; = ^ \ ] ` | _. The game never shows those signs as text. The glyphs are made from the ROM's own e, i, o, u, a and E, with an accent above, in both the text font and the small font. They are rebuilt when the Lufia II font option changes the font. The text editors, the event editor and the name fields take the letters themselves.
- **Dutch names from Lufia II.** The 135 items, spells and monsters both games share get the names the Dutch Lufia II gives them, for example Short Sword becomes Kort zwaard and Thunder becomes Donder. Lufia Forge keeps a copy of those names in `Translations/Lufia1_Dutch.json`, so no Dutch ROM is needed. A name the project changed itself is left alone. Statue (Standbeeld) is too long for Lufia 1's 8 letters, so it keeps its English name.

Signs left alone on purpose:

- `~`: the name screen fills the letters still to type with it. Checked in BizHawk with `~` drawn solid.
- `@`: the space inside item and monster names.
- `!` `"` `&` `'` `+` `,` `-` `.` `:` `<` `>` `?`: the dialogue uses them.

`=` is the last sign on the name screen's grid, so a name can have an ö.

**Checked in the game:** "é è ë ê ï ö ü á à ó ú É" and "egoïstisch, één, ideeën" show in a narration box.
