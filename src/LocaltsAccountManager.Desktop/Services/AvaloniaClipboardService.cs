using Avalonia.Controls;
using LocaltsAccountManager.ViewModels;

namespace LocaltsAccountManager.Desktop.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public Window? Host { get; set; }

    public async Task SetTextAsync(string text)
    {
        var clipboard = Host?.Clipboard ?? throw new InvalidOperationException("Clipboard is not available.");
        await clipboard.SetTextAsync(text).ConfigureAwait(true);
    }
}
