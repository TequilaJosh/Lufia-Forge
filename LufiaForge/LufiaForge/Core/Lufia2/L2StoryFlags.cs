using System.Text.Json;

namespace LufiaForge.Core.Lufia2;

/// <summary>
/// What each story flag and event counter of the US game means (Data/Lufia2_StoryFlags.json: a short label and a note
/// of where it is set, cleared and checked), worked out from the events that set them and their dialogue. 218 flags and
/// 13 counters: every one an event script sets, clears or checks. A hack's own flags have no label.
/// </summary>
public static class L2StoryFlags
{
    private sealed record Item(string label, string note);
    private sealed record Data(Dictionary<string, Item> flags, Dictionary<string, Item> counters);

    private static readonly Lazy<Data?> Names = new(() =>
    {
        try
        {
            using var st = typeof(L2StoryFlags).Assembly.GetManifestResourceStream("Data.Lufia2_StoryFlags.json");
            return st == null ? null : JsonSerializer.Deserialize<Data>(st);
        }
        catch (Exception) { return null; }
    });

    /// <summary>The flag's meaning in a few words (the name given in the project first), or null.</summary>
    public static string? Label(int flag) => L2FlagNotes.Name(L2FlagIndex.Kind.Story, flag) ?? GameLabel(flag);

    /// <summary>The game's own meaning of the flag (from the events), or null.</summary>
    public static string? GameLabel(int flag) => flag < 0x100 ? Names.Value?.flags.GetValueOrDefault(flag.ToString("X2"))?.label : null;

    /// <summary>Where the flag is set, cleared and checked, or null.</summary>
    public static string? Note(int flag) => flag < 0x100 ? Names.Value?.flags.GetValueOrDefault(flag.ToString("X2"))?.note : null;

    public static string? CounterLabel(int counter) => L2FlagNotes.Name(L2FlagIndex.Kind.Counter, counter) ?? GameCounterLabel(counter);

    public static string? GameCounterLabel(int counter) => Names.Value?.counters.GetValueOrDefault(counter.ToString("X2"))?.label;

    public static string? CounterNote(int counter) => Names.Value?.counters.GetValueOrDefault(counter.ToString("X2"))?.note;

    /// <summary>"37 (Idura took Jeros...)" or "37".</summary>
    public static string Named(int flag) => Label(flag) is { } l ? $"{L2ExtraFlags.Number(flag)} ({l})" : L2ExtraFlags.Number(flag);
}
