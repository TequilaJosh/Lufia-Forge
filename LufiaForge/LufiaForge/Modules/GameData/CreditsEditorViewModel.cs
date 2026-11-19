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

// the staff roll at the end of the game (see CreditsRoll), one card at a time with a preview
public partial class CreditsEditorViewModel : GameDataEditorBase
{
    // the editor footer shows a ROM offset, this editor doesn't have one of its own
    public string OffsetText => "";

    public ObservableCollection<CreditCardRow> Cards { get; } = new();
    [ObservableProperty] private CreditCardRow? _selected;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private ImageSource? _preview;
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private bool _busy;

    // after Undo the card that was showing stays showing
    protected override void OnRomLoaded() => _ = Reload(Selected?.Card);

    // finds the cards again in the background. then it shows whatever card got picked by then, else keep
    private async Task Reload(CreditsRoll.Card? keep)
    {
        if (Ctx == null) return;
        var rom = Ctx.Rom;
        Busy = true;
        string oldStatus = Status;
        Status = "Finding the credits...";
        var (cards, mapNames) = await Task.Run(() => (CreditsRoll.Find(rom), MapCatalog.ScanNamed(rom).ToDictionary(m => m.MapId, m => m.Label)));
        if (!ReferenceEquals(rom, Ctx?.Rom)) return;   // another ROM got loaded meanwhile, its own reload fills the list
        // a card typed in meanwhile gets written first (refilling the list would lose the typing). if it can't be
        // written the list stays as it is
        if (HasPendingEdits && WritePendingEdits() != null) { Busy = false; return; }
        keep = Selected?.Card ?? keep;
        Cards.Clear();
        foreach (var card in cards)
            Cards.Add(new CreditCardRow
            {
                Card = card, Label = card.Title,
                Where = $"{mapNames.GetValueOrDefault(card.Map, $"Map {card.Map:X2}")}, event {card.Event}",
            });
        Selected = Cards.FirstOrDefault(r => keep != null && r.Card.Map == keep.Map && r.Card.Event == keep.Event && r.Card.Op == keep.Op) ?? Cards.FirstOrDefault();
        Busy = false;
        Status = keep == null ? $"{Cards.Count} credit cards in the ending." : oldStatus;
    }

    partial void OnSelectedChanged(CreditCardRow? value) => LoadSelected();

    // in a freshly read list a card counts as the same card if it has the same map, event and text command
    protected override object? SelectionKey(object entry) =>
        entry is CreditCardRow row ? (row.Card.Map, row.Card.Event, row.Card.Op) : base.SelectionKey(entry);

    protected override void LoadRecord()
    {
        if (Selected == null) { EditText = ""; Preview = null; return; }
        EditText = Selected.Card.Text.Replace("\n", Environment.NewLine);
    }

    partial void OnEditTextChanged(string value)
    {
        if (Ctx == null || Selected == null) { Preview = null; return; }
        string text = value.Replace("\r", "");
        var pixels = CreditsRoll.Render(Ctx.Rom, text, Selected.Card.Row);
        var bmp = BitmapSource.Create(256, 224, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
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
        var card = Selected.Card;
        var script = EventScript.Load(rom, card.Map, card.Event);
        if (script == null || card.Op >= script.Ops.Count || !script.Ops[card.Op].IsText) { Refuse("This card can't be found any more; reload the ROM."); return; }
        script.Ops[card.Op].Text = EditText.Replace("\r", "");
        if (!script.IsModified) { Status = "Nothing changed."; return; }
        bool expand = rom.Length >= MapWriter.ExpandedSize;
        if (!script.FitsInPlace() && !expand)
        {
            if (!AutoWriting && MessageBox.Show("The new text is a different length, so its event has to be moved. To store it, Lufia Forge needs to expand " +
                    "the ROM from 1 MB to 2 MB. Continue?", "Expand ROM?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            { Refuse("Not applied: the ROM wasn't expanded."); return; }
            expand = true;
        }
        string result = "";
        if (!Commit($"Credits: {Selected.Label}", () => result = script.Save(expand))) return;
        if (result.Length > 0) Status = $"Credits written: {result} (Save Project keeps it).";
        _ = Reload(card);
    }
}
