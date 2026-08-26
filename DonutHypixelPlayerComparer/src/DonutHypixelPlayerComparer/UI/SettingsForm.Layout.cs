using System.Drawing;

using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using DonutComparer.Core.Services;
namespace DonutHypixelPlayerComparer.UI;

public sealed partial class SettingsForm
{
    private TabControl? _tabs;
    private Button? _saveButton;
    private Button? _cancelButton;

    private void BuildLayout()
    {
        Text = "API and valuation settings";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 600);
        Size = new Size(780, 690);
        Font = ArtuurssTheme.BodyFont;
        ArtuurssTheme.Apply(this);

        _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 6) };
        _tabs.TabPages.Add(BuildApiTab());
        _tabs.TabPages.Add(BuildBridgeTab());
        _tabs.TabPages.Add(BuildPerformanceTab());
        _tabs.TabPages.Add(BuildValuationTab());
        _tabs.TabPages.Add(BuildNetworkTab());
        ArtuurssTheme.ApplyTabControl(_tabs);

        _saveButton = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        _saveButton.Click += (_, _) => SaveValues();
        ArtuurssTheme.StylePrimaryButton(_saveButton);
        _cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ArtuurssTheme.StyleSecondaryButton(_cancelButton);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            BackColor = ArtuurssTheme.Background2
        };
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_cancelButton);
        Controls.Add(_tabs);
        Controls.Add(buttons);
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
    }

    private TabPage BuildApiTab()
    {
        var page = Page("API keys");
        var table = FormTable();
        AddRow(table, "Hypixel API key", _hypixelKey);
        AddRow(table, "DonutSMP API key", _donutKey);
        var note = Note(
            "Hypixel key is required for SkyBlock only. Leave the DonutSMP API key blank — Donut data comes from the "
            + "client bridge (Settings → Donut bridge). The optional Donut API key is only useful if you want auction "
            + "listing value; it is not required for money/shards/stats. "
            + "Keys are encrypted for this Windows user with Windows Data Protection.");
        AddWideRow(table, note);
        page.Controls.Add(table);
        StyleControls(table);
        return page;
    }

    private TabPage BuildBridgeTab()
    {
        var page = Page("Donut bridge");
        var table = FormTable();
        AddWideRow(table, _donutBridgeEnabled);
        AddRow(table, "Bridge port", _donutBridgePort);
        AddRow(table, "Job timeout (seconds)", _donutBridgeTimeout);
        AddRow(table, "Command template", _donutBridgeCommand);
        AddRow(table, "Command delay (ms)", _donutBridgeDelay);
        AddWideRow(table, Note(
            "This is the normal Donut path: no /api key needed. While scanning, the app listens on 127.0.0.1 and "
            + "queues usernames for PlayerCheckerBridge in artuurssclient (join DonutSMP, enable that Misc module). "
            + "Default command is /bal {username} (chat balance). Switch to /stats {username} only if you want the "
            + "full GUI profile. DonutSMP rules prohibit macros/scripts, so automating chat commands carries ban risk."));
        page.Controls.Add(table);
        StyleControls(table);
        ArtuurssTheme.ApplyCheckBox(_donutBridgeEnabled);
        return page;
    }

    private TabPage BuildPerformanceTab()
    {
        var page = Page("Performance");
        var table = FormTable();
        AddRow(table, "Concurrent players", _concurrency);
        AddRow(table, "Hypixel requests/minute", _hypixelRate);
        AddRow(table, "DonutSMP requests/minute", _donutRate);
        AddRow(table, "Request timeout (seconds)", _timeout);
        AddRow(table, "Retry count", _retries);
        AddRow(table, "Player cache (minutes)", _playerCache);
        AddRow(table, "Market cache (minutes)", _marketCache);
        AddWideRow(table, Note("Retries use exponential backoff. HTTP 429 responses and server failures are paced automatically."));
        page.Controls.Add(table);
        StyleControls(table);
        return page;
    }

    private TabPage BuildValuationTab()
    {
        var page = Page("Valuation");
        var table = FormTable();
        AddRow(table, "Bazaar price mode", _priceMode);
        AddRow(table, "Hypixel auction pages (0 = all)", _hypixelPages);
        AddRow(table, "DonutSMP auction pages (0 = none)", _donutPages);
        AddRow(table, "DonutSMP value per shard", _shardValue);
        AddWideRow(table, _museum);
        AddWideRow(table, _playerAuctions);
        AddWideRow(table, Note(
            "SkyBlock uses Bazaar prices, lowest active BIN, then official NPC sell values. Bridge-mode Donut estimates include " +
            "money and configured shard value only."));
        page.Controls.Add(table);
        StyleControls(table);
        ArtuurssTheme.ApplyCheckBox(_museum);
        ArtuurssTheme.ApplyCheckBox(_playerAuctions);
        return page;
    }

    private TabPage BuildNetworkTab()
    {
        var page = Page("Network proxy");
        var table = FormTable();
        AddRow(table, "HTTP proxy URL", _proxyUrl);
        AddRow(table, "Proxy username", _proxyUser);
        AddRow(table, "Proxy password", _proxyPassword);
        AddWideRow(table, Note(
            "This optional proxy applies only to HTTPS API calls. LiquidProxy carries Minecraft protocol traffic and is not used here."));
        page.Controls.Add(table);
        StyleControls(table);
        return page;
    }

    private static TabPage Page(string text) => new(text) { Padding = new Padding(18), AutoScroll = true };

    private static TableLayoutPanel FormTable() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        ColumnCount = 2,
        Padding = new Padding(4)
    };

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(3, 9, 18, 9),
            Anchor = AnchorStyles.Left,
            ForeColor = ArtuurssTheme.TextDim
        };
        table.Controls.Add(labelControl, 0, row);
        control.Margin = new Padding(3, 5, 3, 5);
        table.Controls.Add(control, 1, row);
    }

    private static void AddWideRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static void StyleControls(TableLayoutPanel table)
    {
        foreach (Control control in table.Controls)
        {
            switch (control)
            {
                case TextBox textBox:
                    ArtuurssTheme.ApplyTextBox(textBox);
                    break;
                case NumericUpDown numeric:
                    ArtuurssTheme.ApplyNumeric(numeric);
                    break;
                case ComboBox combo:
                    ArtuurssTheme.ApplyComboBox(combo);
                    break;
                case Label { AutoSize: true } label when label.MaximumSize.Width > 0:
                    ArtuurssTheme.ApplyNoteLabel(label);
                    break;
            }
        }
    }

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(650, 0),
        ForeColor = ArtuurssTheme.TextMuted,
        Margin = new Padding(3, 18, 3, 8)
    };
}
