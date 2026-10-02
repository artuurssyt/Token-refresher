using System.Threading.Tasks;

namespace LocaltsAccountManager.ViewModels;

public interface IUiDialogs
{
    Task<string?> PickOpenFileAsync(string title, string filterName, params string[] extensions);

    Task<string?> PickSaveFileAsync(
        string title,
        string filterName,
        string defaultFileName,
        string? initialDirectory,
        params string[] extensions);

    Task AlertAsync(string message, string title);

    Task ErrorAsync(string message, string title);

    Task WarnAsync(string message, string title);

    Task<bool> ConfirmAsync(string message, string title, bool warning = false);
}
