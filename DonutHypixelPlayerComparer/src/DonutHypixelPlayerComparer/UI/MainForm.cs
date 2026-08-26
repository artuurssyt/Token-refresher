using System.Drawing;



using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using DonutComparer.Core.Services;
namespace DonutHypixelPlayerComparer.UI;

public sealed partial class MainForm : Form
{
    private readonly SettingsService _settingsService = new();
    private readonly TextBox _usernames = new();
    private readonly DataGridView _grid = new();
    private readonly RichTextBox _log = new();
    private readonly BindingSource _source = new();
    private readonly ToolStripButton _settingsButton = new("Settings");
    private readonly ToolStripButton _importButton = new("Import usernames");
    private readonly ToolStripButton _startButton = new("Start scan");
    private readonly ToolStripButton _testBridgeButton = new("Test bridge");
    private readonly ToolStripButton _stopButton = new("Stop");
    private readonly ToolStripDropDownButton _exportButton = new("Export");
    private readonly ToolStripProgressBar _progress = new() { Minimum = 0, Maximum = 1, Width = 220 };
    private readonly ToolStripStatusLabel _status = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<PlayerScanResult> _results = new();
    private CancellationTokenSource? _scanCancellation;
    private string _sortProperty = "Username";
    private bool _sortAscending = true;

    private SplitContainer? _contentSplit;
    private SplitContainer? _lowerSplit;

    public MainForm()
    {
        BuildLayout();
        Shown += MainFormShown;
        FormClosing += (_, _) => _scanCancellation?.Cancel();
    }

    private void MainFormShown(object? sender, EventArgs e)
    {
        if (_contentSplit is not null)
            _contentSplit.SplitterDistance = Math.Clamp(150, _contentSplit.Panel1MinSize,
                Math.Max(_contentSplit.Panel1MinSize, _contentSplit.Height - _contentSplit.Panel2MinSize - _contentSplit.SplitterWidth));
        if (_lowerSplit is not null)
        {
            var distance = (int)(_lowerSplit.Height * 0.62);
            _lowerSplit.SplitterDistance = Math.Clamp(distance, _lowerSplit.Panel1MinSize,
                Math.Max(_lowerSplit.Panel1MinSize, _lowerSplit.Height - _lowerSplit.Panel2MinSize - _lowerSplit.SplitterWidth));
        }
    }
}
