using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VoiceGrid.ViewModels;

namespace VoiceGrid.Views;

public partial class MainWindow : Window
{
    private bool _pttDown;
    private bool _syncing;

    public MainWindow()
    {
        InitializeComponent();

        // Tunnel: we see keys before the DataGrid / TextBox can swallow them.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => ReleaseIfDown();     // window lost focus while key held -> treat as release

        ValuesGrid.SelectionChanged += (_, _) => SyncCursorFromGrid();
        ValuesGrid.CellPointerPressed += (_, _) => SyncCursorFromGrid();
        ValuesGrid.KeyUp += (_, _) => SyncCursorFromGrid();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.CursorChanged += SyncGridToCursor;
            SyncGridToCursor();
            _ = vm.InitializeAsync();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;

        if (vm.IsCapturingKey)
        {
            if (e.Key == Key.Escape) vm.CancelKeyCapture(); else vm.AssignKey(e.Key);
            e.Handled = true;
            return;
        }

        if (e.Key == vm.Settings.PushToTalkKey)
        {
            if (!_pttDown)                       // ignore OS key auto-repeat
            {
                _pttDown = true;
                _ = vm.OnPttDownAsync();
            }
            e.Handled = true;
            return;
        }

        if (vm.State == VoiceState.Confirming)
        {
            if (e.Key == Key.Enter) { vm.ConfirmCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { vm.RetryCommand.Execute(null); e.Handled = true; }
        }
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (Vm is { } vm && _pttDown && e.Key == vm.Settings.PushToTalkKey)
        {
            ReleaseIfDown();
            e.Handled = true;
        }
    }

    private void ReleaseIfDown()
    {
        if (!_pttDown || Vm is not { } vm) return;
        _pttDown = false;
        _ = vm.OnPttUpAsync();
    }

    // ---- grid <-> view-model cursor ------------------------------------

    private void SyncGridToCursor()
    {
        if (Vm is not { } vm) return;
        _syncing = true;
        try
        {
            ValuesGrid.SelectedIndex = vm.CurrentRow;
            var col = ValuesGrid.Columns[vm.CurrentCol + 1];
            try { ValuesGrid.CurrentColumn = col; } catch { /* highlight falls back to row only */ }
            if (ValuesGrid.SelectedItem != null) ValuesGrid.ScrollIntoView(ValuesGrid.SelectedItem, col);
            ValuesGrid.Focus();
        }
        finally { _syncing = false; }
    }

    private void SyncCursorFromGrid()
    {
        if (_syncing || Vm is not { } vm) return;
        vm.SetCursorFromGrid(ValuesGrid.SelectedIndex, ValuesGrid.CurrentColumn?.DisplayIndex ?? 1);
    }
}
