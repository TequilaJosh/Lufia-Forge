using LufiaForge.Core;
using LufiaForge.Core.Maps;
using System.Windows;

namespace LufiaForge.Modules.Events;

// deletes an event after saying exactly what changes (the Events and Cutscenes tabs both use it)
public static class EventDelete
{
    // asks, then deletes. returns what was done, or null if cancelled
    public static string? Run(Window? owner, RomBuffer rom, int mapId, int ev, string mapLabel, Func<int, string> otherMapLabel)
    {
        var starters = MapEvents.FindStarters(rom, mapId, ev);
        var lines = new List<string>();
        foreach (int character in starters.Characters) lines.Add($"• Talking to character {character + 1} will do nothing (the character stays).");
        foreach (int area in starters.Areas) lines.Add($"• Step-on area T{area} is switched off.");
        foreach (var chain in starters.Chains.GroupBy(c => (c.Map, c.Event)))
            lines.Add($"• Event {chain.Key.Event} of {otherMapLabel(chain.Key.Map)} no longer continues with this event: it ends there instead ({chain.Count()} \"run event\" command{(chain.Count() == 1 ? "" : "s")} made an end).");
        if (lines.Count == 0) lines.Add("• Nothing on the map or in other events starts it.");
        string text = $"Delete event {ev} of {mapLabel}?\n\nIts lines are removed (it becomes an empty event, so nothing that still points at it can crash the game).\n\n" +
                      string.Join("\n", lines) + "\n\nEdit > Undo takes it all back.";
        var answer = owner != null
            ? MessageBox.Show(owner, text, "Delete event", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(text, "Delete event", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return null;
        // all or nothing: the starters get changed first, and emptying the event can still fail after that
        var before = rom.ToArray();
        try { MapEvents.Delete(rom, mapId, ev, starters); }
        catch (Exception ex)
        {
            if (!rom.SameAs(before)) rom.ReplaceAll(before);
            if (owner != null) MessageBox.Show(owner, "The event couldn't be deleted, so nothing was changed: " + ex.Message, "Delete event", MessageBoxButton.OK, MessageBoxImage.Warning);
            else MessageBox.Show("The event couldn't be deleted, so nothing was changed: " + ex.Message, "Delete event", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return $"Deleted event {ev} of {mapLabel}" + (starters.Any ? $" ({starters.Areas.Count} area(s) switched off, {starters.Chains.Count} run-event command(s) made an end)" : "");
    }
}
