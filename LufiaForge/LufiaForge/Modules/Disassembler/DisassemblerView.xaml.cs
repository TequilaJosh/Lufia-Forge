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
            {
                vm.ScrollRequested -= ScrollToLine;   // (still hooked when a section stayed popped out while away)
                vm.ScrollRequested += ScrollToLine;
            }
        };

        Unloaded += (_, _) =>
        {
            // the disassembly popped out into its own window keeps following jumps while this tab is away
            if (DataContext is DisassemblerViewModel vm && !LufiaForge.Modules.Common.PopOutHost.AnyPoppedOut(this))
                vm.ScrollRequested -= ScrollToLine;
        };
    }

    // double-click a disassembly row -> follow its jump
    private void DisassemblyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DisassemblerViewModel vm) return;
        if (vm.SelectedLine?.HasJumpTarget == true)
            vm.FollowJumpCommand.Execute(vm.SelectedLine);
    }

    // click a cross-reference entry -> go to that address
    private void XrefEntry_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DisassemblerViewModel vm) return;
        if (sender is not System.Windows.Controls.TextBlock entry) return;

        // entries look like "$808000  label", the address is the first word
        string text = entry.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        string addressPart = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                              .TrimStart('$');
        if (int.TryParse(addressPart, NumberStyles.HexNumber, null, out int address))
            vm.NavigateTo(address);
    }

    // double-click a bookmark -> go there
    private void BookmarkItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DisassemblerViewModel vm) return;
        if (sender is not ListBoxItem { DataContext: Bookmark bookmark }) return;
        vm.NavigateToBookmarkCommand.Execute(bookmark);
    }

    // auto-scroll for live PC tracking
    private void ScrollToLine(DisassemblyLine line)
    {
        DisassemblyList.ScrollIntoView(line);
    }
}
