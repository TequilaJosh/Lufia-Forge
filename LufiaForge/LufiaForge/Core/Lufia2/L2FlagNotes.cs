namespace LufiaForge.Core.Lufia2;

/// <summary>
/// The names and notes given to Lufia II flags (story flags, extra flags, counters, chest flags, puzzle switches, doors).
/// With a project open they are kept in it (<see cref="LufiaProject.FlagNotes"/>, saved with the project and taken
/// along by a merge); without one they last until Lufia Forge closes. A name given here is shown instead of the
/// game's own label (<see cref="L2StoryFlags"/>), and a story flag with a name is never handed out as free.
/// </summary>
public static class L2FlagNotes
{
    private static Dictionary<string, FlagNote> _loose = new();

    /// <summary>The open project's notes (null: no project).</summary>
    public static Dictionary<string, FlagNote>? Project { get; set; }

    private static Dictionary<string, FlagNote> Store => Project ?? _loose;

    /// <summary>The notes are kept (a project is open).</summary>
    public static bool Kept => Project != null;

    /// <summary>A name or note changed (the project has something to save).</summary>
    public static event Action? Changed;

    /// <summary>"story:105", "door:4A03" (map 4A, door 3).</summary>
    public static string Key(L2FlagIndex.Kind kind, int number) => $"{kind.ToString().ToLowerInvariant()}:{number:X}";

    public static string? Name(L2FlagIndex.Kind kind, int number) =>
        Store.TryGetValue(Key(kind, number), out var n) && n.Name.Length > 0 ? n.Name : null;

    public static string? Note(L2FlagIndex.Kind kind, int number) =>
        Store.TryGetValue(Key(kind, number), out var n) && n.Note.Length > 0 ? n.Note : null;

    /// <summary>Gives a flag a name and a note (both empty: forgets it).</summary>
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

    /// <summary>Every flag of a kind that has a name or a note.</summary>
    public static IEnumerable<int> Named(L2FlagIndex.Kind kind)
    {
        string prefix = kind.ToString().ToLowerInvariant() + ":";
        foreach (var k in Store.Keys)
            if (k.StartsWith(prefix) && int.TryParse(k[prefix.Length..], System.Globalization.NumberStyles.HexNumber, null, out int n)) yield return n;
    }
}
