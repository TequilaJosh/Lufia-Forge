namespace LufiaForge.Core;

// constants we know for lufia and the fortress of doom (us, snes, taito 1993)
//
// where the text encoding comes from:
//   - Vegetaman (2010): https://sites.google.com/site/vegetaman/home/dictionary
//   - Digisalt Lufiatools 2.0 / AllOriginal.tbl (flobo, 2011), the byte map we trust
public static class Lufia1Constants
{
    // rom identity
    public const string RomTitle       = "LUFIA                ";
    public const int    ExpectedRomSize = 0x100000;
    public const int    SmcHeaderSize   = 0x200;

    // snes header
    public const int  SnesHeaderOffset     = 0x7FC0;
    public const int  SnesChecksumOffset   = 0x7FDE;
    public const int  SnesComplementOffset = 0x7FDC;
    public const int  SnesMappingOffset    = 0x7FD5;
    public const int  SnesRomSizeOffset    = 0x7FD7;
    public const byte LoRomMappingByte     = 0x20;

    // lorom address translation
    public static int SnesAddressToFileOffset(int bank, int address) =>
        ((bank & 0x7F) * 0x8000) + (address & 0x7FFF);

    public static int SnesAddressToFileOffset(int snesAddress)
    {
        int bank    = (snesAddress >> 16) & 0xFF;
        int address = snesAddress & 0xFFFF;
        return SnesAddressToFileOffset(bank, address);
    }

    public static int FileOffsetToSnesAddress(int fileOffset)
    {
        int bank    = (fileOffset / 0x8000) | 0x80;
        int address = (fileOffset % 0x8000) | 0x8000;
        return (bank << 16) | address;
    }

    // text / dialogue encoding
    // the full map (from AllOriginal.tbl, digisalt 2011):
    //
    //   0x00             end of string
    //   0x04             page break (player presses A to go on)
    //   0x05             new line in the current dialogue box
    //   0x07 + 1 byte    character name  (id: 00=Hero, 01=Lufia, 02=Aguro, 03=Jerin...)
    //   0x09 + 1 byte    item name       (id: 00=Nothing, 01=Leather Armor, ...)
    //   0x0A + 1 byte    spell name      (id: 00=Flash, 01=Spark, 02=Blaze, ...)
    //   0x0B + 1 byte    town name       (id: 01=Alekia, 02=Chatam, 03=Sheran...)
    //   0x0C + 1 byte    MTE3 word, lowercase   (1-byte index, word from the MTE3 pointer table)
    //   0x0D + 1 byte    MTE3 word, capitalized (same index, first letter uppercased)
    //   0x10-0x1F        MTE4: special single-byte sequences ('s, ed, ing, I'm, Sinistral...)
    //   0x20-0x7E        plain printable ascii
    //   0x80-0xBF        MTE1: single-byte compressed words, lowercase (the, you, to, it...)
    //   0xC0-0xFF        MTE1: single-byte compressed words, capitalized (The, You, To, It...)
    //
    // 0x0B is TOWN names, not character names, those are 0x07.
    // the older vegetaman research had 0x0B down as character names, that was wrong
    //
    // MTE = multiple title entry (compression). four tables, all in rom bank 0x0A:
    //   MTE1 (0x80-0xFF):  64 word pairs, ptr table at file 0x54AC0, entries at 0x54B42
    //   MTE2 (0x0D XX):    same entry data as MTE3, the decoder capitalizes the first letter
    //   MTE3 (0x0C XX):    256 words, ptr table at file 0x54C17, entries at 0x54E19
    //   MTE4 (0x10-0x1F):  16 special sequences, ptr table at 0x54A50, entries at 0x54A72
    //
    // pointers are LE 16-bit snes bank-relative addresses. file offset = ptr + 0x48000
    // (all MTE tables are in snes bank 0x0A: file = 0x0A*0x8000 + (ptr - 0x8000) = ptr + 0x48000)

    public const byte CtrlEndString        = 0x00;
    public const byte CtrlPageBreak        = 0x04;
    public const byte CtrlNewline          = 0x05;
    public const byte CtrlCharName         = 0x07;  // followed by 1-byte character ID
    public const byte CtrlItemName         = 0x09;  // followed by 1-byte item ID
    public const byte CtrlSpellName        = 0x0A;  // followed by 1-byte spell ID
    public const byte CtrlTownName         = 0x0B;  // followed by 1-byte town ID
    public const byte CtrlDictionaryRef    = 0x0C;  // MTE3 lowercase, followed by 1-byte index
    public const byte CtrlDictionaryRefCap = 0x0D;  // MTE3 capitalized, followed by 1-byte index

    // MTE pointer table file offsets (us rom, bank 0x0A, checked against the rom)
    public const int Mte4PtrTableOffset    = 0x54A50;  // 17 LE16 ptrs (16 entries + end sentinel)
    public const int Mte1PtrTableOffset    = 0x54AC0;  // 65 LE16 ptrs (64 entries + end sentinel)
    public const int Mte3PtrTableOffset    = 0x54C17;  // 257 LE16 ptrs (256 entries + end sentinel)
    public const int Mte1EntryCount        = 64;
    public const int Mte3EntryCount        = 256;
    public const int MtePtrBase            = 0x48000;  // add to a raw LE16 ptr to get the file offset

    // MTE3 scan region for the dictionary export (start = first entry, end = end of the last entry)
    public const int DictionaryStartOffset = 0x054E19;
    public const int DictionaryEndOffset   = 0x0553CC;
    public const int DictionaryBaseOffset  = 0x48000;  // ptr + DictionaryBaseOffset = file offset

    // dialogue text lives in banks $84-$AF (file 0x20000-0x57FFF)
    // story/npc dialogue: banks $84-$87 (0x20000-0x3FFFF)
    // battle messages:    banks $88-$AF (0x40000-0x57FFF)
    public const int DialogueScanStartOffset = 0x20000;
    public const int DialogueScanEndOffset   = 0x58000;

    // character names (0x07 + id byte)
    // from AllOriginal.tbl: 0700=Hero, 0701=Lufia, 0702=Aguro, 0703=Jerin, ...
    public static readonly IReadOnlyDictionary<byte, string> CharacterNames =
        new Dictionary<byte, string>
        {
            { 0x00, "[Hero]"  },
            { 0x01, "[Lufia]" },
            { 0x02, "[Aguro]" },
            { 0x03, "[Jerin]" },
            { 0x04, "[Maxim]" },
            { 0x05, "[Selan]" },
            { 0x06, "[Guy]"   },
            { 0x07, "[Artea]" },
            { 0x08, "[Daos]"  },
            { 0x09, "[Erim]"  },
            { 0x0A, "[Amon]"  },
            { 0x0B, "[Gades]" },
        };

    // town names (0x0B + id byte)
    // from AllOriginal.tbl: 0B01=Alekia, 0B02=Chatam, ...
    public static readonly IReadOnlyDictionary<byte, string> TownNames =
        new Dictionary<byte, string>
        {
            { 0x01, "Alekia"   }, { 0x02, "Chatam"   }, { 0x03, "Sheran"    },
            { 0x04, "Treck"    }, { 0x05, "Lorbenia" }, { 0x06, "Grenoble"  },
            { 0x07, "Kirof"    }, { 0x08, "Medan"    }, { 0x09, "Surinagal" },
            { 0x0A, "Belgen"   }, { 0x0B, "Jenoba"   }, { 0x0C, "Ruan"      },
            { 0x0D, "Rangs"    }, { 0x0E, "Odel"     }, { 0x0F, "Lyden"     },
            { 0x10, "Arus"     }, { 0x11, "Platina"  }, { 0x12, "Carbis"    },
            { 0x13, "Bakku"    }, { 0x14, "Linze"    }, { 0x15, "Marse"     },
            { 0x16, "Herat"    }, { 0x17, "Soshette" }, { 0x18, "Epro"      },
            { 0x19, "Arubus"   }, { 0x1A, "Frederia" }, { 0x1B, "Forfeit"   },
            { 0x1C, "Makao"    }, { 0x1D, "Elfrea"   }, { 0x1E, "Elfrea"    },
        };

    // special single-byte compressed sequences (0x10-0x1F)
    // from AllOriginal.tbl: 10='s  11=ed  12=ing  13=I'm  and so on
    public static readonly IReadOnlyDictionary<byte, string> SpecialSequences =
        new Dictionary<byte, string>
        {
            { 0x10, "'s"        },
            { 0x11, "ed"        },
            { 0x12, "ing"       },
            { 0x13, "I'm"       },
            { 0x14, "I'll"      },
            { 0x15, "I've"      },
            { 0x16, "Alumina"   },
            { 0x17, "Sinistral" },
            { 0x18, "Dual"      },
            { 0x19, "Falcon"    },
            { 0x1A, "Glasdar"   },
            { 0x1B, "Welcome"   },
            { 0x1C, "Raile"     },
            { 0x1D, "Lilah"     },
            { 0x1E, "Reyna"     },
            { 0x1F, "Shaia"     },
        };

    // single-byte compressed words (0x80-0xFF)
    // 0x80-0xBF = lowercase, 0xC0-0xFF = the same words capitalized
    // from AllOriginal.tbl (digisalt 2011), 128 pairs in all
    public static readonly IReadOnlyDictionary<byte, string> SingleByteWords =
        new Dictionary<byte, string>
        {
            // lowercase (0x80-0xBF)
            { 0x80, "the"    }, { 0x81, "you"    }, { 0x82, "to"     }, { 0x83, "it"     },
            { 0x84, "of"     }, { 0x85, "that"   }, { 0x86, "is"     }, { 0x87, "in"     },
            { 0x88, "and"    }, { 0x89, "what"   }, { 0x8A, "this"   }, { 0x8B, "go"     },
            { 0x8C, "but"    }, { 0x8D, "are"    }, { 0x8E, "there"  }, { 0x8F, "no"     },
            { 0x90, "be"     }, { 0x91, "we"     }, { 0x92, "so"     }, { 0x93, "do"     },
            { 0x94, "for"    }, { 0x95, "have"   }, { 0x96, "can"    }, { 0x97, "me"     },
            { 0x98, "know"   }, { 0x99, "don't"  }, { 0x9A, "he"     }, { 0x9B, "if"     },
            { 0x9C, "my"     }, { 0x9D, "here"   }, { 0x9E, "yes"    }, { 0x9F, "on"     },
            { 0xA0, "was"    }, { 0xA1, "island" }, { 0xA2, "with"   }, { 0xA3, "about"  },
            { 0xA4, "your"   }, { 0xA5, "come"   }, { 0xA6, "get"    }, { 0xA7, "see"    },
            { 0xA8, "can't"  }, { 0xA9, "will"   }, { 0xAA, "right"  }, { 0xAB, "now"    },
            { 0xAC, "let"    }, { 0xAD, "ok"     }, { 0xAE, "at"     }, { 0xAF, "take"   },
            { 0xB0, "just"   }, { 0xB1, "up"     }, { 0xB2, "really" }, { 0xB3, "please" },
            { 0xB4, "well"   }, { 0xB5, "not"    }, { 0xB6, "all"    }, { 0xB7, "you're" },
            { 0xB8, "good"   }, { 0xB9, "want"   }, { 0xBA, "four"   }, { 0xBB, "tower"  },
            { 0xBC, "as"     }, { 0xBD, "from"   }, { 0xBE, "back"   }, { 0xBF, "by"     },
            // capitalized (0xC0-0xFF)
            { 0xC0, "The"    }, { 0xC1, "You"    }, { 0xC2, "To"     }, { 0xC3, "It"     },
            { 0xC4, "Of"     }, { 0xC5, "That"   }, { 0xC6, "Is"     }, { 0xC7, "In"     },
            { 0xC8, "And"    }, { 0xC9, "What"   }, { 0xCA, "This"   }, { 0xCB, "Go"     },
            { 0xCC, "But"    }, { 0xCD, "Are"    }, { 0xCE, "There"  }, { 0xCF, "No"     },
            { 0xD0, "Be"     }, { 0xD1, "We"     }, { 0xD2, "So"     }, { 0xD3, "Do"     },
            { 0xD4, "For"    }, { 0xD5, "Have"   }, { 0xD6, "Can"    }, { 0xD7, "Me"     },
            { 0xD8, "Know"   }, { 0xD9, "Don't"  }, { 0xDA, "He"     }, { 0xDB, "If"     },
            { 0xDC, "My"     }, { 0xDD, "Here"   }, { 0xDE, "Yes"    }, { 0xDF, "On"     },
            { 0xE0, "Was"    }, { 0xE1, "Island" }, { 0xE2, "With"   }, { 0xE3, "About"  },
            { 0xE4, "Your"   }, { 0xE5, "Come"   }, { 0xE6, "Get"    }, { 0xE7, "See"    },
            { 0xE8, "Can't"  }, { 0xE9, "Will"   }, { 0xEA, "Right"  }, { 0xEB, "Now"    },
            { 0xEC, "Let"    }, { 0xED, "Ok"     }, { 0xEE, "At"     }, { 0xEF, "Take"   },
            { 0xF0, "Just"   }, { 0xF1, "Up"     }, { 0xF2, "Really" }, { 0xF3, "Please" },
            { 0xF4, "Well"   }, { 0xF5, "Not"    }, { 0xF6, "All"    }, { 0xF7, "You're" },
            { 0xF8, "Good"   }, { 0xF9, "Want"   }, { 0xFA, "Four"   }, { 0xFB, "Tower"  },
            { 0xFC, "As"     }, { 0xFD, "From"   }, { 0xFE, "Back"   }, { 0xFF, "By"     },
        };

    // labels for control codes (hex view, or a byte we don't know)
    public static readonly Dictionary<byte, string> ControlCodeLabels = new()
    {
        { 0x00, "[END]"    }, { 0x01, "[CTL:01]" }, { 0x02, "[CTL:02]" },
        { 0x03, "[CTL:03]" }, { 0x04, "[PAGE]"   }, { 0x05, "[NL]"     },
        { 0x06, "[CTL:06]" }, { 0x07, "[NAME]"   }, { 0x08, "[CTL:08]" },
        { 0x09, "[CTL:09]" }, { 0x0A, "[CTL:0A]" }, { 0x0B, "[TOWN]"   },
        { 0x0C, "[DICT]"   }, { 0x0D, "[DCAP]"   }, { 0x0E, "[CTL:0E]" },
        { 0x0F, "[CTL:0F]" },
    };

    // graphics
    public const int TileByteSize     = 32;
    public const int Tile8bppByteSize = 64;

    // event script engine, checked in the code and in game (see research-script-engine.md and
    // research-event-commands.md)
    // rom (file offsets)
    public const int EventCommandTable   = 0x0C78C;  // bank $01: 128 handler words for commands 00-6D (6E-7F overlap the next table)
    public const int EventRangeTable     = 0x0C868;  // bank $01: (bound, handler) triples for commands 80-FF
    public const int TextControlTable    = 0x0C75C;  // bank $01: handlers for text control codes 00-0F
    public const int ScriptBankBase      = 0x18000;  // script offset T -> file 0x18000 + T
    public const int MapScriptTable      = 0x18200;  // 5 bytes per map: script block (u24), ?, event base
    public const int SpriteRecordTable   = 0x0E25F;  // bank $01: sprite id -> 17-byte record pointer
    public const int ShortWaitTable      = 0x0DDB5;  // commands 80-87: wait x 4 frames
    // wram ($7E:xxxx)
    public const int RamStoryFlags       = 0x1296;   // 256 story flags, flag f at $1296 + f/8
    public const int RamFlagBitMasks     = 0x032B;   // 8 bit masks the engine uses for flag bits
    public const int RamMapLocalFlags    = 0x1336;   // "first time" flags (command 02), index + $0D0C
    public const int RamActorTable       = 0x07DE;   // $20 bytes per actor: 1 leader, 2-5 followers, 6 vehicle, 7+n map character n
    public const int ActorRecordSize     = 0x20;
    public const int RamEventBase        = 0x0D0F;   // u24: start of the running event (jump base)
    public const int RamScriptPointer    = 0x0D12;   // u24 T of the running script ($0D14 = FF means none)
    public const int RamCurrentMap       = 0x0D15;
    public const int RamSpeakerActor     = 0x0D0D;
    public const int RamWaitFrames       = 0x0D26;   // frames left in a script wait
    public const int RamScriptBlocked    = 0x07B4;   // FF while the script waits
    public const int RamFadeState        = 0x077B;   // non-zero while the screen fades
}
