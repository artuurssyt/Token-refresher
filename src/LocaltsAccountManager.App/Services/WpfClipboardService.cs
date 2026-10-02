using System.Windows;
using LocaltsAccountManager.ViewModels;

namespace LocaltsAccountManager.App.Services;

public sealed class WpfClipboardService : IClipboardService
{
    private readonly IUiDispatcher _ui;

    public WpfClipboardService(IUiDispatcher ui)
    {
        _ui = ui;
    }

    public Task SetTextAsync(string text) =>
        _ui.InvokeAsync(() => Clipboard.SetDataObject(text, copy: true));
}
