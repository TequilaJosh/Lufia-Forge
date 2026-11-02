using System.Windows.Controls;

namespace LufiaForge.Modules.TextEditor;

public partial class TextEditorView : UserControl
{
    public TextEditorView()
    {
        InitializeComponent();
        // shown again: the list is read again when another tab, Undo or a patch changed the ROM meanwhile
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) (DataContext as TextEditorViewModel)?.RefreshIfStale(); };
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem != null) lb.ScrollIntoView(lb.SelectedItem);
    }
}
