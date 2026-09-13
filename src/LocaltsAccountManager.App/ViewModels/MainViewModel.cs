using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocaltsAccountManager.Core.Enums;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using LocaltsAccountManager.Infrastructure.Import;
using LocaltsAccountManager.Infrastructure.Paths;
using Microsoft.Win32;

namespace LocaltsAccountManager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IImportService _importService;
    private readonly IBatchProcessor _batchProcessor;
    private readonly IPoolProcessor _poolProcessor;
    private readonly IExportService _exportService;
    private readonly IAccountRepository _accountRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly INetworkDiagnosticsService _networkDiagnostics;
    private readonly IAuthenticationProfileStore _profileStore;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILocaltsService _localtsService;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _poolAutoManageTimer;
    private readonly ICollectionView _libraryView;

    private BatchRecord? _currentBatch;
    private AccountRowViewModel? _selectedAccount;

    public MainViewModel(
        IImportService importService,
        IBatchProcessor batchProcessor,
        IPoolProcessor poolProcessor,
        IExportService exportService,
        IAccountRepository accountRepository,
        IBatchRepository batchRepository,
        ISecureCredentialStore credentialStore,
        INetworkDiagnosticsService networkDiagnostics,
        IAuthenticationProfileStore profileStore,
        IAppSettingsStore settingsStore,
        ILocaltsService localtsService,
        DonutViewModel donut)
    {
        _importService = importService;
        _batchProcessor = batchProcessor;
        _poolProcessor = poolProcessor;
        _exportService = exportService;
        _accountRepository = accountRepository;
        _batchRepository = batchRepository;
        _credentialStore = credentialStore;
        _networkDiagnostics = networkDiagnostics;
        _profileStore = profileStore;
        _settingsStore = settingsStore;
        _localtsService = localtsService;
        Donut = donut;
        _batchProcessor.ProgressChanged += OnBatchProgressChanged;
        _poolProcessor.ProgressChanged += OnPoolProgressChanged;

        _libraryView = CollectionViewSource.GetDefaultView(LibraryAccounts);
        _libraryView.Filter = FilterLibraryAccount;

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += (_, _) => RefreshCountdowns();
        _countdownTimer.Start();

        _poolAutoManageTimer = new DispatcherTimer();
        _poolAutoManageTimer.Tick += (_, _) => _ = RunPoolAutoManageAsync();
    }

    public DonutViewModel Donut { get; }

    public ObservableCollection<AccountRowViewModel> PoolAccounts { get; } = new();

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    public ObservableCollection<AccountLibraryItemViewModel> LibraryAccounts { get; } = new();

    public ICollectionView LibraryView => _libraryView;

    [ObservableProperty]
    private string _librarySearchText = string.Empty;

    [ObservableProperty]
    private AccountLibraryItemViewModel? _selectedLibraryAccount;

    [ObservableProperty]
    private int _libraryAccountCount;

    [ObservableProperty]
    private int _libraryReadyCount;

    [ObservableProperty]
    private string _libraryStatusMessage = "Tokens appear here as each account finishes — export anytime without waiting for the full batch.";

    [ObservableProperty]
    private int _poolTotal;

    [ObservableProperty]
    private int _poolPending;

    [ObservableProperty]
    private int _poolSucceeded;

    [ObservableProperty]
    private int _poolFailed;

    [ObservableProperty]
    private int _poolReadyCount;

    [ObservableProperty]
    private int _poolRemovedCount;

    [ObservableProperty]
    private string _poolStatusMessage = "All imported accounts live here and refresh automatically. Bad accounts (except rate limits) remove themselves.";

    [ObservableProperty]
    private AccountRowViewModel? _selectedPoolAccount;

    [ObservableProperty]
    private string _localtsApiKeyInput = string.Empty;

    [ObservableProperty]
    private string _localtsConnectionStatus = "Localts API key not configured.";

    [ObservableProperty]
    private bool _poolAutoManageEnabled;

    partial void OnLibrarySearchTextChanged(string value) => _libraryView.Refresh();

    partial void OnPoolAutoManageEnabledChanged(bool value)
    {
        var settings = _settingsStore.Load();
        if (settings.PoolAutoManageEnabled == value && settings.PoolAutoManageOptInAcknowledged)
        {
            return;
        }

        settings.PoolAutoManageEnabled = value;
        settings.PoolAutoManageOptInAcknowledged = true;
        _settingsStore.Save(settings);

        _poolAutoManageTimer.Stop();
        if (value)
        {
            StartPoolAutoManageTimer();
            PoolStatusMessage = "Pool auto-refresh enabled (background timer only — not on launch).";
        }
        else
        {
            PoolStatusMessage = "Pool auto-refresh off. Use Refresh Pool when you want to refresh.";
        }
    }

    public AccountRowViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                OnPropertyChanged(nameof(SelectedMinecraftTokenRemaining));
                OnPropertyChanged(nameof(SelectedMinecraftTokenStatus));
                OnPropertyChanged(nameof(SelectedMicrosoftTokenRemaining));
                OnPropertyChanged(nameof(SelectedRefreshUpdatedText));
                RefreshBatchAccountCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedMinecraftTokenRemaining => SelectedAccount?.MinecraftTokenRemaining ?? "—";
    public string SelectedMinecraftTokenStatus => SelectedAccount?.MinecraftTokenStatus ?? "Unknown";
    public string SelectedMicrosoftTokenRemaining => SelectedAccount?.MicrosoftTokenRemaining ?? "—";
    public string SelectedRefreshUpdatedText => SelectedAccount?.RefreshUpdatedText ?? "—";

    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    private string _statusMessage = "Ready.";

    public int Total { get => _total; set => SetProperty(ref _total, value); }
    private int _total;

    public int Pending { get => _pending; set => SetProperty(ref _pending, value); }
    private int _pending;

    public int Processing { get => _processing; set => SetProperty(ref _processing, value); }
    private int _processing;

    public int Succeeded { get => _succeeded; set => SetProperty(ref _succeeded, value); }
    private int _succeeded;

    public int Failed { get => _failed; set => SetProperty(ref _failed, value); }
    private int _failed;

    public int RateLimited { get => _rateLimited; set => SetProperty(ref _rateLimited, value); }
    private int _rateLimited;

    public string AuthProfileStatus { get => _authProfileStatus; set => SetProperty(ref _authProfileStatus, value); }
    private string _authProfileStatus = string.Empty;

    public string LastExportSummary { get => _lastExportSummary; set => SetProperty(ref _lastExportSummary, value); }
    private string _lastExportSummary = string.Empty;

    [RelayCommand]
    private async Task ImportTxtAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _currentBatch = await _importService.ImportFileAsync(dialog.FileName, ImportDestination.ActiveOnly).ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);
            await ReloadPoolAccountsAsync().ConfigureAwait(true);
            StatusMessage =
                $"Imported {_currentBatch.TotalRecords} records from {Path.GetFileName(dialog.FileName)} into this batch (not added to Pool). Click Start Processing to refresh.";
            NotifyBatchCommands();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExtractMsaRefreshTokensAsync()
    {
        var open = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Select dump / credential file"
        };

        if (open.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(open.FileName);
            var extracted = await Task.Run(() => RefreshTokenLineExtractor.ExtractFromStream(stream)).ConfigureAwait(true);

            if (extracted.Lines.Count == 0)
            {
                MessageBox.Show(
                    $"No MSA refresh tokens found.\nScanned: {extracted.ScannedLines}\nMissing refresh: {extracted.MissingRefresh}\nMalformed: {extracted.Malformed}",
                    "Extract MSA refresh tokens",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var settings = _settingsStore.Load();
            var exportDir = ApplicationPaths.EnsureWritableDirectory(
                string.IsNullOrWhiteSpace(settings.DefaultExportDirectory)
                    || ApplicationPaths.NeedsPortableExportMigration(settings.DefaultExportDirectory)
                    ? ApplicationPaths.DefaultExportDirectory
                    : settings.DefaultExportDirectory,
                ApplicationPaths.DefaultExportDirectory,
                ApplicationPaths.LocalAppDataExports);
            Directory.CreateDirectory(exportDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultName = $"msa_refresh_extracted_{stamp}.txt";
            var save = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Save extracted username:refresh lines",
                InitialDirectory = exportDir,
                FileName = defaultName
            };

            if (save.ShowDialog() != true)
            {
                return;
            }

            var outputLines = new List<string>
            {
                "# Extracted MSA refresh tokens (username:refresh). Keep private."
            };
            outputLines.AddRange(RefreshTokenLineExtractor.ToUsernameTokenLines(extracted.Lines));

            await File.WriteAllLinesAsync(save.FileName, outputLines).ConfigureAwait(true);

            LastExportSummary =
                $"Extracted {extracted.Lines.Count} MSA refresh line(s) from {extracted.ScannedLines} scanned.\n"
                + $"Missing refresh: {extracted.MissingRefresh}, malformed: {extracted.Malformed}\n"
                + $"Saved to:\n{save.FileName}";
            StatusMessage = LastExportSummary;

            var importNow = MessageBox.Show(
                $"{LastExportSummary}\n\nImport into this batch now?",
                "Extract complete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (importNow == MessageBoxResult.Yes)
            {
                _currentBatch = await _importService.ImportFileAsync(save.FileName, ImportDestination.ActiveOnly).ConfigureAwait(true);
                await ReloadAccountsAsync().ConfigureAwait(true);
                await ReloadPoolAccountsAsync().ConfigureAwait(true);
                StatusMessage =
                    $"Imported {_currentBatch.TotalRecords} extracted records into this batch. Click Start Processing to refresh.";
                NotifyBatchCommands();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Extract failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ImportTxtToPoolAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _currentBatch = await _importService.ImportFileAsync(dialog.FileName, ImportDestination.Pool).ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);
            await ReloadPoolAccountsAsync().ConfigureAwait(true);
            StatusMessage =
                $"Imported {_currentBatch.TotalRecords} records from {Path.GetFileName(dialog.FileName)} into Pool. "
                + "Accounts already in the pool stay refreshable on the Batch tab via Start Processing.";
            PoolStatusMessage = StatusMessage;
            NotifyBatchCommands();
            NotifyPoolCommands();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ImportActiveTxtAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _currentBatch = await _importService.ImportFileAsync(dialog.FileName, ImportDestination.ActiveOnly).ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);
            LibraryStatusMessage =
                $"Imported {_currentBatch.TotalRecords} account(s) for active library only — not added to pool. Starting refresh...";
            StatusMessage = LibraryStatusMessage;
            NotifyBatchCommands();
            _ = RunBatchInBackgroundAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportFromLocalts))]
    private Task ImportFromLocaltsAsync()
    {
        PoolStatusMessage = "Fetching owned accounts from Localts...";
        NotifyPoolCommands();
        _ = RunLocaltsImportInBackgroundAsync();
        return Task.CompletedTask;
    }

    private async Task RunLocaltsImportInBackgroundAsync()
    {
        try
        {
            var summary = await _importService.ImportFromLocaltsAsync().ConfigureAwait(true);
            _currentBatch = summary.Batch;
            await ReloadAccountsAsync().ConfigureAwait(true);
            await ReloadPoolAccountsAsync().ConfigureAwait(true);

            PoolStatusMessage =
                $"Localts import for {summary.LocaltsUsername}: {summary.ItemsImported} new, {summary.ItemsUpdated} updated, " +
                $"{summary.OrdersPackaged} packaged orders, {summary.OrdersPending} still packaging. Refreshing pool...";

            NotifyBatchCommands();
            await _poolProcessor.RefreshPoolAsync().ConfigureAwait(true);
            await ReloadPoolAccountsAsync().ConfigureAwait(true);
            await ReloadLibraryAccountsAsync().ConfigureAwait(true);
            PoolStatusMessage =
                $"Localts import complete — {summary.ItemsImported} added, {summary.ItemsUpdated} updated. Pool refresh finished.";
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(ex.Message, "Localts import failed", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(NotifyPoolCommands);
        }
    }

    [RelayCommand]
    private async Task SaveLocaltsApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(LocaltsApiKeyInput))
        {
            MessageBox.Show("Paste your Localts API key first.", "Localts API key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await _localtsService.SaveApiKeyAsync(LocaltsApiKeyInput).ConfigureAwait(true);
            var me = await _localtsService.ValidateApiKeyAsync().ConfigureAwait(true);
            LocaltsApiKeyInput = string.Empty;
            LocaltsConnectionStatus = $"Connected as {me.Username} ({me.Balance} credits).";
            ImportFromLocaltsCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Localts API key", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task TestLocaltsConnectionAsync()
    {
        var result = await _localtsService.CheckConnectivityAsync().ConfigureAwait(true);
        LocaltsConnectionStatus = result.DiagnosticSummary;
    }

    [RelayCommand(CanExecute = nameof(CanRefreshPool))]
    private Task RefreshPoolAsync()
    {
        PoolStatusMessage = "Refreshing all pool accounts...";
        NotifyPoolCommands();
        _ = RunPoolRefreshInBackgroundAsync();
        return Task.CompletedTask;
    }

    private async Task RunPoolRefreshInBackgroundAsync()
    {
        try
        {
            await _poolProcessor.RefreshPoolAsync().ConfigureAwait(true);
            await ReloadPoolAccountsAsync().ConfigureAwait(true);
            await ReloadLibraryAccountsAsync().ConfigureAwait(true);
            PoolStatusMessage = $"Pool refresh complete — {PoolSucceeded} active, {PoolRemovedCount} removed this run.";
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(ex.Message, "Pool refresh failed", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(NotifyPoolCommands);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelPool))]
    private async Task CancelPoolAsync()
    {
        await _poolProcessor.CancelAsync().ConfigureAwait(true);
        PoolStatusMessage = "Pool cancellation requested.";
        await ReloadPoolAccountsAsync().ConfigureAwait(true);
        NotifyPoolCommands();
    }

    [RelayCommand(CanExecute = nameof(CanExportPoolZip))]
    private async Task ExportPoolZipAsync()
    {
        var ready = PoolReadyCount;
        var prompt = ready > 0
            ? $"Export {ready} pool token(s) now — one .txt per username in a ZIP. Continue?"
            : "No active pool tokens yet. Refresh the pool first. Continue anyway?";

        if (MessageBox.Show(prompt, "Export pool ZIP", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _exportService.ExportPoolAccessTokensZipAsync().ConfigureAwait(true);
            var summary = $"Exported {result.ExportedCount} pool token(s) to:\n{result.Path}";
            if (_poolProcessor.IsRunning)
            {
                summary += "\n\nPool refresh still running — export again later for more tokens.";
            }

            LastExportSummary = summary;
            PoolStatusMessage = summary;
            MessageBox.Show(summary, "Pool export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Pool ZIP export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportPoolRefreshTokens))]
    private async Task ExportPoolRefreshTokensAsync()
    {
        if (MessageBox.Show(
                $"Export Microsoft refresh tokens for {PoolTotal} pool account(s)? These are long-lived secrets.",
                "Sensitive export",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var path = await _exportService.ExportPoolRefreshTokensAsync().ConfigureAwait(true);
            var summary = $"Pool refresh tokens exported to:\n{path}";
            LastExportSummary = summary;
            PoolStatusMessage = summary;
            MessageBox.Show(summary, "Pool export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Pool refresh token export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ReloadPoolListAsync()
    {
        await ReloadPoolAccountsAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStartProcessing))]
    private Task StartProcessingAsync()
    {
        if (_currentBatch == null)
        {
            return Task.CompletedTask;
        }

        StatusMessage = "Batch running — open Accounts tab anytime to export tokens already ready.";
        NotifyBatchCommands();
        _ = RunBatchInBackgroundAsync();
        return Task.CompletedTask;
    }

    private async Task RunBatchInBackgroundAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        try
        {
            await _batchProcessor.StartAsync(_currentBatch.Id).ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);
            await ExportResultsAsync(showMessage: false).ConfigureAwait(true);
            StatusMessage = "Batch complete.";
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(ex.Message, "Processing failed", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(NotifyBatchCommands);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelProcessing))]
    private async Task CancelProcessingAsync()
    {
        await _batchProcessor.CancelAsync().ConfigureAwait(true);
        StatusMessage = "Cancellation requested.";
        await ReloadAccountsAsync().ConfigureAwait(true);
        NotifyBatchCommands();
    }

    [RelayCommand(CanExecute = nameof(CanRetryFailed))]
    private async Task RetryFailedAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        try
        {
            StatusMessage = "Retrying failed accounts...";
            NotifyBatchCommands();
            await _batchProcessor.RetryFailedAsync(_currentBatch.Id).ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);
            await ExportResultsAsync(showMessage: false).ConfigureAwait(true);
            StatusMessage = "Retried eligible failures.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Retry failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            NotifyBatchCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshBatchAccount))]
    private Task RefreshBatchAccountAsync(AccountRowViewModel? row)
    {
        row ??= SelectedAccount;
        if (row == null || _currentBatch == null)
        {
            return Task.CompletedTask;
        }

        var name = row.AuthenticatedMinecraftUsername ?? row.ProvidedUsername ?? $"line {row.SourceLine}";
        StatusMessage = $"Refreshing {name}...";
        NotifyBatchCommands();
        _ = RefreshSingleAccountInBackgroundAsync(row);
        return Task.CompletedTask;
    }

    private async Task RefreshSingleAccountInBackgroundAsync(AccountRowViewModel row)
    {
        try
        {
            await _batchProcessor.RefreshAccountAsync(_currentBatch!.Id, row.Id).ConfigureAwait(true);
            await ReloadAccountsAsync().ConfigureAwait(true);
            StatusMessage = $"Refreshed {row.AuthenticatedMinecraftUsername ?? row.ProvidedUsername}.";
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(ex.Message, "Refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(NotifyBatchCommands);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportReadyTokens))]
    private async Task ExportAccessTokensZipAsync()
    {
        var ready = LibraryReadyCount;
        var prompt = ready > 0
            ? $"Export {ready} ready token(s) now — one .txt per username in a ZIP. The batch can keep running. Continue?"
            : "Creates access_tokens.zip — one .txt per username containing only the Minecraft access token. Continue?";

        if (MessageBox.Show(prompt, "Mass extract", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = await _exportService.ExportLibraryAccessTokensZipAsync().ConfigureAwait(true);
            var summary = $"Exported {result.ExportedCount} token(s) to:\n{result.Path}";
            if (_batchProcessor.IsRunning)
            {
                summary += "\n\nBatch is still running — export again later for more tokens.";
            }

            LastExportSummary = summary;
            LibraryStatusMessage = summary;
            MessageBox.Show(summary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ZIP export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshLibraryAsync()
    {
        await ReloadLibraryAccountsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CopyLibraryUsername(AccountLibraryItemViewModel? item)
    {
        item ??= SelectedLibraryAccount;
        if (item == null)
        {
            LibraryStatusMessage = "Select an account first.";
            return;
        }

        if (!TrySetClipboard(item.Username))
        {
            return;
        }

        LibraryStatusMessage = $"Copied username {item.Username}.";
    }

    [RelayCommand]
    private void CopyLibraryUuid(AccountLibraryItemViewModel? item)
    {
        item ??= SelectedLibraryAccount;
        if (item == null)
        {
            LibraryStatusMessage = "Select an account first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Uuid))
        {
            LibraryStatusMessage = $"{item.Username} — no UUID stored.";
            return;
        }

        if (!TrySetClipboard(item.Uuid))
        {
            return;
        }

        LibraryStatusMessage = $"Copied UUID for {item.Username}.";
    }

    [RelayCommand]
    private async Task CopyLibraryAccessTokenAsync(AccountLibraryItemViewModel? item)
    {
        item ??= SelectedLibraryAccount;
        if (item == null)
        {
            LibraryStatusMessage = "Select an account first.";
            return;
        }

        var token = await ReadAccessTokenAsync(item.Record).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(token))
        {
            LibraryStatusMessage = $"{item.Username} — no stored access token. Re-run batch on latest build.";
            return;
        }

        if (!TrySetClipboard(token))
        {
            return;
        }

        LibraryStatusMessage =
            $"Copied Minecraft access token for {item.Username}. Paste in artuurssclient Accounts → Session (not Refresh Token).";
    }

    [RelayCommand]
    private async Task CopyLibraryRefreshTokenAsync(AccountLibraryItemViewModel? item)
    {
        item ??= SelectedLibraryAccount;
        if (item == null)
        {
            LibraryStatusMessage = "Select an account first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Record.CredentialReference))
        {
            LibraryStatusMessage = $"{item.Username} — no refresh token stored.";
            return;
        }

        var refresh = await _credentialStore.RetrieveAsync(item.Record.CredentialReference).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(refresh))
        {
            LibraryStatusMessage = $"{item.Username} — refresh token missing from secure store.";
            return;
        }

        if (!TrySetClipboard(refresh))
        {
            return;
        }

        LibraryStatusMessage =
            $"Copied OAuth refresh token for {item.Username}. Paste in artuurssclient Accounts → Refresh Token (or Microsoft).";
    }

    private bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (Exception ex)
        {
            LibraryStatusMessage = $"Clipboard failed: {ex.Message}";
            MessageBox.Show(ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task ExportUsernamesAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        try
        {
            var path = await _exportService.ExportSuccessfulUsernamesAsync(_currentBatch.Id).ConfigureAwait(true);
            var count = (await File.ReadAllLinesAsync(path).ConfigureAwait(true))
                .Count(line => !string.IsNullOrWhiteSpace(line));
            if (count == 0)
            {
                MessageBox.Show(
                    "No usernames in the current batch (Provided Name / MC Username are empty).",
                    "Nothing to export",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            LastExportSummary = $"Exported {count} username(s) to:\n{path}";
            MessageBox.Show(LastExportSummary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportLibraryUsernames))]
    private async Task ExportLibraryUsernamesAsync()
    {
        try
        {
            var path = await _exportService.ExportLibraryUsernamesAsync().ConfigureAwait(true);
            LastExportSummary = $"Active library usernames exported to:\n{path}";
            LibraryStatusMessage = LastExportSummary;
            MessageBox.Show(LastExportSummary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportPoolUsernames))]
    private async Task ExportPoolUsernamesAsync()
    {
        try
        {
            var path = await _exportService.ExportPoolUsernamesAsync().ConfigureAwait(true);
            LastExportSummary = $"Pool usernames exported to:\n{path}";
            PoolStatusMessage = LastExportSummary;
            MessageBox.Show(LastExportSummary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task ExportAccessTokensAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        if (MessageBox.Show(
                "This writes Minecraft access tokens (JWT) to disk. They are session secrets (~24h). Continue?",
                "Sensitive export",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var path = await _exportService.ExportSuccessfulMinecraftAccessTokensAsync(_currentBatch.Id).ConfigureAwait(true);
            LastExportSummary = $"Minecraft access tokens exported to:\n{path}\n(and .json alongside it)";
            MessageBox.Show(LastExportSummary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task ExportRefreshTokensAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        if (MessageBox.Show(
                "This writes Microsoft refresh tokens to disk. Continue?",
                "Sensitive export",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var path = await _exportService.ExportSuccessfulRefreshTokensAsync(_currentBatch.Id).ConfigureAwait(true);
            LastExportSummary = $"Refresh tokens exported to:\n{path}";
            MessageBox.Show(LastExportSummary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBatch))]
    private async Task ExportFailedRecordsAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        var result = MessageBox.Show(
            "This export contains sensitive credential lines. Continue?",
            "Sensitive export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var path = await _exportService.ExportFailedOriginalRecordsAsync(_currentBatch.Id).ConfigureAwait(true);
        LastExportSummary = $"Failed records exported to:\n{path}";
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        var ms = await _networkDiagnostics.TestMicrosoftAsync().ConfigureAwait(true);
        var mc = await _networkDiagnostics.TestMinecraftAsync().ConfigureAwait(true);
        var lt = await _networkDiagnostics.TestLocaltsAsync().ConfigureAwait(true);

        MessageBox.Show(
            $"Microsoft: {ms.Summary}\n\nMinecraft: {mc.Summary}\n\nLocalts: {lt.Summary}",
            "Network diagnostics",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public async Task InitializeAsync()
    {
        var profile = _profileStore.Load();
        AuthProfileStatus = profile.IsVerified && profile.Microsoft.IsConfigured
            ? "Authentication profile verified (Localts live.com refresh)."
            : "BLOCKED: Complete Phase 0 and configure authentication_profile.json.";

        var batch = await _batchRepository.GetLatestForStartupAsync().ConfigureAwait(true);
        if (batch != null)
        {
            _currentBatch = batch;
            await ReloadAccountsAsync().ConfigureAwait(true);
            StatusMessage = batch.Status is BatchStatus.Running or BatchStatus.PausedRateLimited
                ? $"Resumed batch {batch.Name}."
                : batch.Succeeded + batch.Failed > 0
                    ? $"Loaded {batch.Name} — {batch.Succeeded} succeeded, {batch.Failed} failed."
                    : $"Ready: {batch.Name} ({batch.TotalRecords} accounts — click Start Processing).";
        }

        await ReloadPoolAccountsAsync().ConfigureAwait(true);
        PoolAutoManageEnabled = _settingsStore.Load().PoolAutoManageEnabled;
        StartPoolAutoManageTimer();
        await RefreshLocaltsStatusAsync().ConfigureAwait(true);
    }

    private async Task RefreshLocaltsStatusAsync()
    {
        if (await _localtsService.HasApiKeyAsync().ConfigureAwait(true))
        {
            try
            {
                var me = await _localtsService.ValidateApiKeyAsync().ConfigureAwait(true);
                LocaltsConnectionStatus = $"Connected as {me.Username} ({me.Balance} credits).";
            }
            catch (Exception ex)
            {
                LocaltsConnectionStatus = $"Stored API key failed validation: {ex.Message}";
            }
        }
        else
        {
            LocaltsConnectionStatus = "Add your Localts API key below to import all owned accounts.";
        }

        ImportFromLocaltsCommand.NotifyCanExecuteChanged();
    }

    private void StartPoolAutoManageTimer()
    {
        _poolAutoManageTimer.Stop();
        var settings = _settingsStore.Load();
        if (!settings.PoolAutoManageEnabled)
        {
            return;
        }

        var minutes = Math.Max(5, settings.PoolAutoRefreshIntervalMinutes);
        _poolAutoManageTimer.Interval = TimeSpan.FromMinutes(minutes);
        _poolAutoManageTimer.Start();
    }

    private async Task RunPoolAutoManageAsync()
    {
        if (_poolProcessor.IsRunning || _batchProcessor.IsRunning)
        {
            return;
        }

        var settings = _settingsStore.Load();
        if (!settings.PoolAutoManageEnabled || PoolTotal == 0)
        {
            return;
        }

        try
        {
            await _poolProcessor.AutoManageAsync().ConfigureAwait(true);
            await ReloadPoolAccountsAsync().ConfigureAwait(true);
            await ReloadLibraryAccountsAsync().ConfigureAwait(true);
        }
        catch
        {
            // Auto-manage runs silently in the background.
        }
        finally
        {
            NotifyPoolCommands();
        }
    }

    private bool CanRunBatch() => _currentBatch != null;

    private bool CanStartProcessing() => _currentBatch != null && !_batchProcessor.IsRunning && !_poolProcessor.IsRunning;

    private bool CanRetryFailed() => _currentBatch != null && !_batchProcessor.IsRunning && !_poolProcessor.IsRunning;

    private bool CanCancelProcessing() => _batchProcessor.IsRunning;

    private bool CanImportFromLocalts() =>
        !_poolProcessor.IsRunning && !_batchProcessor.IsRunning;

    private bool CanRefreshPool() => PoolTotal > 0 && !_poolProcessor.IsRunning && !_batchProcessor.IsRunning;

    private bool CanCancelPool() => _poolProcessor.IsRunning;

    private bool CanExportPoolZip() => PoolReadyCount > 0;

    private bool CanExportPoolRefreshTokens() => PoolTotal > 0;

    private bool CanExportPoolUsernames() => PoolTotal > 0;

    private bool CanExportLibraryUsernames() => LibraryReadyCount > 0;

    private bool CanExportReadyTokens() => LibraryReadyCount > 0;

    private bool CanRefreshBatchAccount(AccountRowViewModel? row)
    {
        row ??= SelectedAccount;
        if (row == null || _currentBatch == null || _batchProcessor.IsRunning || _poolProcessor.IsRunning)
        {
            return false;
        }

        return row.Record.ParseStatus == ParseStatus.Parsed
               && !string.IsNullOrWhiteSpace(row.Record.CredentialReference)
               && row.ProcessingState != ProcessingState.Authenticating;
    }

    private void NotifyBatchCommands()
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
        CancelProcessingCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        RefreshBatchAccountCommand.NotifyCanExecuteChanged();
        ExportUsernamesCommand.NotifyCanExecuteChanged();
        ExportAccessTokensCommand.NotifyCanExecuteChanged();
        ExportRefreshTokensCommand.NotifyCanExecuteChanged();
        ExportFailedRecordsCommand.NotifyCanExecuteChanged();
        ExportAccessTokensZipCommand.NotifyCanExecuteChanged();
        RefreshPoolCommand.NotifyCanExecuteChanged();
        StartProcessingCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        RefreshBatchAccountCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPoolCommands()
    {
        RefreshPoolCommand.NotifyCanExecuteChanged();
        CancelPoolCommand.NotifyCanExecuteChanged();
        ExportPoolZipCommand.NotifyCanExecuteChanged();
        ExportPoolRefreshTokensCommand.NotifyCanExecuteChanged();
        ExportPoolUsernamesCommand.NotifyCanExecuteChanged();
        ImportFromLocaltsCommand.NotifyCanExecuteChanged();
        StartProcessingCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        RefreshBatchAccountCommand.NotifyCanExecuteChanged();
    }

    private bool FilterLibraryAccount(object obj)
    {
        if (obj is not AccountLibraryItemViewModel item)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(LibrarySearchText))
        {
            return true;
        }

        var q = LibrarySearchText.Trim();
        return item.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
               || item.Uuid.Contains(q, StringComparison.OrdinalIgnoreCase)
               || item.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ReloadLibraryAccountsAsync()
    {
        LibraryAccounts.Clear();
        var active = await _accountRepository.GetAllActiveWithStoredAccessTokensAsync().ConfigureAwait(true);

        foreach (var record in active)
        {
            var item = new AccountLibraryItemViewModel();
            item.UpdateFrom(record);
            LibraryAccounts.Add(item);
        }

        UpdateLibraryCounts();
        _libraryView.Refresh();
        ExportAccessTokensZipCommand.NotifyCanExecuteChanged();
    }

    private void UpdateLibraryCounts()
    {
        LibraryReadyCount = LibraryAccounts.Count;
        LibraryAccountCount = LibraryReadyCount;
        ExportLibraryUsernamesCommand.NotifyCanExecuteChanged();

        if (LibraryReadyCount == 0)
        {
            LibraryStatusMessage = _batchProcessor.IsRunning
                ? "Waiting for first active token — expired accounts are hidden here."
                : "No active accounts. Batch tab → right-click an account → Refresh account, or Start Processing.";
            return;
        }

        var runningNote = _batchProcessor.IsRunning ? " Batch still running — list updates as new tokens arrive." : string.Empty;
        LibraryStatusMessage = $"{LibraryReadyCount} active account(s) with valid tokens.{runningNote}";
    }

    private static bool IsActiveAccount(AccountRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.MinecraftAccessTokenReference))
        {
            return false;
        }

        return !record.MinecraftAccessTokenExpiresAt.HasValue
               || record.MinecraftAccessTokenExpiresAt.Value > DateTimeOffset.UtcNow;
    }

    private async Task<string?> ReadAccessTokenAsync(AccountRecord record) =>
        string.IsNullOrWhiteSpace(record.MinecraftAccessTokenReference)
            ? null
            : await _credentialStore.RetrieveAsync(record.MinecraftAccessTokenReference).ConfigureAwait(true);

    private async Task ExportResultsAsync(bool showMessage)
    {
        if (_currentBatch == null)
        {
            return;
        }

        var usernamesPath = await _exportService.ExportSuccessfulUsernamesAsync(_currentBatch.Id).ConfigureAwait(true);
        var errorsPath = await _exportService.ExportErrorsAsync(_currentBatch.Id).ConfigureAwait(true);
        await _exportService.ExportDetailedResultsAsync(_currentBatch.Id).ConfigureAwait(true);
        var accessPath = await _exportService.ExportSuccessfulMinecraftAccessTokensAsync(_currentBatch.Id).ConfigureAwait(true);
        try
        {
            var result = await _exportService.ExportAccessTokensZipAsync(_currentBatch.Id).ConfigureAwait(true);
            LastExportSummary = $"Batch ZIP: {result.ExportedCount} token(s) at {result.Path}";
        }
        catch
        {
            // ZIP may fail if no tokens stored yet; combined export still useful.
        }

        LastExportSummary = $"Usernames exported:\n{usernamesPath}\n\nFailure report:\n{errorsPath}\n\nAccess tokens:\n{accessPath}";

        if (showMessage)
        {
            MessageBox.Show(LastExportSummary, "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnBatchProgressChanged(object? sender, BatchProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _currentBatch = e.Batch;
            Total = e.Batch.TotalRecords;
            Pending = e.Batch.Pending;
            Processing = Accounts.Count(a => a.ProcessingState == ProcessingState.Authenticating);
            Succeeded = e.Batch.Succeeded;
            Failed = e.Batch.Failed;
            RateLimited = e.Batch.Status == BatchStatus.PausedRateLimited ? 1 : 0;

            if (e.LastUpdatedAccount != null)
            {
                var existing = Accounts.FirstOrDefault(a => a.Id == e.LastUpdatedAccount.Id);
                if (existing != null)
                {
                    existing.UpdateFrom(e.LastUpdatedAccount);
                }
                else
                {
                    Accounts.Add(new AccountRowViewModel(e.LastUpdatedAccount));
                }

                if (SelectedAccount?.Id == e.LastUpdatedAccount.Id)
                {
                    NotifySelectedExpiry();
                }

                if (e.LastUpdatedAccount.ProcessingState == ProcessingState.Succeeded &&
                    IsActiveAccount(e.LastUpdatedAccount))
                {
                    var libItem = LibraryAccounts.FirstOrDefault(a => a.AccountId == e.LastUpdatedAccount.Id);
                    if (libItem != null)
                    {
                        libItem.UpdateFrom(e.LastUpdatedAccount);
                    }
                    else
                    {
                        var item = new AccountLibraryItemViewModel();
                        item.UpdateFrom(e.LastUpdatedAccount);
                        LibraryAccounts.Add(item);
                    }

                    UpdateLibraryCounts();
                    ExportAccessTokensZipCommand.NotifyCanExecuteChanged();
                }
                else if (e.LastUpdatedAccount.ProcessingState == ProcessingState.Succeeded)
                {
                    var libItem = LibraryAccounts.FirstOrDefault(a => a.AccountId == e.LastUpdatedAccount.Id);
                    if (libItem != null)
                    {
                        LibraryAccounts.Remove(libItem);
                        UpdateLibraryCounts();
                        ExportAccessTokensZipCommand.NotifyCanExecuteChanged();
                    }
                }
            }

            NotifyBatchCommands();
        });
    }

    private void OnPoolProgressChanged(object? sender, PoolProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PoolTotal = e.Total;
            PoolPending = e.Pending;
            PoolSucceeded = e.Succeeded;
            PoolFailed = e.Failed;
            PoolRemovedCount = e.Removed;

            if (e.AccountRemoved)
            {
                var removed = PoolAccounts.FirstOrDefault(a => a.Id == e.LastUpdatedAccount?.Id);
                if (removed != null)
                {
                    PoolAccounts.Remove(removed);
                }
            }
            else if (e.LastUpdatedAccount != null)
            {
                var existing = PoolAccounts.FirstOrDefault(a => a.Id == e.LastUpdatedAccount.Id);
                if (existing != null)
                {
                    existing.UpdateFrom(e.LastUpdatedAccount);
                }
                else
                {
                    PoolAccounts.Add(new AccountRowViewModel(e.LastUpdatedAccount));
                }

                if (e.LastUpdatedAccount.ProcessingState == ProcessingState.Succeeded &&
                    IsActiveAccount(e.LastUpdatedAccount))
                {
                    var libItem = LibraryAccounts.FirstOrDefault(a => a.AccountId == e.LastUpdatedAccount.Id);
                    if (libItem != null)
                    {
                        libItem.UpdateFrom(e.LastUpdatedAccount);
                    }
                    else
                    {
                        var item = new AccountLibraryItemViewModel();
                        item.UpdateFrom(e.LastUpdatedAccount);
                        LibraryAccounts.Add(item);
                    }

                    UpdateLibraryCounts();
                }
            }

            UpdatePoolCounts();
            NotifyPoolCommands();
        });
    }

    private async Task ReloadPoolAccountsAsync()
    {
        var selectedId = SelectedPoolAccount?.Id;
        var accounts = await _accountRepository.GetPoolAccountsAsync().ConfigureAwait(true);
        PoolAccounts.Clear();
        foreach (var account in accounts)
        {
            PoolAccounts.Add(new AccountRowViewModel(account));
        }

        if (selectedId.HasValue)
        {
            SelectedPoolAccount = PoolAccounts.FirstOrDefault(a => a.Id == selectedId.Value);
        }

        UpdatePoolCounts();
        NotifyPoolCommands();
    }

    private void UpdatePoolCounts()
    {
        PoolTotal = PoolAccounts.Count;
        PoolPending = PoolAccounts.Count(a =>
            a.ProcessingState is ProcessingState.Queued or ProcessingState.Backoff or ProcessingState.Authenticating or ProcessingState.Pending);
        PoolSucceeded = PoolAccounts.Count(a => a.ProcessingState == ProcessingState.Succeeded);
        PoolFailed = PoolAccounts.Count(a =>
            a.ProcessingState is ProcessingState.Failed or ProcessingState.ReauthenticationRequired or ProcessingState.Malformed);
        PoolReadyCount = PoolAccounts.Count(a =>
            a.ProcessingState == ProcessingState.Succeeded &&
            !string.IsNullOrWhiteSpace(a.Record.MinecraftAccessTokenReference) &&
            (!a.MinecraftAccessTokenExpiresAt.HasValue || a.MinecraftAccessTokenExpiresAt.Value > DateTimeOffset.UtcNow));

        if (PoolTotal == 0)
        {
            PoolStatusMessage = "No pool accounts yet — import a TXT file on the Batch tab to add accounts.";
            return;
        }

        if (_poolProcessor.IsRunning)
        {
            PoolStatusMessage = $"Pool managing {PoolTotal} account(s) — {PoolReadyCount} ready, {PoolPending} in progress.";
            return;
        }

        PoolStatusMessage = $"{PoolTotal} account(s) in pool — {PoolReadyCount} with valid tokens. Failed accounts (except rate limits) auto-remove.";
    }

    private async Task ReloadAccountsAsync()
    {
        if (_currentBatch == null)
        {
            return;
        }

        var selectedId = SelectedAccount?.Id;
        var accounts = await _accountRepository.GetByBatchIdAsync(_currentBatch.Id).ConfigureAwait(true);
        Accounts.Clear();
        foreach (var account in accounts)
        {
            Accounts.Add(new AccountRowViewModel(account));
        }

        if (selectedId.HasValue)
        {
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId.Value);
        }

        Total = _currentBatch.TotalRecords;
        Pending = _currentBatch.Pending;
        Processing = accounts.Count(a => a.ProcessingState == ProcessingState.Authenticating);
        Succeeded = _currentBatch.Succeeded;
        Failed = _currentBatch.Failed;
        RateLimited = _currentBatch.Status == BatchStatus.PausedRateLimited ? 1 : 0;
        NotifyBatchCommands();
        RefreshCountdowns();
        await ReloadLibraryAccountsAsync().ConfigureAwait(true);
        await ReloadPoolAccountsAsync().ConfigureAwait(true);
    }

    private void RefreshCountdowns()
    {
        foreach (var row in Accounts)
        {
            row.RefreshCountdown();
        }

        foreach (var row in PoolAccounts)
        {
            row.RefreshCountdown();
        }

        foreach (var item in LibraryAccounts.ToList())
        {
            item.RefreshExpiry();
            if (item.IsExpired)
            {
                LibraryAccounts.Remove(item);
            }
        }

        if (LibraryAccounts.Count != LibraryReadyCount)
        {
            UpdateLibraryCounts();
            ExportAccessTokensZipCommand.NotifyCanExecuteChanged();
            ExportPoolZipCommand.NotifyCanExecuteChanged();
        }

        NotifySelectedExpiry();
    }

    private void NotifySelectedExpiry()
    {
        OnPropertyChanged(nameof(SelectedMinecraftTokenRemaining));
        OnPropertyChanged(nameof(SelectedMinecraftTokenStatus));
        OnPropertyChanged(nameof(SelectedMicrosoftTokenRemaining));
        OnPropertyChanged(nameof(SelectedRefreshUpdatedText));
    }
}
