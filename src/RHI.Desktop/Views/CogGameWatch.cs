using System.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// w14 final review M1: a ⚙ open on the page belongs to the game it was opened for. Its buttons read the selection when
/// clicked, so once the selection moves to another game (or none) the ⚙ is closed, as Spotlight's side panel does; a
/// refresh that clears the selection closes it too (its rows are rebuilt).
/// </summary>
internal sealed class CogGameWatch : IDisposable
{
    private readonly MainWindowViewModel _vm;
    private readonly DetectedGame? _game;
    private Action? _close;

    public CogGameWatch(MainWindowViewModel vm, Action close)
    {
        _vm = vm;
        _game = vm.SelectedGame;
        _close = close;
        vm.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedGame) || ReferenceEquals(_vm.SelectedGame, _game)) return;
        var close = _close;
        Dispose();
        close?.Invoke();
    }

    public void Dispose()
    {
        _close = null;
        _vm.PropertyChanged -= OnChanged;
    }
}
