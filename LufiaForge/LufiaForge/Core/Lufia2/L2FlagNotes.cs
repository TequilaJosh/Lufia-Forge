namespace LufiaForge.Core.Lufia2;

// the names and notes given to lufia 2 flags (story flags, extra flags, counters, chest flags, puzzle switches, doors).
// with a project open they're kept in it (LufiaProject.FlagNotes, saved with the project and taken along by a merge),
// without one they last until Lufia Forge closes. a name given here shows instead of the game's own label
// (L2StoryFlags), and a story flag with a name never gets handed out as free
public static class L2FlagNotes
{
    private static Dictionary<string, FlagNote> _loose = new();

    // the open project's notes (null = no project)
    public static Dictionary<string, FlagNote>? Project { get; set; }

    private static Dictionary<string, FlagNote> Store => Project ?? _loose;

    // the notes are kept (a project is open)
    public static bool Kept => Project != null;

    // a name or note changed (the project has something to save)
    public static event Action? Changed;

    // "story:105", "door:4A03" (map 4A, door 3)
    public static string Key(L2FlagIndex.Kind kind, int number) => $"{kind.ToString().ToLowerInvariant()}:{number:X}";

    public static string? Name(L2FlagIndex.Kind kind, int number) =>
        Store.TryGetValue(Key(kind, number), out var note) && note.Name.Length > 0 ? note.Name : null;

    public static string? Note(L2FlagIndex.Kind kind, int number) =>
        Store.TryGetValue(Key(kind, number), out var note) && note.Note.Length > 0 ? note.Note : null;

    // gives a flag a name and a note (both empty = forgets it)
    public static void Set(L2FlagIndex.Kind kind, int number, string? name, string? note)
    {
        string key = Key(kind, number);
        name = (name ?? "").Trim();
        note = (note ?? "").Trim();
        var had = Store.GetValueOrDefault(key);
        if (name.Length == 0 && note.Length == 0)
        {
            if (!Store.Remove(key)) return;
        }
        else
        {
            if (had != null && had.Name == name && had.Note == note) return;
            Store[key] = new FlagNote(name, note);
        }
        Changed?.Invoke();
    }

    // every flag of a kind that has a name or a note
    public static IEnumerable<int> Named(L2FlagIndex.Kind kind)
    {
        string prefix = kind.ToString().ToLowerInvariant() + ":";
        foreach (var key in Store.Keys)
            if (key.StartsWith(prefix) && int.TryParse(key[prefix.Length..], System.Globalization.NumberStyles.HexNumber, null, out int number)) yield return number;
    }
}
