namespace LufiaForge.Core;

/// <summary>
/// Known constants for Lufia and the Fortress of Doom (US, SNES, Taito 1993).
///
/// Text encoding sources:
///   - Vegetaman (2010): https://sites.google.com/site/vegetaman/home/dictionary
///   - Digisalt Lufiatools 2.0 / AllOriginal.tbl (flobo, 2011) — authoritative byte map
/// </summary>
public static class Lufia1Constants
{
    // -------------------------------------------------------------------------
    // ROM Identity
    // -------------------------------------------------------------------------
    public const string RomTitle       = "LUFIA                ";
    public const int    ExpectedRomSize = 0x100000;
    public const int    SmcHeaderSize   = 0x200;

    // -------------------------------------------------------------------------
    // SNES Header
    // -------------------------------------------------------------------------
    public const int  SnesHeaderOffset     = 0x7FC0;
    public const int  SnesChecksumOffset   = 0x7FDE;
    public const int  SnesComplementOffset = 0x7FDC;
    public const int  SnesMappingOffset    = 0x7FD5;
    public const int  SnesRomSizeOffset    = 0x7FD7;
    public const byte LoRomMappingByte     = 0x20;

    // -------------------------------------------------------------------------
    // LoROM Address Translation
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Text / Dialogue Encoding
    // -------------------------------------------------------------------------
    // Full encoding map (from AllOriginal.tbl, Digisalt 2011):
    //
    //   0x00             End of string
    //   0x04             Page break (player presses A to continue)
    //   0x05             Newline within current dialogue box
    //   0x07 + 1 byte    Character name  (ID: 00=Hero, 01=Lufia, 02=Aguro, 03=Jerin...)
    //   0x0B + 1 byte    Town name       (ID: 01=Alekia, 02=Chatam, 03=Sheran...)
    //   0x0C + 2 bytes   Dictionary word, lowercase  (2-byte LE ptr + DictionaryBaseOffset)
    //   0x0D + 2 bytes   Dictionary word, capitalized (same pointer, first char uppercased)
    //   0x10–0x1F        Special single-byte sequences ('s, ed, ing, I'm, Sinistral...)
    //   0x20–0x7E        Standard printable ASCII
    //   0x80–0xBF        Single-byte compressed words, lowercase (the, you, to, it...)
    //   0xC0–0xFF        Single-byte compressed words, capitalized (The, You, To, It...)
    //
    // NOTE: 0x0B is TOWN names, NOT character names. Character names use 0x07.
    //       Previous Vegetaman research incorrectly identified 0x0B as char names.
    // -------------------------------------------------------------------------

    public const byte CtrlEndString        = 0x00;
    public const byte CtrlPageBreak        = 0x04;
    public const byte CtrlNewline          = 0x05;
    public const byte CtrlCharName         = 0x07;  // corrected from 0x0B
    public const byte CtrlTownName         = 0x0B;  // new — was incorrectly used as char name
    public const byte CtrlDictionaryRef    = 0x0C;  // lowercase; followed by 2-byte LE pointer
    public const byte CtrlDictionaryRefCap = 0x0D;  // capitalized; same pointer format as 0x0C

    // Dictionary region (US ROM, Vegetaman 2010)
    public const int DictionaryStartOffset = 0x054E19;
    public const int DictionaryEndOffset   = 0x0553CC;
    public const int DictionaryBaseOffset  = 0x48000;  // add to 2-byte LE ptr to get file offset

    // Dialogue text lives in banks $84–$AF (file 0x20000–0x57FFF).
    // Story/NPC dialogue: banks $84–$87 (0x20000–0x3FFFF)
    // Battle messages:    banks $88–$AF (0x40000–0x57FFF)
    public const int DialogueScanStartOffset = 0x20000;
    public const int DialogueScanEndOffset   = 0x58000;

    // -------------------------------------------------------------------------
    // Character names (0x07 + ID byte)
    // From AllOriginal.tbl: 0700=Hero, 0701=Lufia, 0702=Aguro, 0703=Jerin, ...
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Town names (0x0B + ID byte)
    // From AllOriginal.tbl: 0B01=Alekia, 0B02=Chatam, ...
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Special single-byte compressed sequences (0x10–0x1F)
    // From AllOriginal.tbl: 10='s  11=ed  12=ing  13=I'm  etc.
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Single-byte compressed words (0x80–0xFF)
    // 0x80–0xBF = lowercase, 0xC0–0xFF = capitalized counterparts.
    // From AllOriginal.tbl (Digisalt 2011) — 128 pairs total.
    // -------------------------------------------------------------------------
    public static readonly IReadOnlyDictionary<byte, string> SingleByteWords =
        new Dictionary<byte, string>
        {
            // Lowercase (0x80–0xBF)
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
            // Capitalized (0xC0–0xFF)
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

    // -------------------------------------------------------------------------
    // Control code display labels (for hex view / unknown byte fallback)
    // -------------------------------------------------------------------------
    public static readonly Dictionary<byte, string> ControlCodeLabels = new()
    {
        { 0x00, "[END]"    }, { 0x01, "[CTL:01]" }, { 0x02, "[CTL:02]" },
        { 0x03, "[CTL:03]" }, { 0x04, "[PAGE]"   }, { 0x05, "[NL]"     },
        { 0x06, "[CTL:06]" }, { 0x07, "[NAME]"   }, { 0x08, "[CTL:08]" },
        { 0x09, "[CTL:09]" }, { 0x0A, "[CTL:0A]" }, { 0x0B, "[TOWN]"   },
        { 0x0C, "[DICT]"   }, { 0x0D, "[DCAP]"   }, { 0x0E, "[CTL:0E]" },
        { 0x0F, "[CTL:0F]" },
    };

    // -------------------------------------------------------------------------
    // Graphics
    // -------------------------------------------------------------------------
    public const int TileByteSize     = 32;
    public const int Tile8bppByteSize = 64;
}
