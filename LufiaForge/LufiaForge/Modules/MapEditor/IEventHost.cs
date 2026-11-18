namespace LufiaForge.Modules.MapEditor;

// what the event editor needs from the screen that opened it
public interface IEventHost
{
    // ask if the ROM can be expanded to 2 MB
    bool ConfirmExpand(string what);
    // the event got written to the ROM (for the save list / window title)
    void EventSaved(string what);
    // show the "saved in the project" confirmation
    void ConfirmWritten(string heading, string details);
    // show where a story flag is used (optional)
    void ShowFlag(int flag) { }
}
