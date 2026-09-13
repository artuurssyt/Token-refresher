using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;
using DonutHypixelPlayerComparer.Services;

namespace DonutHypixelPlayerComparer.Tests;

internal static class Program
{
    private static int Main()
    {
        var failures = new List<string>();
        Run("username parsing", TestUsernames, failures);
        Run("NBT item parsing", TestNbt, failures);
        Run("Donut valuation limits", TestDonutValuation, failures);
        Run("Donut GUI lore parser", TestDonutGuiParser, failures);
        Run("Donut bridge host queue", TestDonutBridgeHost, failures);
        Run("Donut bridge failure reporting", TestDonutBridgeFailures, failures);
        Run("Donut API string stat parsing", TestDonutApiStatStrings, failures);
        Run("Bridge stays armed alongside API key", TestBridgeArmedWithApiKey, failures);
        Run("Bridge chest payload reaches grid", TestBridgeChestPayloadEndToEnd, failures);
        Run("Bridge dialog payload reaches grid", TestBridgeDialogPayloadEndToEnd, failures);
        Run("Bridge rejects all-zero screens", TestBridgeRejectsZeroWithRawText, failures);
        Run("Lore parser resists wrong-slot decoys", TestLoreParserIgnoresDecoySlots, failures);
        Run("Grid columns bind to real properties", TestGridColumnsBind, failures);
        Run("SkyBlock purse and inventory API", TestSkyBlockProfileParsing, failures);
        Run("Minecraft identity JSON parsing", TestIdentityJsonParsing, failures);
        Run("CSV JSON Excel exports", TestExports, failures);
        Console.WriteLine(failures.Count == 0 ? "All tests passed." : string.Join(Environment.NewLine, failures));
        return failures.Count == 0 ? 0 : 1;
    }

    private static void TestUsernames()
    {
        var parsed = UsernameParser.Parse("username\nNotch\nnotch\nDinnerbone,bad-name\n");
        Assert(parsed.Usernames.SequenceEqual(new[] { "Notch", "Dinnerbone" }), "unique names");
        Assert(parsed.Duplicates.Count == 1, "duplicate detection");
        Assert(parsed.Invalid.SequenceEqual(new[] { "bad-name" }), "invalid detection");
    }

    private static void TestNbt()
    {
        var parsed = HypixelItemParser.ParseInventory(BuildInventoryNbt());
        Assert(parsed.Count == 1, "one item parsed");
        Assert(parsed[0].ItemId == "DIAMOND", "item id parsed");
        Assert(parsed[0].DisplayName == "Diamond", "formatting removed");
        Assert(parsed[0].Count == 2, "count parsed");
    }

    private static void TestDonutValuation()
    {
        var stats = new DonutStats { Money = 1000, Shards = 5 };
        var listings = new[]
        {
            new DonutAuctionListing { SellerName = "Notch", ItemId = "diamond", Count = 2, Price = 500 },
            new DonutAuctionListing { SellerName = "SomeoneElse", ItemId = "stone", Count = 1, Price = 99 }
        };
        var value = DonutValuationService.Calculate(stats, "Notch", "uuid", listings,
            new AppSettings { DonutShardUnitValue = 10 });
        Assert(value.Valuation.LiquidCoins == 1000, "money is liquid");
        Assert(value.Valuation.OtherAssetsValue == 550, "listing and configured shard value");
        Assert(value.Valuation.Total == 1550, "total valuation");
    }

    private static void TestDonutGuiParser()
    {
        var lore = new Dictionary<int, IReadOnlyList<string>>
        {
            [10] = new[] { "Money", "$ 2.2B" },
            [11] = new[] { "Shards", "42" },
            [12] = new[] { "Kills", "406" },
            [13] = new[] { "Deaths", "166" },
            [14] = new[] { "Playtime", "5d 3h" },
            [16] = new[] { "Blocks broken", "1.2k" },
            [15] = new[] { "Blocks placed", "800" },
            [19] = new[] { "Mobs killed", "500" }
        };
        var stats = DonutGuiStatsParser.ParseFromSlotLore(lore);
        Assert(stats.Money == 2_200_000_000m, "money suffix B");
        Assert(stats.Shards == 42, "shards");
        Assert(stats.Kills == 406, "kills");
        Assert(stats.Deaths == 166, "deaths");
        Assert(stats.PlaytimeSeconds == (5 * 86_400) + (3 * 3_600), "playtime");
        Assert(stats.BrokenBlocks == 1200, "broken blocks");
        Assert(stats.PlacedBlocks == 800, "placed blocks");
        Assert(stats.MobsKilled == 500, "mobs killed");
        Assert(DonutGuiStatsParser.HasMinimumStats(stats), "minimum stats");

        var rankedMoney = DonutGuiStatsParser.ParseFromSlotLore(new Dictionary<int, IReadOnlyList<string>>
        {
            [10] = new[] { "Ranked #14", "$ 2.2B" }
        });
        Assert(rankedMoney.Money == 2_200_000_000m, "money ignores rank prefix");

        var emptyPayload = new DonutBridgeStatsPayload();
        var emptyStats = new DonutStats();
        Assert(!DonutGuiStatsParser.HasMinimumStats(emptyStats, emptyPayload), "empty stats rejected");

        // Lore is only enough once it actually yields a value; text alone must not pass.
        var loreOnly = new DonutBridgeStatsPayload { RawGuiText = new() { ["10"] = ["Money", "$ 2.2B"] } };
        Assert(DonutGuiStatsParser.HasMinimumStats(DonutGuiStatsParser.FromPayload(loreOnly), loreOnly),
            "lore with a real value satisfies minimum");
        var zeroLore = new DonutBridgeStatsPayload { RawGuiText = new() { ["10"] = ["Money", "0"] } };
        Assert(!DonutGuiStatsParser.HasMinimumStats(DonutGuiStatsParser.FromPayload(zeroLore), zeroLore),
            "lore that parses to zero is rejected");

        var labelAbove = DonutGuiStatsParser.ParseFromSlotLore(new Dictionary<int, IReadOnlyList<string>>
        {
            [10] = new[] { "§6Money", "§7$1,234,567", "§eClick to view" }
        });
        Assert(labelAbove.Money == 1_234_567m, "value on the line below the label, got " + labelAbove.Money);

        var movedSlots = DonutGuiStatsParser.ParseFromSlotLore(new Dictionary<int, IReadOnlyList<string>>
        {
            [22] = new[] { "Money", "$500k" },
            [23] = new[] { "Kills", "12" },
            [24] = new[] { "Mobs Killed", "9,001" }
        });
        Assert(movedSlots.Money == 500_000m, "money found outside slot 10, got " + movedSlots.Money);
        Assert(movedSlots.Kills == 12, "kills not stolen by mobs killed, got " + movedSlots.Kills);
        Assert(movedSlots.MobsKilled == 9001, "mobs killed found, got " + movedSlots.MobsKilled);

        Assert(DonutGuiStatsParser.TryParseBalanceFromChat(["ardatzpr has $ 1.2K"], "ardatzpr", out var balMoney),
            "chat balance parsed");
        Assert(balMoney == 1200, "1.2K chat balance, got " + balMoney);

        var clientParsed = DonutGuiStatsParser.FromPayload(new DonutBridgeStatsPayload
        {
            Money = 2_200_000_000m,
            Kills = 406,
            RawGuiText = new Dictionary<string, List<string>>
            {
                ["10"] = new List<string> { "Ranked #14 of 900 players" },
                ["12"] = new List<string> { "Kills", "406" }
            }
        });
        Assert(clientParsed.Money == 2_200_000_000m, "client money survives a misleading lore line, got " + clientParsed.Money);
        Assert(clientParsed.Kills == 406, "client kills preserved, got " + clientParsed.Kills);
    }

    private static void TestDonutBridgeHost()
    {
        var settings = new AppSettings { DonutBridgePort = 47892, DonutBridgeJobTimeoutSeconds = 5 };
        using var host = new DonutBridgeHost(settings);
        host.Start();
        Assert(host.Status.Active, "bridge active");
        var waitTask = host.WaitForStatsAsync("Notch", CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47892/") };
        var jobResponse = client.GetAsync("/api/v1/job").GetAwaiter().GetResult();
        Assert(jobResponse.IsSuccessStatusCode, "job available");
        var jobJson = jobResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var jobDoc = System.Text.Json.JsonDocument.Parse(jobJson);
        var jobId = jobDoc.RootElement.GetProperty("jobId").GetString() ?? string.Empty;
        var payload = System.Text.Json.JsonSerializer.Serialize(new DonutBridgeStatsPayload
        {
            Money = 100,
            Kills = 1,
            Deaths = 0,
            PlaytimeSeconds = 60,
            RawGuiText = new Dictionary<string, List<string>>
            {
                ["10"] = new List<string> { "Money", "100" },
                ["12"] = new List<string> { "Kills", "1" }
            }
        });
        var complete = client.PostAsync($"/api/v1/job/{jobId}/complete",
            new StringContent(payload, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        Assert(complete.IsSuccessStatusCode, "complete accepted");
        var stats = waitTask.GetAwaiter().GetResult();
        Assert(stats?.Money == 100, "bridge returned stats");

        foreach (var address in new[] { "127.0.0.1", "[::1]", "localhost" })
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var status = probe.GetAsync($"http://{address}:47892/api/v1/status").GetAwaiter().GetResult();
            Assert(status.IsSuccessStatusCode, $"status reachable over {address}");
        }
    }

    private static void TestDonutBridgeFailures()
    {
        var settings = new AppSettings { DonutBridgePort = 47893, DonutBridgeJobTimeoutSeconds = 15 };
        using var host = new DonutBridgeHost(settings);
        var diagnostics = new List<string>();
        host.Diagnostic += message => { lock (diagnostics) diagnostics.Add(message); };
        host.Start();
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47893/") };

        var failWait = host.WaitForStatsAsync("FailPlayer", CancellationToken.None);
        var failJobId = LeaseJob(client);
        var failBody = new StringContent("""{"error":"stats chest did not open"}""", Encoding.UTF8, "application/json");
        Assert(client.PostAsync($"/api/v1/job/{failJobId}/fail", failBody).GetAwaiter().GetResult().IsSuccessStatusCode,
            "fail accepted");
        var failError = CaptureError(failWait);
        Assert(failError.Contains("stats chest did not open", StringComparison.Ordinal),
            "client failure reason reaches the caller, got: " + failError);

        var zeroWait = host.WaitForStatsAsync("ZeroPlayer", CancellationToken.None);
        var zeroJobId = LeaseJob(client);
        var zeroBody = new StringContent("""{"money":0,"kills":0,"deaths":0}""", Encoding.UTF8, "application/json");
        var zeroResponse = client.PostAsync($"/api/v1/job/{zeroJobId}/complete", zeroBody).GetAwaiter().GetResult();
        Assert((int)zeroResponse.StatusCode == 422, "all-zero completion rejected with 422");
        var zeroError = CaptureError(zeroWait);
        Assert(zeroError.Contains("no usable stats", StringComparison.Ordinal),
            "zero-stat reason reaches the caller, got: " + zeroError);

        lock (diagnostics)
        {
            Assert(diagnostics.Any(line => line.Contains("client took job", StringComparison.OrdinalIgnoreCase)),
                "lease diagnostic emitted");
            Assert(diagnostics.Any(line => line.Contains("stats chest did not open", StringComparison.Ordinal)),
                "failure diagnostic emitted");
        }

        var salvageWait = host.WaitForStatsAsync("SalvagePlayer", CancellationToken.None);
        var salvageJobId = LeaseJob(client);
        var salvageBody = new StringContent(
            """
            {
              "error":"View Full Profile not found within timeout. screen=none lastChat='SalvagePlayer has $ 1.2K'",
              "rawGuiText":{"chat":["SalvagePlayer has $ 1.2K"]}
            }
            """, Encoding.UTF8, "application/json");
        Assert(client.PostAsync($"/api/v1/job/{salvageJobId}/fail", salvageBody).GetAwaiter().GetResult().IsSuccessStatusCode,
            "salvage fail accepted");
        var salvaged = salvageWait.GetAwaiter().GetResult();
        Assert(salvaged?.Money == 1200, "chat in fail payload salvaged, got " + salvaged?.Money);
    }

    private static string LeaseJob(HttpClient client)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var response = client.GetAsync("/api/v1/job").GetAwaiter().GetResult();
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) { Thread.Sleep(20); continue; }
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return document.RootElement.GetProperty("jobId").GetString() ?? string.Empty;
        }
        throw new InvalidOperationException("bridge never offered a job");
    }

    private static string CaptureError(Task<DonutStats?> task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex) { return ex.Message; }
        throw new InvalidOperationException("expected the bridge job to fail");
    }

    /// <summary>
    /// The slot numbers are unverified guesses and the client now sends every slot, so an
    /// unrelated item sitting on a guessed slot must never be mistaken for a stat.
    /// </summary>
    private static void TestLoreParserIgnoresDecoySlots()
    {
        var decoyed = DonutGuiStatsParser.ParseFromSlotLore(new Dictionary<int, IReadOnlyList<string>>
        {
            [10] = new[] { "Server Info", "Online players: 1200" },
            [11] = new[] { "Close", "Click to go back" },
            [12] = new[] { "Kills", "406" },
            [13] = new[] { "Deaths", "166" }
        });
        Assert(decoyed.Money == 0, $"unlabelled decoy is not read as money, got {decoyed.Money}");
        Assert(decoyed.Shards == 0, $"unlabelled decoy is not read as shards, got {decoyed.Shards}");
        Assert(decoyed.Kills == 406, "labelled kills still parse");
        Assert(decoyed.Deaths == 166, "labelled deaths still parse");

        // Stats found by label anywhere must win over the slot hints entirely.
        var moved = DonutGuiStatsParser.ParseFromSlotLore(new Dictionary<int, IReadOnlyList<string>>
        {
            [10] = new[] { "Decoration", "Pane" },
            [31] = new[] { "Money", "$ 2.2B" },
            [32] = new[] { "Shards", "1,204" }
        });
        Assert(moved.Money == 2_200_000_000m, $"money found off the hinted slot, got {moved.Money}");
        Assert(moved.Shards == 1204m, $"shards found off the hinted slot, got {moved.Shards}");
    }

    /// <summary>
    /// A mistyped DataPropertyName renders as a silently blank column rather than an error, so the
    /// names the grid binds are checked against the model the same way WinForms resolves them.
    /// </summary>
    private static void TestGridColumnsBind()
    {
        var bound = new[]
        {
            "Username", "Uuid", "Status", "DonutSource", "DonutMoney", "DonutShards", "DonutPlaytime",
            "DonutKills", "DonutDeaths", "DonutNetWorth", "SkyBlockProfile", "SkyBlockLevel",
            "SkyBlockLiquid", "SkyBlockInventory", "SkyBlockStorage", "SkyBlockNetWorth", "CombinedNetWorth"
        };
        var properties = System.ComponentModel.TypeDescriptor.GetProperties(typeof(PlayerScanResult));
        foreach (var name in bound)
            Assert(properties.Find(name, false) is not null, $"grid column '{name}' resolves on PlayerScanResult");
    }

    /// <summary>
    /// Drives the whole bridge path the Minecraft module uses — lease a job, POST the payload it
    /// builds, then check the values the grid actually binds to. A chest GUI keys rawGuiText by
    /// slot index.
    /// </summary>
    private static void TestBridgeChestPayloadEndToEnd()
    {
        var payload = new Dictionary<string, object?>
        {
            ["money"] = 2_200_000_000L,
            ["shards"] = 1204L,
            ["playtimeSeconds"] = 442_800L,
            ["kills"] = 406L,
            ["deaths"] = 166L,
            ["mobsKilled"] = 500L,
            ["brokenBlocks"] = 14_300L,
            ["placedBlocks"] = 4200L,
            ["moneyMadeFromSell"] = 0L,
            ["moneySpentOnShop"] = 0L,
            ["rank"] = "",
            ["location"] = "",
            ["rawGuiText"] = new Dictionary<string, List<string>>
            {
                ["4"] = new() { "ARTUURSS", "Rank: Donut" },
                ["10"] = new() { "Money", "$ 2.2B" },
                ["11"] = new() { "Shards", "1,204" },
                ["12"] = new() { "Kills", "406" },
                ["13"] = new() { "Deaths", "166" },
                ["14"] = new() { "Playtime", "5d 3h" },
                ["15"] = new() { "Blocks Placed", "4.2K" },
                ["16"] = new() { "Blocks Broken", "14.3K" },
                ["19"] = new() { "Mob Kills", "500" },
                ["widgets"] = new() { "ARTUURSS Stats" }
            }
        };
        var result = RunBridgeRoundTrip(47895, "ARTUURSS", payload);
        AssertDonutRow(result, "chest");
    }

    /// <summary>
    /// The same journey for a server-driven dialog, where there are no slots at all and every
    /// stat arrives as one block of widget lines like "Money: 2.2B".
    /// </summary>
    private static void TestBridgeDialogPayloadEndToEnd()
    {
        var payload = new Dictionary<string, object?>
        {
            ["money"] = 2_200_000_000L,
            ["shards"] = 1204L,
            ["playtimeSeconds"] = 442_800L,
            ["kills"] = 406L,
            ["deaths"] = 166L,
            ["mobsKilled"] = 500L,
            ["brokenBlocks"] = 14_300L,
            ["placedBlocks"] = 4200L,
            ["rank"] = "",
            ["location"] = "",
            ["rawGuiText"] = new Dictionary<string, List<string>>
            {
                ["widgets"] = new()
                {
                    "ARTUURSS Stats",
                    "Money: $ 2.2B",
                    "Shards: 1,204",
                    "Kills: 406",
                    "Deaths: 166",
                    "Playtime: 5d 3h",
                    "Blocks Placed: 4.2K",
                    "Blocks Broken: 14.3K",
                    "Mob Kills: 500",
                    "View Full Profile",
                    "Back"
                }
            }
        };
        var result = RunBridgeRoundTrip(47896, "ARTUURSS", payload);
        AssertDonutRow(result, "dialog");
    }

    /// <summary>A screen with text but no real stats must fail loudly rather than show zeros.</summary>
    private static void TestBridgeRejectsZeroWithRawText()
    {
        var payload = new Dictionary<string, object?>
        {
            ["money"] = 0L,
            ["kills"] = 0L,
            ["deaths"] = 0L,
            ["rawGuiText"] = new Dictionary<string, List<string>>
            {
                ["widgets"] = new() { "Loading...", "Please wait" }
            }
        };
        string error;
        try
        {
            RunBridgeRoundTrip(47897, "GhostPlayer", payload);
            error = string.Empty;
        }
        catch (Exception ex) { error = ex.Message; }
        Assert(error.Length > 0, "all-zero screen is rejected instead of reported as success");
        Assert(error.Contains("no usable stats", StringComparison.Ordinal),
            "rejection explains itself, got: " + error);
    }

    private static PlayerScanResult RunBridgeRoundTrip(int port, string username,
        Dictionary<string, object?> payload)
    {
        var settings = new AppSettings { DonutBridgePort = port, DonutBridgeJobTimeoutSeconds = 20 };
        using var host = new DonutBridgeHost(settings);
        host.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var wait = host.WaitForStatsAsync(username, CancellationToken.None);
        var jobId = LeaseJob(client);
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        client.PostAsync($"/api/v1/job/{jobId}/complete", body).GetAwaiter().GetResult();
        var stats = wait.GetAwaiter().GetResult();

        var valued = DonutValuationService.Calculate(stats, username, "uuid",
            Array.Empty<DonutAuctionListing>(), settings);
        return new PlayerScanResult
        {
            Username = username,
            Donut = stats,
            DonutSource = "Bridge",
            DonutValuation = valued.Valuation,
            Status = "Complete"
        };
    }

    private static void AssertDonutRow(PlayerScanResult row, string shape)
    {
        Assert(row.Donut is not null, $"{shape}: bridge returned stats");
        Assert(row.DonutMoney == 2_200_000_000m, $"{shape}: money reaches the grid, got {row.DonutMoney}");
        Assert(row.DonutShards == 1204m, $"{shape}: shards reach the grid, got {row.DonutShards}");
        Assert(row.DonutKills == 406, $"{shape}: kills reach the grid, got {row.DonutKills}");
        Assert(row.DonutDeaths == 166, $"{shape}: deaths reach the grid, got {row.DonutDeaths}");
        Assert(row.DonutMobsKilled == 500, $"{shape}: mob kills reach the grid, got {row.DonutMobsKilled}");
        Assert(row.DonutBrokenBlocks == 14_300, $"{shape}: broken blocks, got {row.DonutBrokenBlocks}");
        Assert(row.DonutPlacedBlocks == 4200, $"{shape}: placed blocks, got {row.DonutPlacedBlocks}");
        Assert(row.DonutPlaytime == "5d 3h 0m", $"{shape}: playtime formats for display, got '{row.DonutPlaytime}'");
        Assert(row.DonutSource == "Bridge", $"{shape}: source column populated");
        Assert(row.DonutNetWorth > 0, $"{shape}: net worth is non-zero, got {row.DonutNetWorth}");
        Assert(row.CombinedNetWorth == row.DonutNetWorth, $"{shape}: combined includes Donut");
    }

    // Configuring an API key must not disarm the bridge, otherwise a flaky API leaves no fallback.
    private static void TestBridgeArmedWithApiKey()
    {
        var settings = new AppSettings
        {
            DonutBridgeEnabled = true,
            DonutBridgeOnly = false,
            DonutApiKey = "test-key",
            DonutBridgePort = 47894,
            MaxDonutAuctionPages = 0,
            HypixelApiKey = string.Empty
        };
        var messages = new List<string>();
        var progress = new Progress<ScanProgress>(update => { lock (messages) messages.Add(update.Message); });
        using var service = new PlayerScanService(settings);
        service.ScanAsync(Array.Empty<string>(), progress, CancellationToken.None).GetAwaiter().GetResult();
        Thread.Sleep(100);
        lock (messages)
        {
            Assert(messages.Any(line => line.Contains("bridge listening", StringComparison.OrdinalIgnoreCase)),
                "bridge starts even though an API key is configured");
            Assert(messages.Any(line => line.Contains("falling back", StringComparison.OrdinalIgnoreCase)),
                "scan announces API-first with bridge fallback");
        }

        settings = new AppSettings
        {
            DonutBridgeEnabled = true,
            DonutBridgeOnly = true,
            DonutApiKey = "test-key",
            DonutBridgePort = 47895,
            MaxDonutAuctionPages = 0,
            HypixelApiKey = string.Empty
        };
        messages.Clear();
        using var bridgeOnly = new PlayerScanService(settings);
        bridgeOnly.ScanAsync(Array.Empty<string>(), progress, CancellationToken.None).GetAwaiter().GetResult();
        Thread.Sleep(100);
        lock (messages)
        {
            Assert(messages.Any(line => line.Contains("bridge-only", StringComparison.OrdinalIgnoreCase)),
                "scan announces bridge-only mode");
            Assert(!messages.Any(line => line.Contains("falling back", StringComparison.OrdinalIgnoreCase)),
                "bridge-only scan does not announce API fallback");
        }
    }

    // api.Stats types every field as a string, so the client must survive both raw digits
    // and the compact formatting /stats renders in game.
    private static void TestDonutApiStatStrings()
    {
        Assert(DonutGuiStatsParser.ParseCompactDecimal("2200000000") == 2_200_000_000m, "raw digit string");
        Assert(DonutGuiStatsParser.ParseCompactDecimal("3.2M") == 3_200_000m, "compact millions");
        Assert(DonutGuiStatsParser.ParseCompactDecimal("$ 230M") == 230_000_000m, "currency prefixed");
        Assert(DonutGuiStatsParser.ParseCompactDecimal("2.17B") == 2_170_000_000m, "compact billions");
        Assert(DonutGuiStatsParser.ParseCompactDecimal("14.3K") == 14_300m, "compact thousands");
        Assert(DonutGuiStatsParser.ParseCompactDecimal("1,234,567") == 1_234_567m, "grouped digits");
        Assert(DonutGuiStatsParser.ParsePlaytimeSeconds("31h 52m") == (31 * 3600) + (52 * 60), "humanised playtime");
        Assert(DonutGuiStatsParser.ParseCompactDecimal("") == 0, "empty string is zero");
    }

    private static void TestSkyBlockProfileParsing()
    {
        const string uuid = "e609eda4ea004d2bbe6d4efb7d1fdde2";
        var settings = new AppSettings { IncludeMuseum = false, IncludePlayerAuctions = false };
        using var http = new ResilientHttpClient(settings);
        var service = new SkyBlockValuationService(new HypixelClient(http, settings), settings);

        // Shape taken from a real profiles response: purse lives under currencies.coin_purse.
        var json = """
        {"success":true,"profiles":[{"profile_id":"da01f8d7","cute_name":"Banana","selected":true,
          "banking":{"balance":250.0},
          "members":{"UUID":{"currencies":{"coin_purse":69.5},"leveling":{"experience":1800}}}}]}
        """.Replace("UUID", uuid);
        using var document = JsonDocument.Parse(json);
        var data = service.CalculateAsync(document.RootElement, uuid, new MarketSnapshot(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(data.SelectedProfile == "Banana", "selected profile name");
        Assert(data.Valuation.LiquidCoins == 319.5m, "purse plus bank, got " + data.Valuation.LiquidCoins);
        Assert(Math.Abs(data.Level - 18) < 0.001, "skyblock level, got " + data.Level);
        Assert(!data.InventoryApiEnabled, "inventory API reported as disabled");

        var withInventory = """
        {"success":true,"profiles":[{"profile_id":"da01f8d7","cute_name":"Mango","selected":true,
          "members":{"UUID":{"currencies":{"coin_purse":10},"inventory":{"inv_contents":{"data":""}}}}}]}
        """.Replace("UUID", uuid);
        using var second = JsonDocument.Parse(withInventory);
        var enabled = service.CalculateAsync(second.RootElement, uuid, new MarketSnapshot(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(enabled.InventoryApiEnabled, "inventory API reported as enabled");
    }

    private static void TestIdentityJsonParsing()
    {
        var json = """{"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch"}""";
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = JsonValue.String(root, "id", string.Empty).Replace("-", string.Empty);
        Assert(id == "069a79f444e94726a5befca90e38aaf5", "id property parsed");
        Assert(id.Length == 32, "uuid length accepted by identity client");
        Assert(JsonValue.String(root, "name", string.Empty) == "Notch", "name property parsed");
        Assert(JsonValue.String(root, "missing", "fallback") == "fallback", "missing property fallback");
    }

    private static void TestExports()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DonutHypixelComparerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rows = new[] { new PlayerScanResult { Username = "Notch", Uuid = new string('a', 32), Status = "Complete" } };
            var csv = Path.Combine(directory, "results.csv");
            var json = Path.Combine(directory, "results.json");
            var excel = Path.Combine(directory, "results.xlsx");
            ExportService.ExportCsvAsync(csv, rows, CancellationToken.None).GetAwaiter().GetResult();
            ExportService.ExportJsonAsync(json, rows, CancellationToken.None).GetAwaiter().GetResult();
            ExportService.ExportExcelAsync(excel, rows, CancellationToken.None).GetAwaiter().GetResult();
            Assert(File.ReadAllText(csv).Contains("Notch"), "CSV content");
            Assert(File.ReadAllText(json).Contains("Notch"), "JSON content");
            using var archive = ZipFile.OpenRead(excel);
            Assert(archive.GetEntry("xl/worksheets/sheet1.xml") is not null, "Excel worksheet");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string BuildInventoryNbt()
    {
        using var raw = new MemoryStream();
        WriteByte(raw, 10); WriteString(raw, string.Empty);
        WriteByte(raw, 9); WriteString(raw, "i"); WriteByte(raw, 10); WriteInt(raw, 1);
        WriteByte(raw, 1); WriteString(raw, "Count"); WriteByte(raw, 2);
        WriteByte(raw, 10); WriteString(raw, "tag");
        WriteByte(raw, 10); WriteString(raw, "ExtraAttributes");
        WriteByte(raw, 8); WriteString(raw, "id"); WriteString(raw, "DIAMOND"); WriteByte(raw, 0);
        WriteByte(raw, 10); WriteString(raw, "display");
        WriteByte(raw, 8); WriteString(raw, "Name"); WriteString(raw, "§aDiamond"); WriteByte(raw, 0);
        WriteByte(raw, 0); WriteByte(raw, 0); WriteByte(raw, 0);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, true)) raw.WriteTo(gzip);
        return Convert.ToBase64String(compressed.ToArray());
    }

    private static void WriteByte(Stream stream, byte value) => stream.WriteByte(value);
    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);
        stream.Write(length); stream.Write(bytes);
    }

    private static void Run(string name, Action test, List<string> failures)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures.Add("FAIL " + name + ": " + ex.Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
