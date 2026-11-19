using System.Text.Json;

namespace LufiaForge.Core.Lufia2;

// what each story flag and event counter of the US game means (Data/Lufia2_StoryFlags.json: a short label and a note
// of where it's set, cleared and checked), worked out from the events that set them and their dialogue. 218 flags and
// 13 counters, every one an event script sets, clears or checks. a hack's own flags have no label
public static class L2StoryFlags
{
    private sealed record Item(string label, string note);
    private sealed record Data(Dictionary<string, Item> flags, Dictionary<string, Item> counters);

    private static readonly Lazy<Data?> Names = new(() =>
    {
        try
        {
            using var stream = typeof(L2StoryFlags).Assembly.GetManifestResourceStream("Data.Lufia2_StoryFlags.json");
            return stream == null ? null : JsonSerializer.Deserialize<Data>(stream);
        }
        catch (Exception) { return null; }
    });

    // the flag's meaning in a few words (the name given in the project first), or null
    public static string? Label(int flag) => L2FlagNotes.Name(L2FlagIndex.Kind.Story, flag) ?? GameLabel(flag);

    // the game's own meaning of the flag (from the events), or null
    public static string? GameLabel(int flag) => flag < 0x100 ? Names.Value?.flags.GetValueOrDefault(flag.ToString("X2"))?.label : null;

    // where the flag is set, cleared and checked, or null
    public static string? Note(int flag) => flag < 0x100 ? Names.Value?.flags.GetValueOrDefault(flag.ToString("X2"))?.note : null;

    public static string? CounterLabel(int counter) => L2FlagNotes.Name(L2FlagIndex.Kind.Counter, counter) ?? GameCounterLabel(counter);

    public static string? GameCounterLabel(int counter) => Names.Value?.counters.GetValueOrDefault(counter.ToString("X2"))?.label;

    public static string? CounterNote(int counter) => Names.Value?.counters.GetValueOrDefault(counter.ToString("X2"))?.note;

    // "37 (Idura took Jeros...)" or "37"
    public static string Named(int flag) => Label(flag) is { } label ? $"{L2ExtraFlags.Number(flag)} ({label})" : L2ExtraFlags.Number(flag);
}
