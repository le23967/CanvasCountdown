using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CanvasCountdown.App.Views;

/// <summary>"3 tasks added — Undo", for ten seconds; the toolbar keeps the way back after that.</summary>
public sealed partial class UndoToast : UserControl
{
    public UndoToast()
    {
        InitializeComponent();
    }

    public MainViewModel ViewModel => AppServices.Current.ViewModel;

    private void OnUndo(object sender, RoutedEventArgs e) => _ = ViewModel.UndoLastAdditionAsync();

    private void OnKeep(object sender, RoutedEventArgs e) => ViewModel.DismissUndoToast();
}
