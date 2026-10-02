using CommunityToolkit.Mvvm.ComponentModel;
using LocaltsAccountManager.Core;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.ViewModels;

public partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(AccountRecord record)
    {
        UpdateFrom(record);
    }

    public AccountRecord Record { get; private set; } = null!;

    public Guid Id => Record.Id;
    public ProcessingState ProcessingState => Record.ProcessingState;
    public string? ProvidedUsername => Record.ProvidedUsername;
    public string? AuthenticatedMinecraftUsername => Record.AuthenticatedMinecraftUsername;
    public string? AuthenticatedMinecraftUuid => Record.AuthenticatedMinecraftUuid;
    public bool? ProvidedUsernameMismatch => Record.ProvidedUsernameMismatch;
    public int SourceLine => Record.SourceLine;
    public ErrorCategory ErrorCategory => Record.ErrorCategory;
    public string? ServiceErrorDetail => Record.ServiceErrorDetail;
    public FailureStage FailureStage => Record.FailureStage;
    public string? OAuthErrorCode => Record.OAuthErrorCode;
    public DateTimeOffset? MinecraftAccessTokenExpiresAt => Record.MinecraftAccessTokenExpiresAt;
    public DateTimeOffset? MicrosoftAccessTokenExpiresAt => Record.MicrosoftAccessTokenExpiresAt;
    public DateTimeOffset? RefreshCredentialUpdatedAt => Record.RefreshCredentialUpdatedAt;

    [ObservableProperty]
    private string _minecraftTokenRemaining = "—";

    [ObservableProperty]
    private string _minecraftTokenStatus = "Unknown";

    [ObservableProperty]
    private string _microsoftTokenRemaining = "—";

    [ObservableProperty]
    private string _refreshUpdatedText = "—";

    public void UpdateFrom(AccountRecord record)
    {
        Record = record;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(ProcessingState));
        OnPropertyChanged(nameof(ProvidedUsername));
        OnPropertyChanged(nameof(AuthenticatedMinecraftUsername));
        OnPropertyChanged(nameof(AuthenticatedMinecraftUuid));
        OnPropertyChanged(nameof(ProvidedUsernameMismatch));
        OnPropertyChanged(nameof(SourceLine));
        OnPropertyChanged(nameof(ErrorCategory));
        OnPropertyChanged(nameof(ServiceErrorDetail));
        OnPropertyChanged(nameof(FailureStage));
        OnPropertyChanged(nameof(OAuthErrorCode));
        OnPropertyChanged(nameof(MinecraftAccessTokenExpiresAt));
        OnPropertyChanged(nameof(MicrosoftAccessTokenExpiresAt));
        OnPropertyChanged(nameof(RefreshCredentialUpdatedAt));
        RefreshCountdown();
    }

    public void RefreshCountdown()
    {
        if (Record.ProcessingState == ProcessingState.Backoff && Record.BackoffUntil.HasValue)
        {
            MinecraftTokenRemaining = TokenExpiryFormatter.FormatRemaining(Record.BackoffUntil);
            MinecraftTokenStatus = Record.ErrorCategory == ErrorCategory.RateLimited ? "Rate limited" : "Retry pending";
            MicrosoftTokenRemaining = "—";
        }
        else if (Record.ProcessingState == ProcessingState.Failed && Record.ErrorCategory == ErrorCategory.RateLimited)
        {
            MinecraftTokenRemaining = "—";
            MinecraftTokenStatus = "Rate limited";
            MicrosoftTokenRemaining = "—";
        }
        else if (Record.ProcessingState == ProcessingState.Succeeded)
        {
            MinecraftTokenRemaining = TokenExpiryFormatter.FormatRemaining(Record.MinecraftAccessTokenExpiresAt);
            MinecraftTokenStatus = TokenExpiryFormatter.FormatStatus(Record.MinecraftAccessTokenExpiresAt);
            MicrosoftTokenRemaining = TokenExpiryFormatter.FormatRemaining(Record.MicrosoftAccessTokenExpiresAt);
        }
        else
        {
            MinecraftTokenRemaining = "—";
            MinecraftTokenStatus = Record.ProcessingState.ToString();
            MicrosoftTokenRemaining = "—";
        }

        RefreshUpdatedText = Record.RefreshCredentialUpdatedAt.HasValue
            ? Record.RefreshCredentialUpdatedAt.Value.ToLocalTime().ToString("g")
            : "—";
    }
}
