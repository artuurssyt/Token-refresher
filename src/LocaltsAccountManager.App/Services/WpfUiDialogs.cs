using System.IO;
using System.Windows;
using LocaltsAccountManager.ViewModels;
using Microsoft.Win32;

namespace LocaltsAccountManager.App.Services;

public sealed class WpfUiDialogs : IUiDialogs
{
    private readonly IUiDispatcher _ui;

    public WpfUiDialogs(IUiDispatcher ui)
    {
        _ui = ui;
    }

    public Task<string?> PickOpenFileAsync(string title, string filterName, params string[] extensions) =>
        _ui.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = BuildFilter(filterName, extensions)
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });

    public Task<string?> PickSaveFileAsync(
        string title,
        string filterName,
        string defaultFileName,
        string? initialDirectory,
        params string[] extensions) =>
        _ui.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = BuildFilter(filterName, extensions),
                FileName = defaultFileName
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        });

    public Task AlertAsync(string message, string title) =>
        _ui.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));

    public Task ErrorAsync(string message, string title) =>
        _ui.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));

    public Task WarnAsync(string message, string title) =>
        _ui.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning));

    public Task<bool> ConfirmAsync(string message, string title, bool warning = false) =>
        _ui.InvokeAsync(() =>
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                warning ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes);

    private static string BuildFilter(string filterName, string[] extensions)
    {
        var patterns = extensions.Length == 0
            ? "*.*"
            : string.Join(";", extensions.Select(NormalizePattern));
        return $"{filterName} ({patterns})|{patterns}|All files (*.*)|*.*";
    }

    private static string NormalizePattern(string extension)
    {
        var trimmed = extension.Trim();
        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.StartsWith('.'))
        {
            return "*" + trimmed;
        }

        return "*." + trimmed;
    }
}
