namespace LufiaForge.Core;

// story scenes for lufia and the fortress of doom (us snes).
//
// the scene boundaries came from scanning the rom for each scene's opening dialogue line and taking the file offset of
// the first entry that matched. offsets are sorted by rom position, the scene numbers follow the story so the text
// editor can show dialogue in story order.
//
// scenes with no boundary found (offset == -1) had no opening line that only showed up once in the scanned region,
// so their entries can end up under the scene before them or under "Battle / Menu"
public static class LufiaSceneData
{
    // every scene in story order (index 0 = before the story / battle+menu text)
    public static readonly SceneInfo[] AllScenes =
    {
        new( 0, "Battle / Menu"),
        new( 3, "Prologue"),
        new( 4, "Island Fortress"),
        new( 5, "Alekia 1"),
        new( 6, "Chatam 1"),
        new( 7, "Sheran 1"),
        new( 8, "Alekia 2"),
        new( 9, "Sheran 2"),
        new(10, "Alekia 3"),
        new(11, "Chatam 2"),
        new(12, "Treck 1"),
        new(13, "East Cave"),
        new(14, "The One's House"),
        new(15, "Treck 2"),
        new(16, "Maberia"),
        new(17, "Treck 3"),
        new(18, "Lorbenia"),
        new(19, "Grenoble 1"),
        new(20, "Old Cave 1"),
        new(21, "Grenoble 2"),
        new(22, "Northwest Tower"),
        new(23, "Kirof 1"),
        new(24, "Medan 1"),
        new(25, "Kirof 2"),
        new(26, "Ghost Cave"),
        new(27, "Kirof 3"),
        new(28, "Medan 2"),
        new(29, "Belgen 1"),
        new(30, "Dais"),
        new(31, "North Tower"),
        new(32, "Belgen 2"),
        new(33, "Guide Station"),
        new(34, "Jenoba"),
        new(35, "Medan Mining Cave"),
        new(36, "Red Tower"),
        new(37, "Elfrea"),
        new(38, "Odel 1"),
        new(39, "Lyden"),
        new(40, "Arus Cave"),
        new(41, "Arus"),
        new(42, "Tower of Grief"),
        new(43, "Platina"),
        new(44, "Carbis 1"),
        new(45, "Old Cave 5th Floor"),
        new(46, "Carbis 2"),
        new(47, "Tower of Light"),
        new(48, "Gayas Island Cave"),
        new(49, "Loire Island"),
        new(50, "Herat 1"),
        new(51, "Lyden (Wizard)"),
        new(52, "Herat 2"),
        new(53, "Aisen Tower"),
        new(54, "Carbis 3"),
        new(55, "Doom Island 1"),
        new(56, "Soshette"),
        new(57, "Carbis 4"),
        new(58, "Epro"),
        new(59, "Frederia"),
        new(60, "Dragon Shrine"),
        new(61, "Arubus"),
        new(62, "Glasdar Tower"),
        new(63, "Doom Island 2"),
        new(64, "The Ending"),
    };

    // scene boundaries sorted by rom file offset (to put entries in scenes).
    // from the rom scan, scenes with no match are left out
    private static readonly (int StartOffset, int SceneIndex)[] SceneBoundaries =
    {
        (0x0206A8, 12),  // Treck 1
        (0x020C2B, 17),  // Treck 3
        (0x021429, 13),  // East Cave
        (0x021486, 14),  // The One's House
        (0x021BDC, 18),  // Lorbenia
        (0x0220B3, 29),  // Belgen 1
        (0x023525, 21),  // Grenoble 2
        (0x023E58, 19),  // Grenoble 1
        (0x02458F, 20),  // Old Cave 1
        (0x02469A,  9),  // Sheran 2
        (0x02486F, 45),  // Old Cave 5th Floor
        (0x024AF2, 22),  // Northwest Tower
        (0x025133, 23),  // Kirof 1
        (0x025263, 25),  // Kirof 2
        (0x0254F2, 27),  // Kirof 3
        (0x025703, 28),  // Medan 2
        (0x025FE8, 24),  // Medan 1
        (0x0269D6, 35),  // Medan Mining Cave
        (0x026F8A, 26),  // Ghost Cave
        (0x0285B2, 33),  // Guide Station
        (0x028EF8, 34),  // Jenoba
        (0x029916, 36),  // Red Tower
        (0x02A376, 37),  // Elfrea
        (0x02AFEC, 38),  // Odel 1
        (0x02BA2F, 40),  // Arus Cave
        (0x02BED1, 41),  // Arus
        (0x02C923, 42),  // Tower of Grief
        (0x02CDC5, 43),  // Platina
        (0x02DA60, 39),  // Lyden
        (0x02E558,  7),  // Sheran 1
        (0x02E833, 44),  // Carbis 1
        (0x02EA52, 46),  // Carbis 2
        (0x02EF3D, 54),  // Carbis 3
        (0x02F190, 57),  // Carbis 4
        (0x02FC9E, 47),  // Tower of Light
        (0x030486, 48),  // Gayas Island Cave
        (0x0312ED, 49),  // Loire Island
        (0x03187A, 50),  // Herat 1
        (0x031ADF, 52),  // Herat 2
        (0x0321AB, 53),  // Aisen Tower
        (0x033252, 58),  // Epro
        (0x033906, 59),  // Frederia
        (0x033991, 64),  // The Ending
        (0x0341FD, 62),  // Glasdar Tower
        (0x034FDB,  3),  // Prologue
        (0x0352C2, 55),  // Doom Island 1
    };

    // SceneIndex -> SceneInfo
    private static readonly Dictionary<int, SceneInfo> _byIndex =
        AllScenes.ToDictionary(s => s.SceneIndex);

    // the scene (index + name) a rom file offset belongs to: the last boundary with StartOffset <= romOffset,
    // or scene 0 ("Battle / Menu") when no boundary comes before the offset
    public static SceneInfo GetScene(int romOffset)
    {
        int sceneIndex = 0;
        foreach (var (start, index) in SceneBoundaries)
        {
            if (start <= romOffset)
                sceneIndex = index;
            else
                break;   // the array is sorted by StartOffset, lowest first
        }
        return _byIndex.TryGetValue(sceneIndex, out var scene) ? scene : AllScenes[0];
    }
}

// a scene's story index and the name we show for it
public sealed class SceneInfo
{
    public int    SceneIndex { get; }
    public string SceneName  { get; }

    public SceneInfo(int sceneIndex, string sceneName)
    {
        SceneIndex = sceneIndex;
        SceneName  = sceneName;
    }

    public override string ToString() => SceneName;
}
