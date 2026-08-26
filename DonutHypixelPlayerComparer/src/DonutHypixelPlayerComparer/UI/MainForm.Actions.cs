using System.ComponentModel;




using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using DonutComparer.Core.Services;
namespace DonutHypixelPlayerComparer.UI;

public sealed partial class MainForm
{
    private void SettingsClicked(object? sender, EventArgs e)
    {
        var settings = _settingsService.Load();
        using var dialog = new SettingsForm(settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _settingsService.Save(settings);
            AppendLog("Settings saved. API keys are encrypted for this Windows user.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void ImportClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import Minecraft usernames",
            Filter = "Username files (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var text = await File.ReadAllTextAsync(dialog.FileName);
            _usernames.Text = string.IsNullOrWhiteSpace(_usernames.Text)
                ? text : _usernames.Text.TrimEnd() + Environment.NewLine + text;
            AppendLog($"Imported {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StartScanAsync()
    {
        var parsed = UsernameParser.Parse(_usernames.Text);
        if (parsed.Usernames.Count == 0)
        {
            MessageBox.Show(this, "Enter or import at least one valid Minecraft username.",
                "No usernames", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.HypixelApiKey))
        {
            MessageBox.Show(this,
                "A Hypixel API key is only needed for SkyBlock. Without one the scan still runs, but it reports DonutSMP stats only.",
                "No Hypixel API key", MessageBoxButtons.OK, MessageBoxIcon.Information);
            using var settingsDialog = new SettingsForm(settings);
            if (settingsDialog.ShowDialog(this) == DialogResult.OK)
            {
                _settingsService.Save(settings);
                settings = _settingsService.Load();
            }
        }
        // Only refuse to start when nothing at all could be looked up.
        if (string.IsNullOrWhiteSpace(settings.HypixelApiKey)
            && !settings.DonutBridgeEnabled
            && string.IsNullOrWhiteSpace(settings.DonutApiKey))
        {
            MessageBox.Show(this,
                "No data source is configured. Add a Hypixel API key for SkyBlock and/or enable the Donut client bridge for DonutSMP.",
                "Nothing to scan with", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.HypixelApiKey))
            AppendLog("No Hypixel API key set — running a DonutSMP-only scan.");
        if (settings.DonutBridgeEnabled && string.IsNullOrWhiteSpace(settings.DonutApiKey))
            AppendLog("Donut stats will be fetched via the Minecraft client bridge. Join DonutSMP and enable PlayerCheckerBridge.");
        else if (settings.DonutBridgeEnabled)
            AppendLog("Donut stats will use the official API first and fall back to the client bridge. "
                + "Join DonutSMP with PlayerCheckerBridge enabled so the fallback is available.");
        else if (string.IsNullOrWhiteSpace(settings.DonutApiKey))
            AppendLog("No Donut source configured. Enable the Donut client bridge in Settings (no /api key needed).");

        _results.Clear();
        RefreshSource();
        _grid.Columns.Cast<DataGridViewColumn>().ToList().ForEach(column => column.HeaderCell.SortGlyphDirection = SortOrder.None);
        _progress.Maximum = parsed.Usernames.Count;
        _progress.Value = 0;
        _exportButton.Enabled = false;
        ToggleScanning(true);
        _log.Clear();
        if (parsed.Duplicates.Count > 0) AppendLog($"Skipped {parsed.Duplicates.Count} duplicate username(s).");
        if (parsed.Invalid.Count > 0) AppendLog($"Ignored invalid values: {string.Join(", ", parsed.Invalid.Take(10))}");
        AppendLog($"Starting scan for {parsed.Usernames.Count} unique player(s)…");

        _scanCancellation = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(update =>
        {
            _status.Text = update.Message;
            _progress.Value = Math.Clamp(update.Completed, _progress.Minimum, _progress.Maximum);
            UpdateBridgeStatus(update.BridgeStatus);
            AppendLog(update.Message);
            if (update.Result is not null)
            {
                _results.Add(update.Result);
                RefreshSource();
            }
        });
        try
        {
            using var scanner = new PlayerScanService(settings);
            await scanner.ScanAsync(parsed.Usernames, progress, _scanCancellation.Token);
            _status.Text = $"Complete — {_results.Count} player(s)";
            AppendLog("Scan complete. Double-click any result for the valuation breakdown.");
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"Stopped — {_results.Count} result(s) retained";
            AppendLog("Scan stopped by user. Completed rows were retained.");
        }
        catch (Exception ex)
        {
            _status.Text = "Scan failed";
            AppendLog("Fatal scan error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            ToggleScanning(false);
            _exportButton.Enabled = _results.Count > 0;
            UpdateBridgeStatus(null);
        }
    }

    private async Task TestBridgeAsync()
    {
        var settings = _settingsService.Load();
        if (!settings.DonutBridgeEnabled)
        {
            MessageBox.Show(this, "Enable the Donut client bridge in Settings first.",
                "Bridge disabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var username = UsernameParser.Parse(_usernames.Text).Usernames.FirstOrDefault();
        if (username is null)
        {
            MessageBox.Show(this, "Enter at least one username to test with.",
                "No usernames", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _log.Clear();
        ToggleScanning(true);
        _status.Text = "Testing Donut bridge…";
        _scanCancellation = new CancellationTokenSource();
        DonutBridgeHost? host = null;
        try
        {
            host = new DonutBridgeHost(settings);
            host.Diagnostic += message => BeginInvoke(() =>
            {
                AppendLog(message);
                UpdateBridgeStatus(host?.Status);
            });
            host.Start();
            AppendLog($"Bridge test started. Join DonutSMP, enable PlayerCheckerBridge, and it will run /bal {username} (or your configured template).");
            var stats = await host.WaitForStatsAsync(username, _scanCancellation.Token);
            AppendLog($"Bridge test succeeded for {username}: money {stats?.Money:N0}, shards {stats?.Shards:N0}, "
                      + $"kills {stats?.Kills:N0}, deaths {stats?.Deaths:N0}, playtime {stats?.PlaytimeSeconds / 3600}h.");
            _status.Text = "Bridge test succeeded";
        }
        catch (OperationCanceledException)
        {
            AppendLog("Bridge test stopped.");
            _status.Text = "Bridge test stopped";
        }
        catch (Exception ex)
        {
            AppendLog("Bridge test failed: " + ex.Message);
            AppendLog("Full request log: " + AppPaths.BridgeLogFile);
            _status.Text = "Bridge test failed";
        }
        finally
        {
            host?.Dispose();
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            ToggleScanning(false);
            UpdateBridgeStatus(null);
        }
    }

    private async Task ExportAsync(string format)
    {
        if (_results.Count == 0) return;
        var extension = format == "xlsx" ? "xlsx" : format;
        using var dialog = new SaveFileDialog
        {
            Title = "Export player comparison",
            FileName = $"player-comparison-{DateTime.Now:yyyyMMdd-HHmm}.{extension}",
            Filter = format switch
            {
                "csv" => "CSV files (*.csv)|*.csv",
                "json" => "JSON files (*.json)|*.json",
                _ => "Excel workbooks (*.xlsx)|*.xlsx"
            }
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var snapshot = _results.ToList();
            if (format == "csv") await ExportService.ExportCsvAsync(dialog.FileName, snapshot, CancellationToken.None);
            else if (format == "json") await ExportService.ExportJsonAsync(dialog.FileName, snapshot, CancellationToken.None);
            else await ExportService.ExportExcelAsync(dialog.FileName, snapshot, CancellationToken.None);
            AppendLog($"Exported {snapshot.Count} row(s) to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void GridColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0) return;
        var column = _grid.Columns[e.ColumnIndex];
        if (column.DataPropertyName.Length == 0) return;
        _sortAscending = _sortProperty == column.DataPropertyName ? !_sortAscending : true;
        _sortProperty = column.DataPropertyName;
        foreach (DataGridViewColumn item in _grid.Columns) item.HeaderCell.SortGlyphDirection = SortOrder.None;
        column.HeaderCell.SortGlyphDirection = _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
        RefreshSource();
    }

    private void GridCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not PlayerScanResult result) return;
        using var details = new PlayerDetailsForm(result);
        details.ShowDialog(this);
    }

    private void RefreshSource()
    {
        var property = typeof(PlayerScanResult).GetProperty(_sortProperty);
        IEnumerable<PlayerScanResult> sorted = _results;
        if (property is not null)
            sorted = _sortAscending
                ? _results.OrderBy(row => property.GetValue(row), ObjectComparer.Instance)
                : _results.OrderByDescending(row => property.GetValue(row), ObjectComparer.Instance);
        _source.DataSource = new BindingList<PlayerScanResult>(sorted.ToList());
    }

    private void ToggleScanning(bool scanning)
    {
        _startButton.Enabled = !scanning;
        _testBridgeButton.Enabled = !scanning;
        _settingsButton.Enabled = !scanning;
        _importButton.Enabled = !scanning;
        _stopButton.Enabled = scanning;
        _usernames.ReadOnly = scanning;
    }

    private void AppendLog(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private sealed class ObjectComparer : IComparer<object?>
    {
        public static ObjectComparer Instance { get; } = new();
        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            if (left is IComparable comparable)
            {
                try { return comparable.CompareTo(right); }
                catch (ArgumentException) { }
            }
            return string.Compare(left.ToString(), right.ToString(), StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
