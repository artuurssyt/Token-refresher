

using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;
using DonutHypixelPlayerComparer.Services;
namespace DonutHypixelPlayerComparer.UI;

public sealed partial class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _hypixelKey = SecretBox();
    private readonly TextBox _donutKey = SecretBox();
    private readonly NumericUpDown _concurrency = Number(1, 16);
    private readonly NumericUpDown _hypixelRate = Number(1, 300);
    private readonly NumericUpDown _donutRate = Number(1, 250);
    private readonly NumericUpDown _timeout = Number(5, 180);
    private readonly NumericUpDown _retries = Number(0, 8);
    private readonly NumericUpDown _playerCache = Number(0, 1440);
    private readonly NumericUpDown _marketCache = Number(1, 1440);
    private readonly ComboBox _priceMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _hypixelPages = Number(0, 200);
    private readonly NumericUpDown _donutPages = Number(0, 200);
    private readonly NumericUpDown _shardValue = Number(0, 1_000_000_000, 2);
    private readonly CheckBox _museum = new() { Text = "Include museum assets", AutoSize = true };
    private readonly CheckBox _playerAuctions = new() { Text = "Include active player auctions", AutoSize = true };
    private readonly CheckBox _donutBridgeEnabled = new() { Text = "Use Donut client bridge (artuurssclient in-game lookup)", AutoSize = true };
    private readonly NumericUpDown _donutBridgePort = Number(1024, 65535);
    private readonly NumericUpDown _donutBridgeTimeout = Number(15, 300);
    private readonly TextBox _donutBridgeCommand = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _donutBridgeDelay = Number(250, 10_000);
    private readonly TextBox _proxyUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _proxyUser = new() { Dock = DockStyle.Fill };
    private readonly TextBox _proxyPassword = SecretBox();

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        BuildLayout();
        LoadValues();
    }

    private void LoadValues()
    {
        _hypixelKey.Text = _settings.HypixelApiKey;
        _donutKey.Text = _settings.DonutApiKey;
        SetNumeric(_concurrency, _settings.Concurrency);
        SetNumeric(_hypixelRate, _settings.HypixelRequestsPerMinute);
        SetNumeric(_donutRate, _settings.DonutRequestsPerMinute);
        SetNumeric(_timeout, _settings.RequestTimeoutSeconds);
        SetNumeric(_retries, _settings.RetryCount);
        SetNumeric(_playerCache, _settings.PlayerCacheMinutes);
        SetNumeric(_marketCache, _settings.MarketCacheMinutes);
        _priceMode.Items.AddRange(Enum.GetNames<BazaarPriceMode>());
        _priceMode.SelectedItem = _settings.BazaarPriceMode.ToString();
        SetNumeric(_hypixelPages, _settings.MaxHypixelAuctionPages);
        SetNumeric(_donutPages, _settings.MaxDonutAuctionPages);
        SetNumeric(_shardValue, _settings.DonutShardUnitValue);
        _museum.Checked = _settings.IncludeMuseum;
        _playerAuctions.Checked = _settings.IncludePlayerAuctions;
        _donutBridgeEnabled.Checked = _settings.DonutBridgeEnabled;
        SetNumeric(_donutBridgePort, _settings.DonutBridgePort);
        SetNumeric(_donutBridgeTimeout, _settings.DonutBridgeJobTimeoutSeconds);
        _donutBridgeCommand.Text = _settings.DonutBridgeCommandTemplate;
        SetNumeric(_donutBridgeDelay, _settings.DonutBridgeCommandDelayMs);
        _proxyUrl.Text = _settings.HttpProxyUrl;
        _proxyUser.Text = _settings.HttpProxyUsername;
        _proxyPassword.Text = _settings.HttpProxyPassword;
    }

    private static void SetNumeric(NumericUpDown control, decimal value) =>
        control.Value = Math.Clamp(value, control.Minimum, control.Maximum);

    private void SaveValues()
    {
        _settings.HypixelApiKey = _hypixelKey.Text.Trim();
        _settings.DonutApiKey = _donutKey.Text.Trim();
        _settings.Concurrency = (int)_concurrency.Value;
        _settings.HypixelRequestsPerMinute = (int)_hypixelRate.Value;
        _settings.DonutRequestsPerMinute = (int)_donutRate.Value;
        _settings.RequestTimeoutSeconds = (int)_timeout.Value;
        _settings.RetryCount = (int)_retries.Value;
        _settings.PlayerCacheMinutes = (int)_playerCache.Value;
        _settings.MarketCacheMinutes = (int)_marketCache.Value;
        _settings.BazaarPriceMode = Enum.TryParse<BazaarPriceMode>(_priceMode.Text, out var mode)
            ? mode : BazaarPriceMode.ConservativeSell;
        _settings.MaxHypixelAuctionPages = (int)_hypixelPages.Value;
        _settings.MaxDonutAuctionPages = (int)_donutPages.Value;
        _settings.DonutShardUnitValue = _shardValue.Value;
        _settings.IncludeMuseum = _museum.Checked;
        _settings.IncludePlayerAuctions = _playerAuctions.Checked;
        _settings.DonutBridgeEnabled = _donutBridgeEnabled.Checked;
        _settings.DonutBridgePort = (int)_donutBridgePort.Value;
        _settings.DonutBridgeJobTimeoutSeconds = (int)_donutBridgeTimeout.Value;
        _settings.DonutBridgeCommandTemplate = _donutBridgeCommand.Text.Trim();
        _settings.DonutBridgeCommandDelayMs = (int)_donutBridgeDelay.Value;
        _settings.HttpProxyUrl = _proxyUrl.Text.Trim();
        _settings.HttpProxyUsername = _proxyUser.Text.Trim();
        _settings.HttpProxyPassword = _proxyPassword.Text;
    }

    private static TextBox SecretBox() => new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };

    private static NumericUpDown Number(decimal min, decimal max, int decimals = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        ThousandsSeparator = true,
        Dock = DockStyle.Left,
        Width = 150
    };
}
