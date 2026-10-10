using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Binding = System.Windows.Data.Binding;
using TextBox = System.Windows.Controls.TextBox;

namespace LufiaForge.Modules.GameData.Views;

public partial class TitleEditorView : UserControl
{
    public TitleEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TitleEditorViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(TitleEditorViewModel.Zoom) or nameof(TitleEditorViewModel.Canvas)) Layout();
                    if (e.PropertyName == nameof(TitleEditorViewModel.EditFront)) BackLayer.IsChecked = !vm.EditFront;
                };
                BackLayer.IsChecked = !vm.EditFront;
                Layout();
            }
        };
        BackLayer.Checked += (_, _) => { if (DataContext is TitleEditorViewModel vm) vm.EditFront = false; };
        PreviewKeyDown += OnKeys;
        Focusable = true;
    }

    private TitleEditorViewModel? Vm => DataContext as TitleEditorViewModel;

    private void Layout()
    {
        if (Vm is not { } vm) return;
        Paper.Width = vm.LayerW * vm.Zoom; Paper.Height = vm.LayerH * vm.Zoom;
        ScreenFrame.Margin = new Thickness(Core.Maps.TitleScreen.ScreenX * vm.Zoom, Core.Maps.TitleScreen.ScreenY * vm.Zoom, 0, 0);
        ScreenFrame.Width = Core.Maps.TitleScreen.ScreenW * vm.Zoom; ScreenFrame.Height = Core.Maps.TitleScreen.ScreenH * vm.Zoom;
    }

    private (int X, int Y) Pixel(MouseEventArgs e)
    {
        var p = e.GetPosition(Paper);
        int z = Math.Max(1, Vm?.Zoom ?? 1);
        return ((int)Math.Floor(p.X / z), (int)Math.Floor(p.Y / z));
    }

    private void Paper_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        Focus();
        var (x, y) = Pixel(e);
        Vm.StrokeStart(x, y, e.ChangedButton == MouseButton.Right);
        Paper.CaptureMouse();
        e.Handled = true;
    }

    private void Paper_MouseMove(object sender, MouseEventArgs e)
    {
        if (Vm == null || !Paper.IsMouseCaptured) return;
        var (x, y) = Pixel(e);
        Vm.StrokeMove(x, y);
    }

    private void Paper_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Vm?.StrokeEnd();
        Paper.ReleaseMouseCapture();
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TitleSwatch s }) Vm?.SelectColourCommand.Execute(s.Index);
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Vm == null || e.OriginalSource is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { Vm.UndoCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Vm.RedoCommand.Execute(null); e.Handled = true; }
    }
}

/// <summary>Zoom 1-4 ↔ combo index 0-3.</summary>
public sealed class TitleZoomConverter : IValueConverter
{
    public static readonly TitleZoomConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int z ? z - 1 : 1;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is int i && i >= 0 ? i + 1 : 2;
}

/// <summary>Tool ↔ radio button (parameter = the tool's name).</summary>
public sealed class TitleToolConverter : IValueConverter
{
    public static readonly TitleToolConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p as string;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && Enum.TryParse<TitleEditorViewModel.Tool>(p as string, out var tool) ? tool : Binding.DoNothing;
}
