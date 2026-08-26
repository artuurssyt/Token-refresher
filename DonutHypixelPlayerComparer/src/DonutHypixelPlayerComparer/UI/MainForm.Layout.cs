using System.Drawing;

using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using DonutComparer.Core.Services;
namespace DonutHypixelPlayerComparer.UI;

public sealed partial class MainForm
{
    private Label? _headerTitle;
    private Label? _headerSubtitle;
    private Panel? _headerPanel;
    private ToolStrip? _toolbar;
    private StatusStrip? _statusBar;
    private ToolStripStatusLabel? _bridgeStatus;
    private ToolStripStatusLabel? _footerNote;

    private void BuildLayout()
    {
        Text = "DonutSMP + Hypixel Player Comparer";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1380, 860);
        MinimumSize = new Size(1040, 680);
        Font = ArtuurssTheme.BodyFont;
        ArtuurssTheme.Apply(this);

        _headerPanel = new Panel { Dock = DockStyle.Top, Height = 72 };
        _headerTitle = new Label
        {
            Text = "Minecraft Player Comparer",
            AutoSize = true,
            Location = new Point(18, 9)
        };
        _headerSubtitle = new Label
        {
            Text = "Official DonutSMP and Hypixel SkyBlock data with transparent estimated valuations",
            AutoSize = true,
            Location = new Point(20, 43)
        };
        ArtuurssTheme.ApplyHeader(_headerPanel, _headerTitle, _headerSubtitle);
        _headerPanel.Controls.Add(_headerTitle);
        _headerPanel.Controls.Add(_headerSubtitle);

        _toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Padding = new Padding(10, 6, 10, 6),
            AutoSize = true
        };
        _stopButton.Enabled = false;
        _exportButton.Enabled = false;
        _exportButton.DropDownItems.Add("CSV", null, async (_, _) => await ExportAsync("csv"));
        _exportButton.DropDownItems.Add("JSON", null, async (_, _) => await ExportAsync("json"));
        _exportButton.DropDownItems.Add("Excel (.xlsx)", null, async (_, _) => await ExportAsync("xlsx"));
        _testBridgeButton.ToolTipText =
            "Run one Donut bridge lookup for the first username, skipping SkyBlock market loading.";
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _settingsButton, _importButton, new ToolStripSeparator(), _startButton, _testBridgeButton, _stopButton,
            new ToolStripSeparator(), _exportButton
        });
        ArtuurssTheme.ApplyToolStrip(_toolbar);
        ArtuurssTheme.StylePrimaryToolStripButton(_startButton);

        _usernames.Dock = DockStyle.Fill;
        _usernames.Multiline = true;
        _usernames.ScrollBars = ScrollBars.Vertical;
        _usernames.AcceptsReturn = true;
        _usernames.PlaceholderText = "Enter one Minecraft username per line, paste a list, or import a .txt/.csv file";
        ArtuurssTheme.ApplyTextBox(_usernames);
        var inputGroup = new GroupBox
        {
            Text = "Usernames",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        ArtuurssTheme.ApplyGroupBox(inputGroup);
        inputGroup.Controls.Add(_usernames);

        ConfigureGrid();
        var resultsGroup = new GroupBox { Text = "Results — double-click a row for asset details", Dock = DockStyle.Fill };
        ArtuurssTheme.ApplyGroupBox(resultsGroup);
        resultsGroup.Controls.Add(_grid);
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        ArtuurssTheme.ApplyRichTextBox(_log);
        var logGroup = new GroupBox { Text = "Status and errors", Dock = DockStyle.Fill };
        ArtuurssTheme.ApplyGroupBox(logGroup);
        logGroup.Controls.Add(_log);

        var lower = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 430,
            BackColor = ArtuurssTheme.Background
        };
        _lowerSplit = lower;
        lower.Panel1.BackColor = ArtuurssTheme.Background;
        lower.Panel2.BackColor = ArtuurssTheme.Background;
        lower.Panel1.Controls.Add(resultsGroup);
        lower.Panel2.Controls.Add(logGroup);
        var content = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 150,
            Padding = new Padding(10),
            BackColor = ArtuurssTheme.Background
        };
        _contentSplit = content;
        content.Panel1.BackColor = ArtuurssTheme.Background;
        content.Panel2.BackColor = ArtuurssTheme.Background;
        content.Panel1.Controls.Add(inputGroup);
        content.Panel2.Controls.Add(lower);

        _bridgeStatus = new ToolStripStatusLabel
        {
            Text = "Donut bridge: idle",
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            BorderStyle = Border3DStyle.Etched,
            ForeColor = ArtuurssTheme.TextDim
        };
        _footerNote = new ToolStripStatusLabel(
            "Hypixel (optional) + Donut client bridge • keys encrypted with Windows DPAPI");
        _statusBar = new StatusStrip();
        _statusBar.Items.Add(_status);
        _statusBar.Items.Add(_bridgeStatus);
        _statusBar.Items.Add(_progress);
        _statusBar.Items.Add(_footerNote);
        ArtuurssTheme.ApplyStatusStrip(_statusBar);
        ArtuurssTheme.StyleProgressBar(_progress);

        Controls.Add(content);
        Controls.Add(_statusBar);
        Controls.Add(_toolbar);
        Controls.Add(_headerPanel);

        _settingsButton.Click += SettingsClicked;
        _importButton.Click += ImportClicked;
        _startButton.Click += async (_, _) => await StartScanAsync();
        _testBridgeButton.Click += async (_, _) => await TestBridgeAsync();
        _stopButton.Click += (_, _) => _scanCancellation?.Cancel();
        _grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
        _grid.CellDoubleClick += GridCellDoubleClick;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToOrderColumns = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        ArtuurssTheme.ApplyDataGridView(_grid);
        _grid.Columns.Add(Column("Username", "Username", 120));
        _grid.Columns.Add(Column("UUID", "Uuid", 220));
        _grid.Columns.Add(Column("Status", "Status", 90));
        _grid.Columns.Add(Column("Donut via", "DonutSource", 80));
        _grid.Columns.Add(Column("Donut money", "DonutMoney", 110, "N2"));
        _grid.Columns.Add(Column("Shards", "DonutShards", 75, "N0"));
        _grid.Columns.Add(Column("Playtime", "DonutPlaytime", 110));
        _grid.Columns.Add(Column("Kills", "DonutKills", 70, "N0"));
        _grid.Columns.Add(Column("Deaths", "DonutDeaths", 70, "N0"));
        _grid.Columns.Add(Column("Donut net worth", "DonutNetWorth", 130, "N2"));
        _grid.Columns.Add(Column("SkyBlock profile", "SkyBlockProfile", 115));
        _grid.Columns.Add(Column("SB level", "SkyBlockLevel", 80, "N2"));
        _grid.Columns.Add(Column("SB liquid", "SkyBlockLiquid", 110, "N2"));
        _grid.Columns.Add(Column("Inventory", "SkyBlockInventory", 110, "N2"));
        _grid.Columns.Add(Column("Storage", "SkyBlockStorage", 110, "N2"));
        _grid.Columns.Add(Column("SkyBlock net worth", "SkyBlockNetWorth", 145, "N2"));
        _grid.Columns.Add(Column("Numeric combined", "CombinedNetWorth", 135, "N2"));
        _grid.DataSource = _source;
    }

    private static DataGridViewTextBoxColumn Column(string header, string property, int width, string? format = null) =>
        new()
        {
            HeaderText = header,
            DataPropertyName = property,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            DefaultCellStyle = format is null ? new DataGridViewCellStyle() : new DataGridViewCellStyle { Format = format }
        };

    private void UpdateBridgeStatus(DonutComparer.Core.Services.DonutBridgeStatus? status)
    {
        if (_bridgeStatus is null) return;
        if (status is null || !status.Active)
        {
            _bridgeStatus.Text = "Donut bridge: idle";
            _bridgeStatus.ForeColor = ArtuurssTheme.TextDim;
            return;
        }
        if (status.ClientConnected)
        {
            _bridgeStatus.Text = status.CurrentUsername is null
                ? $"Donut bridge: connected • queue {status.QueueDepth}"
                : $"Donut bridge: {status.CurrentUsername} • queue {status.QueueDepth}";
            _bridgeStatus.ForeColor = ArtuurssTheme.RedBright;
        }
        else
        {
            _bridgeStatus.Text = $"Donut bridge: waiting for client • queue {status.QueueDepth}";
            _bridgeStatus.ForeColor = ArtuurssTheme.Red;
        }
    }
}
