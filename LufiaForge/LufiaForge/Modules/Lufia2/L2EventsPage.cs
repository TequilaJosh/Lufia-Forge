using CommunityToolkit.Mvvm.ComponentModel;
using LufiaForge.Core.Lufia2;

namespace LufiaForge.Modules.Lufia2;

/// <summary>A map's event scripts as a listing: the start-up script, then what runs on load, in scenes, when
/// talking to a character and when stepping on a trigger.</summary>
public partial class L2EventsPage : L2Page
{
    [ObservableProperty] private string _listing = "";
    [ObservableProperty] private string _summary = "";

    protected override IEnumerable<string> ReadNames() => Enumerable.Range(0, L2Maps.Count).Select(m =>
    {
        string n = L2Maps.Name(Rom!, m);
        return $"{m:X2}  {(n.Length > 0 && !n.Contains('~') ? n : "Map " + m.ToString("X2"))}";
    });

    protected override void Load(int index)
    {
        var scripts = L2Script.MapScripts(Rom!, index);
        var sb = new System.Text.StringBuilder();
        int commands = 0, texts = 0;
        foreach (var (title, ops) in scripts)
        {
            sb.AppendLine($"== {title}");
            foreach (var o in ops)
            {
                if (o.IsText) { texts++; sb.AppendLine($"{o.At:X6}    \"{o.Text.Replace("\n", "\n            ")}\""); }
                else { commands++; sb.AppendLine($"{o.At:X6}  {o.Text}"); }
            }
            sb.AppendLine();
        }
        Listing = sb.ToString();
        Summary = $"{scripts.Count} scripts, {commands} commands, {texts} pieces of text. Addresses are file offsets; flags are bits at $7E:077E, variables bytes at $7E:079E. Edit the text on the Text page.";
    }

    protected override bool IsField(string p) => false;
    protected override string? Write() => null;
}
