using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;

namespace LufiaForge.Modules.Events;

/// <summary>Deleting an event after saying exactly what changes (shared by the Events and Cutscenes tabs).</summary>
public static class EventDelete
{
    /// <summary>Asks, then deletes. Returns a description of what was done, or null when cancelled.</summary>
    public static string? Run(Window? owner, RomBuffer rom, int mapId, int ev, string mapLabel, Func<int, string> otherMapLabel)
    {
        var st = MapEvents.FindStarters(rom, mapId, ev);
        var lines = new List<string>();
        foreach (int c in st.Characters) lines.Add($"• Talking to character {c + 1} will do nothing (the character stays).");
        foreach (int a in st.Areas) lines.Add($"• Step-on area T{a} is switched off.");
        foreach (var grp in st.Chains.GroupBy(c => (c.Map, c.Event)))
            lines.Add($"• Event {grp.Key.Event} of {otherMapLabel(grp.Key.Map)} no longer continues with this event ({grp.Count()} \"run event\" command{(grp.Count() == 1 ? "" : "s")} removed).");
        if (lines.Count == 0) lines.Add("• Nothing on the map or in other events starts it.");
        string text = $"Delete event {ev} of {mapLabel}?\n\nIts lines are removed (it becomes an empty event, so nothing that still points at it can crash the game).\n\n" +
                      string.Join("\n", lines) + "\n\nEdit > Undo takes it all back.";
        var r = owner != null
            ? MessageBox.Show(owner, text, "Delete event", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(text, "Delete event", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (r != MessageBoxResult.OK) return null;
        MapEvents.Delete(rom, mapId, ev, st);
        return $"Deleted event {ev} of {mapLabel}" + (st.Any ? $" ({st.Areas.Count} area(s) switched off, {st.Chains.Count} run-event command(s) removed)" : "");
    }
}
