# Lufia II new statuses

The 65816 source of Lufia Forge's 16 new statuses for Lufia II (US) and the tool that turns it into
`LufiaForge/Core/Lufia2/L2StatusPatch.Data.cs`.

* `l2status.asm`: the hooks into the game's battle, menu and save code and the added routines (asar-like syntax,
  assembled by `tools/limits/asm65.py`; `HOOK` is where the added block goes). Inside the block only JSL, JML and BRL
  reach other routines (the generator relocates 24-bit addresses only).
* `gen.py <clean US ROM>`: assembles it at two places, checks every hooked place still holds the game's bytes, and
  writes the C# data (`Sites`, `Code` and its relocations).

The statuses (bits of a second status word per fighter, at `$7F:FC04`): Zombie, Frozen, Decay, Burnt, Berserk, Spooked,
Amnesia, Piety, Regen, Doom, Blind, Stone, Bleed, Curse, Stop, Mana leak. Zombie, Decay, Burnt and Blind stay on a hero
after a battle and are saved (in the save buffer's unused `$7E:3BBB-3BC3`, behind an "LF" marker); the rest end with
the battle.

Battle script commands: `5D s c` cause status s (chance c%), `5E s c` cure it, `5F s p` resist it by p% (in a "when hit"
or armor script: that target only), `60 s` ward the targets against it for the battle. For `5E`, `5F` and `60`, s = `10`
means all 16 at once: `5F 10 64` makes a monster immune to every new status, as the game's shared script `42 11` does for
its own eight.

`61` gives the hero targets focus for the battle (the Tag candy; see below).

Bounce is the game's own status bit 6 (`$0F` of a fighter): the game's check at `$85:D0F2` sends a hit back at the
attacker when the action has reg 26 bit 5, which only battle ops 21 and 23 set, and nothing in the game ever gives the bit.
The patch sets that bit for weapon attacks too (op 37, action kind 1: the heroes' attack and the monsters' plain one), says
"<name> bounces the attack back!" after the action, gives the game's statuses a monster starts with (a byte per monster)
or a hero's worn items give (the low byte of a word per item) on the battle's first round, and ends Bounce on the heroes
with the battle.

Focus (Mewlies' idea for the game's unused Tag ring and Tag candy): a hero with focus (a worn item with bit 8 of its word,
or command 61 this battle) hits harder each turn in a row it attacks the same foe with its weapon: the 2nd-6th attack's
power gets the 5 focus percents (+10 +25 +50 +75 +100 to start) at the element hook, and "Maxim's focus: +25%" is said
after it. Another foe, more than one, or any other action starts it again. RAM: `$7F:FB60-$7F:FBF5` (cleared at every
battle's start; the game never uses `$7F:FB4B` up).

Piety is double-edged: the fighter's healing (HP and MP it restores) and its light attacks do 25% more, and dark hits it
for 50% more.

After the block Lufia Forge allocates the monsters' starting statuses (a word per monster), a 10-byte record per item
(elements halved / nullified, the game's statuses immune / halved, the new ones immune / resisted) that the STATUS
screen's status page adds up for the hero's worn items, and the address of the element table (4 bytes at MonStart +
$1600; not inside the code, which Lufia Forge compares to recognise the patch). From the 4th version then: the game's
statuses each monster starts with (a byte per id at + $1604), a word per item (+ $1704: low byte the game's statuses its
wearer starts with, bit 8 focus when worn) and the 5 focus percents (+ $1B04).

The element table (Lufia Forge's `L2Elements`, allocated on its own, $6700 bytes): a row of 17 signed words per monster
(256 at +0), hero (7 at +$2200) and item (512 at +$2300): a percent per element bit, then "plain" (no element). At
$85:D1BC (`ElemHit`, after the target's "when hit" script, before the damage formula) a hit's power is multiplied by
(100 + percent) / 100 for each element of the attack in turn (a hero: its row plus its six worn items' rows; below -100
the hit heals). The STATUS screen's element page (Y) shows the hero's totals ("Fire +30%"; % is the menu font's tile
$117, drawn with the codes 09 3D / 17 / 09 3C as the game's "IP 0%"). `L2StatusPatchV1.Data.cs` keeps the first
version of the patch (before the table) and `L2StatusPatchV2.Data.cs` the second (before "every status" and the piety
bonuses), so a ROM that has either is upgraded (the second keeps its element table where it is).

Each piece was checked in BizHawk (Lufia Forge test kits): every status caused, ticking, wearing off and cured; the
damage changes measured; save and load; the STATUS screen pages; every element percent on a monster and on a hero
(with his armor and weapon rows), several elements in one attack, immunity and healing, a game monster's weaknesses
moved into the table (each hit's damage logged before and after the hook and checked against the same sums).
