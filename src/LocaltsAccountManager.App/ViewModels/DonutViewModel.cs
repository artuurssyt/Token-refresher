using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using DonutComparer.Core.Services;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;
using Microsoft.Win32;

namespace LocaltsAccountManager.App.ViewModels;

public partial class DonutViewModel : ObservableObject
{
    private readonly IAccountRepository _accountRepository;
    private readonly IBatchRepository _batchRepository;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly SettingsService _settingsService = new();
    private CancellationTokenSource? _scanCts;
    private PlayerScanService? _scanService;

    public DonutViewModel(
        IAccountRepository accountRepository,
        IBatchRepository batchRepository,
        ISecureCredentialStore credentialStore)
    {
        _accountRepository = accountRepository;
        _batchRepository = batchRepository;
        _credentialStore = credentialStore;
        ReloadSettingsSummary();
    }

    public ObservableCollection<PlayerScanResult> Results { get; } = new();

    [ObservableProperty]
    private string _usernameText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Paste usernames, load from Batch/Pool/Accounts, then Start scan. Join DonutSMP with PlayerCheckerBridge enabled.";

    [ObservableProperty]
    private string _bridgeStatusText = "Bridge idle.";

    [ObservableProperty]
    private string _settingsSummary = string.Empty;

    [ObservableProperty]
    private string _hypixelApiKeyInput = string.Empty;

    [ObservableProperty]
    private string _donutApiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _scanCompleted;

    [ObservableProperty]
    private int _scanTotal;

    [ObservableProperty]
    private PlayerScanResult? _selectedResult;

    [RelayCommand]
    private async Task LoadFromBatchAsync()
    {
        var batch = await _batchRepository.GetLatestForStartupAsync().ConfigureAwait(true);
        if (batch is null)
        {
            StatusMessage = "No batch found. Import a file on the Batch tab first.";
            return;
        }

        var accounts = await _accountRepository.GetByBatchIdAsync(batch.Id).ConfigureAwait(true);
        var names = accounts
            .Select(a => a.AuthenticatedMinecraftUsername ?? a.ProvidedUsername)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppendUsernames(names!);
        StatusMessage = names.Count == 0
            ? $"Batch has {accounts.Count} account(s) but no usernames yet (refresh them on the Batch tab first)."
            : $"Loaded {names.Count} username(s) from Batch ({batch.Name}).";
    }

    [RelayCommand]
    private async Task LoadFromPoolAsync()
    {
        var pool = await _accountRepository.GetPoolAccountsAsync().ConfigureAwait(true);
        var names = pool
            .Select(a => a.AuthenticatedMinecraftUsername ?? a.ProvidedUsername)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppendUsernames(names!);
        StatusMessage = $"Loaded {names.Count} username(s) from Pool.";
    }

    [RelayCommand]
    private async Task LoadFromAccountsAsync()
    {
        var active = await _accountRepository.GetAllActiveWithStoredAccessTokensAsync().ConfigureAwait(true);
        var names = active
            .Select(a => a.AuthenticatedMinecraftUsername ?? a.ProvidedUsername)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppendUsernames(names!);
        StatusMessage = $"Loaded {names.Count} username(s) from active Accounts.";
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private Task StartScanAsync()
    {
        var parsed = UsernameParser.Parse(UsernameText);
        if (parsed.Usernames.Count == 0)
        {
            StatusMessage = "Add at least one username first.";
            return Task.CompletedTask;
        }

        IsScanning = true;
        ScanCompleted = 0;
        ScanTotal = parsed.Usernames.Count;
        Results.Clear();
        NotifyScanCommands();
        _ = RunScanInBackgroundAsync(parsed.Usernames);
        return Task.CompletedTask;
    }

    private async Task RunScanInBackgroundAsync(IReadOnlyList<string> usernames)
    {
        _scanCts = new CancellationTokenSource();
        var settings = _settingsService.Load();
        _scanService = new PlayerScanService(settings);
        var progress = new Progress<ScanProgress>(p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ScanCompleted = p.Completed;
                ScanTotal = p.Total;
                StatusMessage = p.Message;
                if (p.BridgeStatus != null)
                {
                    BridgeStatusText =
                        $"Bridge: {(p.BridgeStatus.ClientConnected ? "client connected" : "waiting for client")} — "
                        + $"queue {p.BridgeStatus.QueueDepth}, current {p.BridgeStatus.CurrentUsername ?? "—"}";
                }

                if (p.Result != null)
                {
                    var existing = Results.FirstOrDefault(r =>
                        string.Equals(r.Username, p.Result.Username, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        var idx = Results.IndexOf(existing);
                        Results[idx] = p.Result;
                    }
                    else
                    {
                        Results.Add(p.Result);
                    }
                }
            });
        });

        try
        {
            var results = await _scanService.ScanAsync(usernames, progress, _scanCts.Token).ConfigureAwait(true);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Results.Clear();
                foreach (var row in results)
                {
                    Results.Add(row);
                }

                StatusMessage = $"Scan finished — {results.Count(r => r.Donut is not null)} with Donut stats, "
                               + $"{results.Count(r => r.HasError)} with errors.";
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(ex.Message, "Donut scan failed", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            _scanService?.Dispose();
            _scanService = null;
            _scanCts?.Dispose();
            _scanCts = null;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsScanning = false;
                NotifyScanCommands();
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopScan))]
    private void StopScan()
    {
        _scanCts?.Cancel();
        StatusMessage = "Stopping scan...";
    }

    [RelayCommand]
    private void SaveApiKeys()
    {
        var settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(HypixelApiKeyInput))
        {
            settings.HypixelApiKey = HypixelApiKeyInput.Trim();
            HypixelApiKeyInput = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(DonutApiKeyInput))
        {
            settings.DonutApiKey = DonutApiKeyInput.Trim();
            DonutApiKeyInput = string.Empty;
        }

        _settingsService.Save(settings);
        ReloadSettingsSummary();
        StatusMessage = "Donut/Hypixel API keys saved (DPAPI). Join DonutSMP → enable PlayerCheckerBridge → Start scan.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Results.Count == 0)
        {
            StatusMessage = "No scan results to export.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"donut_scan_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ExportService.ExportCsvAsync(dialog.FileName, Results.ToList(), CancellationToken.None).ConfigureAwait(true);
        StatusMessage = $"Exported CSV to {dialog.FileName}";
    }

    [RelayCommand]
    private void CopySelectedUsername()
    {
        if (SelectedResult == null || string.IsNullOrWhiteSpace(SelectedResult.Username))
        {
            StatusMessage = "Select a result row first.";
            return;
        }

        Clipboard.SetDataObject(SelectedResult.Username, copy: true);
        StatusMessage = $"Copied {SelectedResult.Username}.";
    }

    [RelayCommand]
    private async Task CopySelectedAccessTokenAsync()
    {
        if (SelectedResult == null)
        {
            StatusMessage = "Select a result row first.";
            return;
        }

        var record = await FindLibraryAccountAsync(SelectedResult).ConfigureAwait(true);
        if (record == null || string.IsNullOrWhiteSpace(record.MinecraftAccessTokenReference))
        {
            StatusMessage = $"{SelectedResult.Username} — no matching stored access token in Pool/Accounts.";
            return;
        }

        var token = await _credentialStore.RetrieveAsync(record.MinecraftAccessTokenReference).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusMessage = $"{SelectedResult.Username} — access token missing from secure store.";
            return;
        }

        Clipboard.SetDataObject(token, copy: true);
        StatusMessage =
            $"Copied Minecraft access token for {SelectedResult.Username}. "
            + "Paste in artuurssclient Accounts → Session (not Refresh Token).";
    }

    [RelayCommand]
    private async Task ExportMeteorSessionsAsync()
    {
        var accounts = await CollectReadyAccountsAsync().ConfigureAwait(true);
        if (accounts.Count == 0)
        {
            MessageBox.Show(
                "No ready accounts with stored access tokens. Refresh Pool/Accounts first.",
                "Export sessions",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var lines = new List<string>();
        foreach (var account in accounts)
        {
            var token = await _credentialStore.RetrieveAsync(account.MinecraftAccessTokenReference!).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            // Meteor SessionAccount accepts the Minecraft JWT access token as the session value.
            lines.Add(token.Trim());
        }

        if (lines.Count == 0)
        {
            MessageBox.Show("Could not read any access tokens from secure storage.", "Export sessions",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"meteor_sessions_{lines.Count}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await File.WriteAllLinesAsync(dialog.FileName, lines).ConfigureAwait(true);
        Clipboard.SetDataObject(string.Join(Environment.NewLine, lines), copy: true);
        StatusMessage =
            $"Exported {lines.Count} Minecraft access JWT(s) to {dialog.FileName} (also copied). "
            + "In artuurssclient: Accounts → Session → paste one JWT and Add. "
            + "Do NOT use Refresh Token for these — that field needs the OAuth refresh token (M.C…), not the JWT.";
        MessageBox.Show(StatusMessage, "Export sessions", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private async Task ExportSessionLinesDetailedAsync()
    {
        var accounts = await CollectReadyAccountsAsync().ConfigureAwait(true);
        if (accounts.Count == 0)
        {
            MessageBox.Show("No ready accounts with stored access tokens.", "Export sessions",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = new List<string>
        {
            "# username:uuid:accessToken — for manual login; Meteor Session tab uses accessToken only"
        };
        foreach (var account in accounts)
        {
            var token = await _credentialStore.RetrieveAsync(account.MinecraftAccessTokenReference!).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var name = account.AuthenticatedMinecraftUsername ?? account.ProvidedUsername ?? "unknown";
            var uuid = account.AuthenticatedMinecraftUuid ?? "";
            lines.Add($"{name}:{uuid}:{token.Trim()}");
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"session_lines_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await File.WriteAllLinesAsync(dialog.FileName, lines).ConfigureAwait(true);
        StatusMessage = $"Wrote {lines.Count - 1} detailed session line(s) to {dialog.FileName}";
    }

    private async Task<IReadOnlyList<AccountRecord>> CollectReadyAccountsAsync()
    {
        var pool = await _accountRepository.GetPoolActiveWithStoredAccessTokensAsync().ConfigureAwait(true);
        var library = await _accountRepository.GetAllActiveWithStoredAccessTokensAsync().ConfigureAwait(true);
        var map = new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in pool.Concat(library))
        {
            var key = !string.IsNullOrWhiteSpace(account.AuthenticatedMinecraftUuid)
                ? account.AuthenticatedMinecraftUuid!
                : account.AuthenticatedMinecraftUsername ?? account.Id.ToString();
            map.TryAdd(key, account);
        }

        return map.Values.ToList();
    }

    private async Task<AccountRecord?> FindLibraryAccountAsync(PlayerScanResult result)
    {
        var all = await CollectReadyAccountsAsync().ConfigureAwait(true);
        return all.FirstOrDefault(a =>
            (!string.IsNullOrWhiteSpace(result.Uuid) &&
             string.Equals(a.AuthenticatedMinecraftUuid, result.Uuid, StringComparison.OrdinalIgnoreCase))
            || string.Equals(a.AuthenticatedMinecraftUsername, result.Username, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.ProvidedUsername, result.Username, StringComparison.OrdinalIgnoreCase));
    }

    private void AppendUsernames(IEnumerable<string> names)
    {
        var existing = new HashSet<string>(
            UsernameParser.Parse(UsernameText).Usernames,
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (existing.Add(name))
            {
                if (UsernameText.Length > 0 && !UsernameText.EndsWith('\n'))
                {
                    UsernameText += Environment.NewLine;
                }

                UsernameText += name + Environment.NewLine;
            }
        }
    }

    private void ReloadSettingsSummary()
    {
        var settings = _settingsService.Load();
        SettingsSummary =
            $"Bridge port {settings.DonutBridgePort}, command '{settings.DonutBridgeCommandTemplate}', "
            + (settings.DonutBridgeOnly ? "bridge-only, " : string.Empty)
            + $"Hypixel key {(string.IsNullOrWhiteSpace(settings.HypixelApiKey) ? "no" : "yes")}, "
            + $"Donut API key {(string.IsNullOrWhiteSpace(settings.DonutApiKey) ? "no" : "yes")}.";
    }

    private bool CanStartScan() => !IsScanning && !string.IsNullOrWhiteSpace(UsernameText);

    private bool CanStopScan() => IsScanning;

    private void NotifyScanCommands()
    {
        StartScanCommand.NotifyCanExecuteChanged();
        StopScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnUsernameTextChanged(string value) => StartScanCommand.NotifyCanExecuteChanged();
}
