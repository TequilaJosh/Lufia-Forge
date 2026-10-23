# Lufia II (US): music and sound

Static analysis only (ROM, the 65816 disassembly in l2_disasm.txt and a recursive SPC700 disassembly of the
uploaded driver, spc_driver_dis.txt). Nothing here was run in an emulator.
Tool: `python -I l2music.py [rom] [outdir] [--decode NN,MM]` writes music_out\ (driver image, samples as .brr
and .wav, unpacked songs, songs.txt, samples.txt, sfx.txt) and song_NN.txt (full decode). song_00.txt = FIELD.

Confidence: **H** = read from code and consistent with the data; **M** = read from code, meaning partly inferred;
**G** = guess.

## 1. SNES side (bank $80)

All talk goes through two helpers: `$80:99FD` puts a command number on ports 2 and 3, `$80:9A0A` waits for the
driver's $CD/$EF on ports 2/3, clears them and waits for the echo (same handshake as Lufia 1's $00:90E0/$90EB). (H)

| Routine | What it does | Driver command |
|---|---|---|
| `$80:9383` | boot: IPL upload (`$80:9C0F`, standard $BBAA / $CC handshake) of the block list at **$90:C000** (file 0x84000), then waits for $AB/$CD on ports 0/1 and answers $67/$89 (identical to Lufia 1), then `$80:9623` and `$80:93D6` | |
| `$80:93D6` → `$80:9703` | load the shared samples: write address $5800 (cmd 11), slot 0 (cmd 13), then samples 00-1F into slots 00-1F (cmd 14 each); reads the end address back (cmd 12) into `$0584` | 11, 13, 14, 12 |
| `$80:93FE` | **play song A** (event 4B): `$80:941A` load, then cmd 02 (start) | 02 |
| `$80:969F` | change song (event 6F): `$80:941A` load, then cmd 07 (start, variant: sets driver flag $9B.3, $08CA = 0; probably "start without restarting volume"/fade-in, M) | 07 |
| `$80:941A` | load song A (A < $64, else ignored): stop (cmd 03 via `$80:9528`), cmd 05, unpack **packed file A** to $7E:2020 (`$80:8E9D`), check the "S2" signature at $7E:2022, set the sample write address ($0584, or $5800 when header byte +18 is non-zero), upload the song's samples, then send the song image to sound RAM **$4400** (`$80:9911`, cmd 01); answer of cmd 12 kept in `$0586` | 03 05 11 13 14 01 12 |
| `$80:953B` | **sound effect A** (A < $8F, else ignored): event 4C via `$84:8775`, 102 callers | 04 |
| `$80:9528` | stop music (event 72) | 03 |
| `$80:96CC` + `$80:9692` | fade out (event 70: cmd 0A with $3C = 60, then cmd 06 sets the fade flag) | 0A, 06 |
| `$80:96F2` | read the song's sync counter (cmd 08 returns $8D on port 0): event 4D waits until it is >= xx | 08 |
| `$80:95F0` | read the driver status byte $9B (cmd 0F): event 4E waits while bit 1 (sound effect playing) | 0F |
| `$80:9623` | cmd 0D with A (boot sends 0 or 1 depending on the controller read at $4218 = $3000; driver stores it in $99, read back by cmd 18; stereo/mono option? G) | 0D |

Event commands (from events.md): `4B xx` play song xx, `4C xx` sound effect xx, `4D xx` wait for sync >= xx,
`4E` wait while a sound effect plays, `6F xx` change song, `70` fade out, `71` wait for fade, `72` stop.
The current song is kept in `$099D` (also `$7F:D0FD`); `4B` with the playing song does nothing.

### Driver commands ($03B5: `JMP [$03B8+X]`, 33 commands 01-21) (H for the ones listed)
01 receive a block (address on ports 0/1, stream as `$80:9945`), 02 start song, 03 stop ($3645), 04 sound effect
(port 0 = id), 05 ($3E33), 06 fade flag, 07 start (variant), 08 report sync, 0A fade speed ($08CB = 256/port0),
0B/0C/0D store port 0 into $08CC / $08CD / $99, 0E-10 misc, **11 set sample write pointer** ($9C), **12 report it**,
**13 select sample slot** ($9E = slot*4), **14 receive sample** (8-byte header, then the BRR stream), 1D header only,
1A/1B per-channel volume from ports 0/1.

## 2. Driver and sound RAM layout

* Upload blocks: ROM 0x84000 len 0x303 → $0300; ROM 0x84307 len 0x3A28 → $09D2; terminator at 0x87D33
  (len 0, start $0300). Driver ROM area 0x84000-0x87D36. (H)
* $0200-$02FF: **sample directory** (DIR, 64 slots × 4 bytes: start, loop start). (H, cmd 14 writes $0200+slot*4)
* $0300-$0602: entry code; $0603-$08D1 channel state (16 channels: 8 music + 8 sound effect, X = 0-15);
  $08D2+slot*4: each slot's ADSR1, ADSR2, pitch multiplier (H).
* $09D2: **sound effect table** (143 u16 offsets from $09D2), then the effects, then the rest of the driver code up
  to $43FA (event handlers $3560-$3AB9, tables $3BA0 / $3BC8, drum table $3C10, bit masks $4342, note pitch table
  $4362/$436E, sine $437A).
* **$4400-$57FF: the song** (max 0x1400 bytes). ENDING, the largest, is 0x13CC.
* **$5800-: samples.** Shared slots 00-1F first (end address saved in $0584), then each song's own samples.

## 3. Song data

Songs are **packed files 00-3E** (same table $A7:8000 and LZ packer as maps; files 3F+ are maps). The unpacked
file is a 2-byte length followed by the image that is copied to $4400. (H)

### Header (offsets inside the image, i.e. address - $4400)
| Off | Meaning |
|---|---|
| 00 | "S2C" (the SNES checks "S2" at +0/+1): Lufia 1 has "SFC" |
| 03 | tempo: timer 0 divider (`$35EA` → $FA), one tick = tempo × 125 µs (same as Lufia 1) |
| 04-0F | name, ASCII romaji, space padded ("FIELD", "BATL 1", "ENDING"...) |
| 10-17 | channel table: voice per channel, +80 = drum channel, FF = unused (`$3578`, same as Lufia 1) |
| 18 | non-zero = the song's samples overwrite the shared ones (write pointer reset to $5800); set in OPENING 2, OPENING.1, ENDING (M: SNES-side code is clear, the reason is inferred) |
| 19 | copied to $08C9 at start (also set by `FF 00`); used by the echo setup $42C4: echo delay? (G) |
| 20-2F | 8 order-list pointers, **u16 little-endian offsets from $4400** (driver adds $44 to the high byte) |
| 30-4F | sample list (up to 32, FF ends): sample ids < 80 load into slots 20, 21, 22... in order; after the first id >= 80 the rest are **drum notes** (id & 7F looked up in the drum note list at $80:9A34, a copy of the driver's $3C10, giving a shared sample 00-06 which is uploaded again into its own slot, duplicates skipped via the list at $7E:2000) |
| 50- | order lists, then phrases |

### Order list (per channel): same as Lufia 1 except the addresses
`hh ll` = phrase at **($44 + hh) << 8 | ll** (Lufia 1: absolute address), `80+t` = transpose t for the next
phrase (reset at each phrase end), `FF` = end of the list (channel stops). (H, $3B28 / $386C)

### Phrase events ($3691, F0+ via `JMP [$3BA0+X]`, FF xx via `JMP [$3BC8+X]`)
| Bytes | Meaning | vs Lufia 1 |
|---|---|---|
| `00-7F d g v` | note n (+ transpose), next event after d ticks, sounds g ticks (0 = rest), velocity v | same |
| `80-EF` | note (n & 7F) reusing the last full note's d, g, v | same |
| `F0 d x` | vibrato depth x ($0883; 0 = off), then wait d (H for the store, M for "vibrato": it scales a sine-table LFO added to the pitch) | L1: plain wait |
| `F1 d v` | channel volume ($0723) | same |
| `F2 d p` | pan ($0733, 40 = centre) | same |
| `F3 d` | wait d (2 bytes) | L1: nothing |
| `F4 d t` | tempo t (writes timer 0 directly) | L1: nothing |
| `F5`, `F8-FA` | nothing (1 byte) | same |
| `F6 d m` | pitch multiplier m/128 ($07C3; 80 = normal, 00 = ×2) (H) | L1: 4-byte "tune" |
| `F7 d i` | instrument = sample slot i (00-1F shared, 20+ the song's own) (H, `$420F` writes SRCN = i) | same idea |
| `FB` | loop start (outer slot if free, else inner) | same |
| `FC n` | loop end: body plays n times, 00 = forever; n = 1 does nothing | same (L2 frees the slot when the count runs out) |
| `FD` | phrase end → next order-list entry | same |
| `FE` | channel end (this channel only, `$38BD`) | L1: whole song stops (per SongCodec) |
| `FF s p` | effect, no wait: 00 $08C9 · 01 echo feedback · 02 echo FIR set ($08C7) · 03/04 echo on/off · 05-07 nothing · 08/09 ADSR attack/decay · 0A/0B sustain level/rate · 0C $07D3 = 80\|p (GAIN? G) · 0D/0E/0F echo volume L+R / L / R · 10 noise clock · 11/12 noise on/off · 13/14 pitch modulation on/off · 15 $0863 · 16 $0813 · 17 $0843 (modulation depth/speed values, G) | same idea, table not compared entry by entry |

Drum channels (channel table +80): the note is looked up in the drum list at $3C10 (notes 24 26 2A 2E 30 2D 29 31 28)
which gives pitch ($3C1A/$3C23), pan ($3C2C) and shared sample ($3C35: 00 01 03 04 05 05 05 06 02). (H)

Pitch: note n → table[n % 12] ($4362/$436E, 0x217D..0x3F38) >> (8 - n/12), × the sample's 8.8 multiplier. (H)

**Verdict: the same Neverland sequence format as Lufia 1** (note bytes, order lists, loops, drums, ticks), with
relative addresses, a 32-entry sample list, and changed F0/F3/F4/F6/FE. The driver is a newer build: only 12 of
496 16-byte chunks of Lufia 1's driver code occur in Lufia 2's, and the command numbers differ (L1 sample upload
0D/0F/0E/10, SFX 09; L2 11/13/14/12, SFX 04). SongCodec could be ported with those changes.

FIELD decoded (song_00.txt): echo on, 8 channels (6, 7 drums), shared instruments 10-13 and 1E plus its own
slots 20/21 (samples 2C, 2B); every channel's forever loop closes at tick 6336 (33 bars of 192 ticks), which
checks the loop and length logic.

## 4. Samples (table $98:8000, file 0xC0000)

* 57 entries (0x00-0x38), 3 bytes each; the first pointer (0xAB) is the end of the table. Entry = 23-bit offset
  from $98:8000 in 32 KB banks: bank = $98 + (b2 << 1 | w >> 15), address = w | $8000 (`$80:98A5`). (H)
  Lufia 1 used plain 3-byte SNES pointers at $09:8000.
* Record: `u16 length` (BRR bytes), `u16 loop offset` (from the BRR start; = length when not looping), `ADSR1`,
  `ADSR2`, `pitch multiplier` (8.8, big-endian: byte 6 integer, byte 7 fraction), then the BRR data. **Same 8-byte
  header as Lufia 1** (checked against its table). Samples run 0xC00AB-0xCDE6B (banks $98-$99).
* 11 empty placeholders (length 8): 07-0C, 17, 18, 1A, 1B, 25. Samples 00-06 are the drum kit.
* Full list with ROM offsets, sizes, loops, ADSR, multipliers: music_out\samples.txt; audio in music_out\samples\*.wav
  (32 kHz, played at multiplier 1.0).

## 5. Songs (packed files 00-3E)

| id | ROM | packed | unpacked | name | tempo | samples |
|---|---|---|---|---|---|---|
| 00 | 0x1387FB | 4FD | 7A0 | FIELD | 89 | 2C 2B |
| 01 | 0x138CF8 | 466 | 6E7 | SHIRO (castle) | 111 | 2D |
| 02 | 0x13915E | 6F5 | A9A | MACHI (town) | 69 | 21 22 32 |
| 03 | 0x139853 | 3CF | 594 | MURA (village) | 120 | 22 20 |
| 04 | 0x139C22 | 182 | 221 | OYASUMI (inn) | 118 | 2F |
| 05 | 0x139DA4 | 446 | 629 | TOU (tower) | 77 | |
| 06 | 0x13A1EA | 987 | F3D | OPENING 2 | 71 | 12 samples, +18 set |
| 07 | 0x13AB71 | 34E | 502 | DOROBOU | 83 | 26 |
| 08 | 0x13AEBF | 352 | 61D | DOUKUTHU (cave) | 87 | 23 |
| 09 | 0x13B211 | 380 | 634 | CUCHYUTEIEN | 71 | |
| 0A | 0x13B591 | 582 | C08 | DENSETU | 69 | |
| 0B | 0x13BB13 | 493 | 73D | HANA | 100 | 24 2F |
| 0C | 0x13BFA6 | 1B | 52 | DAMY (empty) | | |
| 0D | 0x13BFC1 | 272 | 46C | OPENING.1 | 83 | 1F 1E 29 16 A6, +18 set |
| 0E | 0x13C233 | 1B | 52 | DAMY | | |
| 0F | 0x13C24E | 46B | AE8 | KEKKONSHIKI (wedding) | 154 | |
| 10 | 0x13C6B9 | 575 | 89E | PARTY | 57 | 22 |
| 11 | 0x13CC2E | 36A | 4A8 | WAKARE (farewell) | 156 | 24 21 2F |
| 12 | 0x13CF98 | 472 | 6F0 | UMI (sea) | 95 | |
| 13 | 0x13D40A | 463 | 767 | YAMA (mountain) | 77 | |
| 14 | 0x13D86D | 2C9 | 4B8 | MUUS | 100 | 2C 2B 20 |
| 15 | 0x13DB36 | 497 | 69D | MINATO (port) | 74 | 22 28 |
| 16 | 0x13DFCD | 1A9 | 273 | MACHI HAKAI | 71 | |
| 17 | 0x13E176 | 357 | 5C3 | DOUKUTHU 1 | 111 | |
| 18 | 0x13E4CD | 65C | A23 | BATL 1 | 56 | 30 28 2E |
| 19 | 0x13EB29 | 866 | F9D | BATL 2 | 57 | |
| 1A | 0x13F38F | 1FE | 2C8 | HAKA (grave) | 91 | 29 |
| 1B | 0x13F58D | 7EB | F24 | BIG BOS | 105 | |
| 1C | 0x13FD78 | 600 | 9A5 | LAST BATL | 78 | |
| 1D | 0x140378 | 28B | 368 | KACHI (victory) | 71 | |
| 1E | 0x140603 | 15C | 1CE | HOKORA (shrine) | 91 | 24 27 |
| 1F | 0x14075F | 3D0 | 64D | KENKYUUZYO (lab) | 83 | 28 |
| 20 | 0x140B2F | 287 | 41E | KAICYU (undersea) | 87 | 37 |
| 21 | 0x140DB6 | 2D7 | 4C4 | KAICYUCITY | 91 | 2F 22 |
| 22 | 0x14108D | 721 | C36 | KAZINO | 63 | 23 20 |
| 23 | 0x1417AE | 134 | 18C | 777 PLAY | 65 | 2A |
| 24 | 0x1418E2 | 180 | 23B | 777 JAC GAME | 67 | |
| 25 | 0x141A62 | 115 | 175 | 777 START | 71 | |
| 26 | 0x141B77 | 6D | AB | GAAAAN | 111 | 29 |
| 27 | 0x141BE4 | 1CD | 2E3 | HARU (spring) | 100 | 24 20 |
| 28 | 0x141DB1 | 51A | 7DF | NATU (summer) | 54 | 23 21 |
| 29 | 0x1422CB | 1FB | 2F8 | AKI (autumn) | 111 | 23 |
| 2A | 0x1424C6 | 1F9 | 2A9 | FUYU (winter) | 127 | 20 |
| 2B | 0x1426BF | 299 | 455 | NAME IN | 118 | 2A |
| 2C | 0x142958 | 463 | 6D5 | SHINDEN | 83 | |
| 2D | 0x142DBB | B08 | 13CE | ENDING | 71 | 13 samples + 6 drum notes, +18 set |
| 2E | 0x1438C3 | 3AD | 5F2 | LOVE | 77 | 24 34 2F |
| 2F | 0x143C70 | 163 | 1CC | SHINDEN | 167 | |
| 30 | 0x143DD3 | 1B | 52 | DAMY | | |
| 31 | 0x143DEE | 1B | 52 | DAMY | | |
| 32 | 0x143E09 | 4CD | 7B8 | HIDECKA | 91 | |
| 33 | 0x1442D6 | 11C | 197 | ITEM GET1 | 63 | |
| 34 | 0x1443F2 | D6 | 11B | ITEM GET2 | 48 | |
| 35 | 0x1444C8 | EA | 13D | ITEM GET3 | 68 | |
| 36 | 0x1445B2 | BA | 111 | ITEM GET4 | 56 | |
| 37 | 0x14466C | 24B | 3E7 | NAZO NO HITO | 74 | 34 37 2A |
| 38 | 0x1448B7 | 473 | 78D | KOKUUTOU DAS | 116 | |
| 39 | 0x144D2A | 388 | 5A1 | SORA (sky) | 69 | |
| 3A | 0x1450B2 | 1FC | 33D | BOSKAIWA | 91 | |
| 3B | 0x1452AE | 133 | 1ED | NAZONOFUE | 125 | |
| 3C | 0x1453E1 | 195 | 1ED | MUUS CYANGE | 95 | |
| 3D | 0x145576 | 1F4 | 2BC | NALBIK | 139 | 37 |
| 3E | 0x14576A | 105 | 14F | MUUS 2 | 69 | |

ROM = the packed file's offset (its 2-byte unpacked size comes first). Translations in brackets are mine (G).
Channel tables and the raw lists: music_out\songs.txt. Which map plays which song was not traced (map data /
event 4B arguments).

## 6. Sound effects

143 effects (00-8E) live **inside the driver** at $09D2 (ROM 0x84307 + (addr - $09D2)), not in separate ROM
data. Record: `n`, n voice numbers, n u16 offsets (from the record) to tracks in the normal phrase format; the
driver plays them on channels voice+8, muting that music voice. Per-effect voices and track addresses:
music_out\sfx.txt. (H for the layout, `$3D04`)

## 7. Not done / open
* Exact meaning of header +19, `FF 00`, `FF 0C`, `FF 15-17`, cmd 05/07/0D (marked G/M above).
* No playback test (rules: static only). Next step would be porting LufiaSound/SongCodec with the differences above
  and checking songs re-encode to identical bytes.
