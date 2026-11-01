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

    /// <summary>The flag's meaning in a few words, or null.</summary>
    public static string? Label(int flag) => Names.Value?.flags.GetValueOrDefault(flag.ToString("X2"))?.label;

    /// <summary>Where the flag is set, cleared and checked, or null.</summary>
    public static string? Note(int flag) => Names.Value?.flags.GetValueOrDefault(flag.ToString("X2"))?.note;

    public static string? CounterLabel(int counter) => Names.Value?.counters.GetValueOrDefault(counter.ToString("X2"))?.label;

    public static string? CounterNote(int counter) => Names.Value?.counters.GetValueOrDefault(counter.ToString("X2"))?.note;

    /// <summary>"37 (Idura took Jeros...)" or "37".</summary>
    public static string Named(int flag) => Label(flag) is { } l ? $"{flag:X2} ({l})" : flag.ToString("X2");
}
