using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LufiaForge.Core.Maps;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LufiaForge.Modules.GameData;

public sealed class CreditCardRow
{
    public CreditsRoll.Card Card { get; init; } = null!;
    public string Label { get; init; } = "";
    public string Where { get; init; } = "";
}

/// <summary>The staff roll at the end of the game (see <see cref="CreditsRoll"/>): one card at a time, with a preview.</summary>
public partial class CreditsEditorViewModel : GameDataEditorBase
{
    public ObservableCollection<CreditCardRow> Cards { get; } = new();
    [ObservableProperty] private CreditCardRow? _selected;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private bool _busy;

    protected override void OnRomLoaded() => _ = Reload(null);

    private async Task Reload(CreditsRoll.Card? keep)
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        Busy = true;
        Status = "Finding the credits...";
        var (cards, names) = await Task.Run(() => (CreditsRoll.Find(rom), MapCatalog.ScanNamed(rom).ToDictionary(m => m.MapId, m => m.Label)));
        Cards.Clear();
        foreach (var c in cards)
            Cards.Add(new CreditCardRow
            {
                Card = c, Label = c.Title,
                Where = $"{names.GetValueOrDefault(c.Map, $"Map {c.Map:X2}")}, event {c.Event}",
            });
        Selected = Cards.FirstOrDefault(r => keep != null && r.Card.Map == keep.Map && r.Card.Event == keep.Event && r.Card.Op == keep.Op) ?? Cards.FirstOrDefault();
        Busy = false;
        if (keep == null) Status = $"{Cards.Count} credit cards in the ending.";
    }

    partial void OnSelectedChanged(CreditCardRow? value) => LoadSelected();

    protected override void LoadSelected()
    {
        if (Selected == null) { EditText = ""; Preview = null; return; }
        EditText = Selected.Card.Text.Replace("\n", Environment.NewLine);
    }

    partial void OnEditTextChanged(string value)
    {
        if (Ctx == null || Selected == null) { Preview = null; return; }
        string text = value.Replace("\r", "");
        var px = CreditsRoll.Render(Ctx.Rom, text, Selected.Card.Row);
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, px, 256 * 4);
        bmp.Freeze();
        Preview = bmp;
        int longest = text.Split('\n').Max(l => l.Length);
        Warning = longest > CreditsRoll.MaxLineLength ? $"A line has {longest} characters; the screen fits {CreditsRoll.MaxLineLength}." : "";
    }

    [RelayCommand]
    private void Centre() => EditText = CreditsRoll.Centre(EditText).Replace("\n", Environment.NewLine);

    [RelayCommand]
    private void Apply()
    {
        if (Ctx == null || Selected == null) return;
        var rom = Ctx.Rom;
        var c = Selected.Card;
        var s = EventScript.Load(rom, c.Map, c.Event);
        if (s == null || c.Op >= s.Ops.Count || !s.Ops[c.Op].IsText) { Status = "This card can't be found any more; reload the ROM."; return; }
        s.Ops[c.Op].Text = EditText.Replace("\r", "");
        if (!s.IsModified) { Status = "Nothing changed."; return; }
        bool expand = rom.Length >= MapWriter.ExpandedSize;
        if (!s.FitsInPlace() && !expand)
        {
            if (MessageBox.Show("The new text is a different length, so its event has to be moved. To store it, Lufia Forge needs to expand " +
                    "the ROM from 1 MB to 2 MB. Continue?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { Status = "Not applied."; return; }
            expand = true;
        }
        string result = "";
        Commit($"Credits: {Selected.Label}", () => result = s.Save(expand));
        if (result.Length > 0) Status = $"Credits written: {result} (unsaved until File > Save ROM).";
        _ = Reload(c);
    }
}
