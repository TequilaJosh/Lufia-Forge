using LufiaForge.Core.Maps;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace LufiaForge.Modules.MapEditor;

/// <summary>One line of the event as raw bytes.</summary>
public sealed class ByteRow
{
    public int Index { get; init; }
    public string LineText => $"{Index + 1}";
    public string OffsetText { get; init; } = "";
    public string Decoded { get; init; } = "";
    public UIElement HexView { get; init; } = null!;
    public TextBlock Colored { get; init; } = null!;
    public TextBox? Editor { get; init; }
}

/// <summary>
/// Raw view of an event: each line's bytes as they will be written (jumps recalculated), colour-coded, with the
/// decoded meaning beside them. Command bytes can be edited as hex; the line selection is shared with the Lines view.
/// </summary>
public partial class RawBytesView : UserControl
{
    private EventEditorPanel? _panel;
    private List<(ScriptOp Op, int Offset, byte[] Bytes)>? _layout;
    private bool _syncing;

    private static readonly Brush OpBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly Brush TextBrush = Brushes.White;
    private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xC8, 0x7A));
    private static readonly Brush JumpBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xE0, 0xFF));
    private static readonly Brush UnknownBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x3C));
    private static readonly Brush OperandBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xF0));

    public RawBytesView() { InitializeComponent(); }

    public void Bind(EventEditorPanel panel) { _panel = panel; Refresh(); }

    /// <summary>Rebuild the rows from the current lines.</summary>
    public void Refresh()
    {
        if (_panel?.Script is not { } script) return;
        _layout = script.PreviewLayout();
        var rows = new List<ByteRow>();
        for (int i = 0; i < script.Ops.Count; i++)
        {
            var op = script.Ops[i];
            var entry = _layout?.FirstOrDefault(l => ReferenceEquals(l.Op, op));
            byte[] bytes = entry?.Bytes ?? op.Bytes;
            int offset = entry?.Offset ?? op.Offset;
            var colored = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Colorize(colored, op, bytes);
            TextBox? editor = null;
            if (!op.IsText)
            {
                editor = new TextBox
                {
                    Text = string.Join(" ", bytes.Select(b => b.ToString("X2"))), FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0), Tag = i,
                    ToolTip = "Edit the hex and press Enter (Esc cancels)",
                };
                editor.KeyDown += Editor_KeyDown;
            }
            var stack = new StackPanel();
            stack.Children.Add(colored);
            if (editor != null) stack.Children.Add(editor);
            rows.Add(new ByteRow
            {
                Index = i, OffsetText = op.IsNew ? "new" : $"{offset:X6}",
                Decoded = op.IsText ? $"{EventScript.SpeakerName(op.Bytes)}: {op.Text.Replace("\n", " ")}" : script.Describe(op),
                HexView = stack, Colored = colored, Editor = editor,
            });
        }
        _syncing = true;
        int keep = Rows.SelectedIndex;
        Rows.ItemsSource = rows;
        Rows.SelectedIndex = Math.Min(keep, rows.Count - 1);
        _syncing = false;
        ShowEditor();

        int now = script.BuiltLength(), was = script.End - script.Start;
        SizeText.Text = now < 0 ? "A jump lands inside a command: fix it in Lines before saving."
            : now == was ? $"{now} bytes — same size as in the ROM (written in place)."
            : $"{now} bytes (was {was}, {(now > was ? "+" : "")}{now - was}) — the event will be moved when applied.";
    }

    private static void Colorize(TextBlock tb, ScriptOp op, byte[] bytes)
    {
        if (bytes.Length == 0) return;
        var def = op.IsText ? null : EventCommands.Find(bytes[0]);
        var jumps = op.IsText ? new HashSet<int>() : EventScript.JumpPositions(bytes).SelectMany(p => new[] { p, p + 1 }).ToHashSet();
        int openerLen = op.IsText ? op.Bytes.Length : 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            Brush brush;
            bool underline = false;
            if (op.IsText) brush = i < openerLen ? OpBrush : b < 0x10 ? CodeBrush : TextBrush;
            else if (i == 0) brush = def == null ? UnknownBrush : OpBrush;
            else if (jumps.Contains(i)) { brush = JumpBrush; underline = true; }
            else brush = def == null ? UnknownBrush : OperandBrush;
            var run = new Run(b.ToString("X2") + " ") { Foreground = brush };
            if (underline) run.TextDecorations = TextDecorations.Underline;
            tb.Inlines.Add(run);
        }
    }

    /// <summary>Select a line (from the Lines view).</summary>
    public void Select(int index)
    {
        if (Rows.Items.Count == 0) return;
        _syncing = true;
        Rows.SelectedIndex = Math.Clamp(index, 0, Rows.Items.Count - 1);
        Rows.ScrollIntoView(Rows.SelectedItem);
        _syncing = false;
        ShowEditor();
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowEditor();
        if (!_syncing && Rows.SelectedIndex >= 0) _panel?.SelectLine(Rows.SelectedIndex, fromBytes: true);
    }

    private void ShowEditor()
    {
        if (Rows.ItemsSource is not IEnumerable<ByteRow> rows) return;
        foreach (var r in rows)
            if (r.Editor != null) r.Editor.Visibility = r.Index == Rows.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not int index || _panel == null) return;
        if (e.Key == Key.Escape) { Refresh(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string? error = ApplyHex(index, tb.Text);
        if (error != null) { SizeText.Text = "✖ " + error; return; }
        Refresh();
    }

    /// <summary>Turn typed hex into one or more commands replacing line <paramref name="index"/>.</summary>
    private string? ApplyHex(int index, string text)
    {
        var script = _panel!.Script;
        var op = script.Ops[index];
        var parts = text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();
        foreach (var p in parts)
        {
            var hex = p.StartsWith("$") ? p[1..] : p;
            for (int k = 0; k + 1 < hex.Length + 1; k += 2)
            {
                if (k + 2 > hex.Length || !byte.TryParse(hex.AsSpan(k, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                    return $"\"{p}\" isn't hex bytes.";
                bytes.Add(b);
            }
        }
        if (bytes.Count == 0)
        {
            var err = script.Remove(op);
            if (err != null) return err;
            _panel.LinesChangedExternally(Math.Max(0, index - 1));
            return null;
        }
        // split into whole commands
        var commands = new List<byte[]>();
        for (int p = 0; p < bytes.Count;)
        {
            int len = EventScript.CommandLength(bytes[p], p + 1 < bytes.Count ? bytes[p + 1] : 0);
            if (len == 0) return $"{bytes[p]:X2} isn't a command the engine has.";
            if (len < 0) return $"{bytes[p]:X2} starts a dialogue box — add dialogue from the Lines view.";
            if (p + len > bytes.Count) return $"Command {bytes[p]:X2} needs {len} bytes; only {bytes.Count - p} given.";
            commands.Add(bytes.Skip(p).Take(len).ToArray());
            p += len;
        }
        var typed = commands[0];
        var setErr = script.SetCommandBytes(op, typed);
        if (setErr != null) return setErr;
        RetargetTypedJumps(op, typed);
        for (int k = 1; k < commands.Count; k++)
        {
            var added = script.NewCommand(commands[k], index + k);
            RetargetTypedJumps(added, commands[k]);
        }
        _panel.LinesChangedExternally(index);
        return null;
    }

    /// <summary>A typed jump value that differs from the computed one moves the jump to the line at that offset.</summary>
    private void RetargetTypedJumps(ScriptOp op, byte[] typed)
    {
        if (_layout == null) return;
        var positions = EventScript.JumpPositions(typed);
        var current = _layout.FirstOrDefault(l => ReferenceEquals(l.Op, op)).Bytes;
        var script = _panel!.Script;
        for (int k = 0; k < positions.Count && k < op.Targets.Count; k++)
        {
            int pos = positions[k];
            int value = typed[pos] | (typed[pos + 1] << 8);
            if (current != null && current.Length > pos + 1 && (current[pos] | (current[pos + 1] << 8)) == value) continue;
            int abs = script.JumpBase + value;
            var target = _layout.FirstOrDefault(l => l.Offset == abs).Op;
            op.Targets[k].Op = target;
            op.Targets[k].Absolute = abs;
        }
    }
}
