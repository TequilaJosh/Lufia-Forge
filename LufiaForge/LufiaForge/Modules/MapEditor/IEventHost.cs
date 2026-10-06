namespace LufiaForge.Modules.MapEditor;

/// <summary>What the event editor needs from the screen that opened it.</summary>
public interface IEventHost
{
    /// <summary>Ask whether the ROM may be expanded to 2 MB.</summary>
    bool ConfirmExpand(string what);
    /// <summary>The event was written to the ROM (for the save list / window title).</summary>
    void EventSaved(string what);
    /// <summary>Show the "written to the ROM" confirmation.</summary>
    void ConfirmWritten(string heading, string details);
    /// <summary>Show where a story flag is used (optional).</summary>
    void ShowFlag(int flag) { }
}
