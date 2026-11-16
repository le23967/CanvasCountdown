using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CanvasCountdown.App.Infrastructure;

/// <summary>
/// Shows dialogs one at a time.
/// </summary>
/// <remarks>
/// WinUI throws if a second dialog opens while one is showing — which happens
/// as soon as, say, an error arrives while the import preview is open. Each
/// dialog therefore waits for the one before it to close.
/// </remarks>
internal static class Dialogs
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await OneAtATime.WaitAsync();
        try
        {
            dialog.Style ??= Application.Current.Resources["DefaultContentDialogStyle"] as Style;
            return await dialog.ShowAsync();
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    public static Task<ContentDialogResult> ConfirmAsync(XamlRoot root, string title, string message, string confirm, string cancel = "Cancel")
        => ShowAsync(new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Close,
        });

    public static Task<ContentDialogResult> InformAsync(XamlRoot root, string title, string message)
        => ShowAsync(new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });
}
