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
or armor script: that target only), `60 s` ward the targets against it for the battle.

After the block Lufia Forge allocates the monsters' starting statuses (a word per monster) and a 10-byte record per item
(elements halved / nullified, the game's statuses immune / halved, the new ones immune / resisted) that the STATUS
screen's resistance pages (Y) add up for the hero's worn items.

Each piece was checked in BizHawk (Lufia Forge test kits): every status caused, ticking, wearing off and cured; the
damage changes measured; save and load; the STATUS screen pages.
