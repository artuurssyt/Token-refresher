using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LocaltsAccountManager.ViewModels;

namespace LocaltsAccountManager.Desktop.Services;

public sealed class AvaloniaUiDialogs : IUiDialogs
{
    public Window? Host { get; set; }

    public async Task<string?> PickOpenFileAsync(string title, string filterName, params string[] extensions)
    {
        var provider = Host?.StorageProvider;
        if (provider is null)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(filterName) { Patterns = ToPatterns(extensions) },
                FilePickerFileTypes.All
            }
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(
        string title,
        string filterName,
        string defaultFileName,
        string? initialDirectory,
        params string[] extensions)
    {
        var provider = Host?.StorageProvider;
        if (provider is null)
        {
            return null;
        }

        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            start = await provider.TryGetFolderFromPathAsync(initialDirectory).ConfigureAwait(true);
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            SuggestedStartLocation = start,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(filterName) { Patterns = ToPatterns(extensions) }
            }
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    public Task AlertAsync(string message, string title) =>
        MessageDialog.ShowAsync(Host, title, message, MessageDialogKind.Info);

    public Task ErrorAsync(string message, string title) =>
        MessageDialog.ShowAsync(Host, title, message, MessageDialogKind.Error);

    public Task WarnAsync(string message, string title) =>
        MessageDialog.ShowAsync(Host, title, message, MessageDialogKind.Warning);

    public Task<bool> ConfirmAsync(string message, string title, bool warning = false) =>
        MessageDialog.ConfirmAsync(Host, title, message, warning);

    private static string[] ToPatterns(string[] extensions)
    {
        if (extensions.Length == 0)
        {
            return new[] { "*.*" };
        }

        return extensions.Select(e =>
        {
            var trimmed = e.Trim();
            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                return trimmed;
            }

            if (trimmed.StartsWith('.'))
            {
                return "*" + trimmed;
            }

            return "*." + trimmed;
        }).ToArray();
    }
}
