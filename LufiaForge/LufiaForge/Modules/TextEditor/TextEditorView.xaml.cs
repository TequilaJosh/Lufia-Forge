using System.Windows.Controls;

namespace LufiaForge.Modules.TextEditor;

public partial class TextEditorView : UserControl
{
    public TextEditorView()
    {
        InitializeComponent();
        // shown again: re-read the list if another tab, Undo or a patch changed the ROM in the meantime
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) (DataContext as TextEditorViewModel)?.RefreshIfStale(); };
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox && listBox.SelectedItem != null) listBox.ScrollIntoView(listBox.SelectedItem);
    }
}
