using CommunityToolkit.Mvvm.ComponentModel;
using LocaltsAccountManager.Core;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.ViewModels;

public sealed partial class AccountLibraryItemViewModel : ObservableObject
{
    public Guid AccountId { get; private set; }

    [ObservableProperty]
    private string _username = "Unknown";

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _uuid = string.Empty;

    [ObservableProperty]
    private string _expiryText = "—";

    [ObservableProperty]
    private bool _isExpired;

    [ObservableProperty]
    private bool _hasAccessToken;

    [ObservableProperty]
    private string _skinImageUrl = string.Empty;

    public string AvatarLetter =>
        string.IsNullOrWhiteSpace(Username) ? "?" : char.ToUpperInvariant(Username.Trim()[0]).ToString();

    public AccountRecord Record { get; private set; } = null!;

    public void UpdateFrom(AccountRecord record)
    {
        Record = record;
        AccountId = record.Id;
        Username = record.AuthenticatedMinecraftUsername ?? record.ProvidedUsername ?? "Unknown";
        Uuid = record.AuthenticatedMinecraftUuid ?? string.Empty;
        Subtitle = BuildSubtitle(record);
        HasAccessToken = !string.IsNullOrWhiteSpace(record.MinecraftAccessTokenReference);
        RefreshExpiry();
        LoadSkin();
        OnPropertyChanged(nameof(AvatarLetter));
    }

    public void RefreshExpiry()
    {
        if (Record.ProcessingState != ProcessingState.Succeeded)
        {
            ExpiryText = Record.ProcessingState.ToString();
            IsExpired = Record.ProcessingState is ProcessingState.Failed or ProcessingState.ReauthenticationRequired;
            return;
        }

        ExpiryText = TokenExpiryFormatter.FormatRemaining(Record.MinecraftAccessTokenExpiresAt);
        IsExpired = Record.MinecraftAccessTokenExpiresAt.HasValue &&
                    Record.MinecraftAccessTokenExpiresAt.Value <= DateTimeOffset.UtcNow;
        if (IsExpired)
        {
            ExpiryText = "expired";
        }
    }

    private void LoadSkin()
    {
        var key = !string.IsNullOrWhiteSpace(Uuid) ? Uuid : Username;
        SkinImageUrl = $"https://mc-heads.net/avatar/{Uri.EscapeDataString(key)}/32";
    }

    private static string BuildSubtitle(AccountRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ProvidedUsername) &&
            !string.Equals(record.ProvidedUsername, record.AuthenticatedMinecraftUsername, StringComparison.OrdinalIgnoreCase))
        {
            return $"(import: {record.ProvidedUsername})";
        }

        return record.ProcessingState == ProcessingState.Succeeded ? "(ready)" : $"({record.ProcessingState})";
    }
}
