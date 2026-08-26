using System.Drawing;


using DonutComparer.Core.Infrastructure;
using DonutComparer.Core.Models;
using DonutComparer.Core.Services;
namespace DonutHypixelPlayerComparer.UI;

public sealed class PlayerDetailsForm : Form
{
    public PlayerDetailsForm(PlayerScanResult result)
    {
        Text = result.Username + " — details";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(950, 650);
        MinimumSize = new Size(760, 480);
        Font = ArtuurssTheme.BodyFont;
        ArtuurssTheme.Apply(this);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        ArtuurssTheme.ApplyTabControl(tabs);
        var overviewPage = new TabPage("Overview") { Padding = new Padding(12) };
        var overview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = BuildOverview(result)
        };
        ArtuurssTheme.ApplyTextBox(overview, mono: true);
        overviewPage.Controls.Add(overview);

        var assetsPage = new TabPage("Valued assets") { Padding = new Padding(8) };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells
        };
        ArtuurssTheme.ApplyDataGridView(grid);
        grid.Columns.Add(TextColumn("Category", "Category", 150));
        grid.Columns.Add(TextColumn("Item ID", "ItemId", 180));
        grid.Columns.Add(TextColumn("Name", "DisplayName", 190));
        grid.Columns.Add(TextColumn("Count", "Count", 70, "N0"));
        grid.Columns.Add(TextColumn("Unit price", "UnitPrice", 100, "N2"));
        grid.Columns.Add(TextColumn("Total value", "TotalValue", 110, "N2"));
        grid.Columns.Add(TextColumn("Price source", "PriceSource", 170));
        grid.DataSource = result.Assets.OrderByDescending(asset => asset.TotalValue).ToList();
        assetsPage.Controls.Add(grid);
        tabs.TabPages.Add(overviewPage);
        tabs.TabPages.Add(assetsPage);
        Controls.Add(tabs);
    }

    private static DataGridViewTextBoxColumn TextColumn(string header, string property, int width, string? format = null) =>
        new()
        {
            HeaderText = header,
            DataPropertyName = property,
            Width = width,
            DefaultCellStyle = format is null ? new DataGridViewCellStyle() : new DataGridViewCellStyle { Format = format }
        };

    private static string BuildOverview(PlayerScanResult row) => $"""
Player
  Username: {row.Username}
  UUID:     {row.Uuid}
  Status:   {row.Status}
  Error:    {row.Error}

DonutSMP
  Money:                 {row.DonutMoney:N2}
  Shards:                {row.DonutShards:N0}
  Playtime:              {row.DonutPlaytime}
  Kills / deaths:        {row.DonutKills:N0} / {row.DonutDeaths:N0}
  Mobs killed:           {row.DonutMobsKilled:N0}
  Blocks broken/placed:  {row.DonutBrokenBlocks:N0} / {row.DonutPlacedBlocks:N0}
  Made from /sell:       {row.DonutMoneyMadeFromSell:N2}
  Spent in /shop:        {row.DonutMoneySpentOnShop:N2}
  Rank / location:       {row.DonutRank} / {row.DonutLocation}
  Auction listings:      {row.DonutAuctionListingsValue:N2}
  Estimated net worth:   {row.DonutNetWorth:N2}
  Method: {row.DonutValuation.Methodology}

Hypixel SkyBlock
  Profiles:              {row.SkyBlockProfiles}
  Selected profile:      {row.SkyBlockProfile}
  SkyBlock level:        {row.SkyBlockLevel:N2}
  Liquid coins:          {row.SkyBlockLiquid:N2}
  Inventory value:       {row.SkyBlockInventory:N2}
  Storage value:         {row.SkyBlockStorage:N2}
  Armor / other assets:  {row.SkyBlockOther:N2}
  Estimated net worth:   {row.SkyBlockNetWorth:N2}
  Valued / unvalued:     {row.SkyBlockValuation.ValuedItemStacks:N0} / {row.SkyBlockValuation.UnvaluedItemStacks:N0} stacks
  Method: {row.SkyBlockValuation.Methodology}

Numeric combined summary: {row.CombinedNetWorth:N2}
Note: DonutSMP money and SkyBlock coins are separate in-game economies; the numeric sum is not an exchange-rate valuation.
""";
}
