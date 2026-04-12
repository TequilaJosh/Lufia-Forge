using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LufiaForge.Modules.Disassembler;

public partial class DisassemblerView : UserControl
{
    public DisassemblerView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is DisassemblerViewModel vm)
                vm.ScrollRequested += ScrollToLine;
        };

        Unloaded += (_, _) =>
        {
            if (DataContext is DisassemblerViewModel vm)
                vm.ScrollRequested -= ScrollToLine;
        };
    }

    // -------------------------------------------------------------------------
    // Double-click on disassembly row → follow jump target
    // -------------------------------------------------------------------------

    private void DisassemblyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DisassemblerViewModel vm) return;
        if (vm.SelectedLine?.HasJumpTarget == true)
            vm.FollowJumpCommand.Execute(vm.SelectedLine);
    }

    // -------------------------------------------------------------------------
    // Click on cross-reference entry → navigate to that address
    // -------------------------------------------------------------------------

    private void XrefEntry_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DisassemblerViewModel vm) return;
        if (sender is not System.Windows.Controls.TextBlock tb) return;

        // Entry text format: "$808000  label" — parse the address from the first token
        string text = tb.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        string addrPart = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                              .TrimStart('$');
        if (int.TryParse(addrPart, NumberStyles.HexNumber, null, out int addr))
            vm.NavigateTo(addr);
    }

    // -------------------------------------------------------------------------
    // Bookmark double-click → navigate
    // -------------------------------------------------------------------------

    private void BookmarkItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DisassemblerViewModel vm) return;
        if (sender is not ListBoxItem { DataContext: Bookmark bm }) return;
        vm.NavigateToBookmarkCommand.Execute(bm);
    }

    // -------------------------------------------------------------------------
    // Auto-scroll for live PC tracking
    // -------------------------------------------------------------------------

    private void ScrollToLine(DisassemblyLine line)
    {
        DisassemblyList.ScrollIntoView(line);
    }
}
