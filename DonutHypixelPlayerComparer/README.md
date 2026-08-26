# DonutSMP + Hypixel Player Comparer

A local Windows desktop application for comparing player statistics and transparent estimated net worth across DonutSMP and Hypixel SkyBlock.

- **Hypixel SkyBlock** uses the official Hypixel API.
- **DonutSMP** uses two sources with automatic failover. The official API (`/v1/stats/{user}`, key from `/api` in game) is tried first, and any player it errors on or returns nothing for falls through to the in-game client bridge: your [artuurssclient](https://www.artuurss.com) instance runs `/stats`, opens the full profile GUI, and posts parsed stats back over localhost.
- Each row's **Donut via** column shows which source actually produced its numbers.

The UI uses the dark red palette from [artuurss.com](https://www.artuurss.com).

## Run the application

The packaged application is a single self-contained executable:

`App\DonutHypixelPlayerComparer.exe`

A **Player Comparer** shortcut is also on the Desktop. No .NET runtime install is needed.

1. Open the executable.
2. Select **Settings** → enter a **Hypixel API key** if you want SkyBlock columns (optional for Donut-only).
3. Leave the **DonutSMP API key blank**. Turn on **Use Donut client bridge** (default).
4. Join DonutSMP in artuurssclient and enable the **PlayerCheckerBridge** Misc module.
5. Paste usernames, enter one per line, or select **Import usernames** for a `.txt`/`.csv` file.
6. Select **Start scan**, or **Test bridge** to run a single bridge lookup without loading SkyBlock prices.
7. Double-click a row to see the asset and price-source breakdown.
8. Use **Export** to save CSV, JSON, or Excel.

Donut data is meant to come from the in-game bridge (`/bal {username}` by default). An optional Donut
API key only adds auction-listing value; you do not need `/api` for normal scans.

To rebuild after code changes:

```powershell
dotnet publish src/DonutHypixelPlayerComparer/DonutHypixelPlayerComparer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o App
```

## Troubleshooting the bridge

Every bridge request is logged to `%LOCALAPPDATA%\DonutHypixelPlayerComparer\bridge-log.txt`,
including the raw JSON the Minecraft client posts. Use **Test bridge** for a fast single-player
round trip when debugging the client module.

## Donut client bridge (fallback)

The API is preferred where it works: DonutSMP's rules prohibit "macros, scripts, auto-clickers", so
automating `/stats` in game carries a ban risk the API does not. The bridge exists because the API
is not always dependable, and it only runs for players the API could not return.

If the API fails three times in a row, the app stops calling it for the rest of that scan and uses
the bridge for the remaining players.

During a scan the app listens on `127.0.0.1:47891` and queues usernames. The Minecraft module:

1. Sends `/stats {username}`
2. Clicks **View Full Profile** on the summary screen
3. Reads stat values from the `{USERNAME} Stats` chest GUI item lore
4. POSTs results back to the desktop app

Implement the module in artuurssclient using the prompt in [docs/MINECRAFT_CLIENT_MODULE_PROMPT.md](docs/MINECRAFT_CLIENT_MODULE_PROMPT.md).

Bridge-sourced Donut valuation includes **money + configured shard value** only. Auction listing value is unavailable without the DonutSMP REST API, so rows showing `Bridge` in **Donut via** exclude it.

## API key setup

### Hypixel

Create and use an application key through the [Hypixel Developer Dashboard](https://developer.hypixel.net/). The key is entered in Settings and encrypted locally with Windows DPAPI.

### DonutSMP

Run `/api` in game on DonutSMP and paste the key into Settings. It authenticates as
`Authorization: Bearer {KEY}` and allows 250 requests per minute per key. Setting it also unlocks
auction-listing valuation, which the bridge cannot provide. See the
[DonutSMP API documentation](https://api.donutsmp.net/v1/player/index.html).

## What the estimates mean

### SkyBlock

For the selected profile, the application separates purse/bank, inventory, storage, armor, museum, and auctions. Prices use Bazaar → lowest BIN → NPC sell fallback.

### DonutSMP (bridge mode)

Stats come from the in-game `/stats` full profile chest GUI: money, shards, playtime, kills, deaths, blocks, and mobs killed. Shop/sell totals appear only if present in the GUI lore.

## Privacy and security

- Hypixel API keys and proxy passwords are encrypted with Windows DPAPI.
- The Donut bridge binds to **localhost only**.
- No Minecraft password or session token is stored.
- The bridge sends chat commands only while the Minecraft module is enabled on DonutSMP.

## Build from source

Requirements: Windows 10/11 and .NET 8 SDK.

```powershell
dotnet build .\DonutHypixelPlayerComparer.sln -c Release
dotnet run --project .\tests\DonutHypixelPlayerComparer.Tests\DonutHypixelPlayerComparer.Tests.csproj -c Release
dotnet publish .\src\DonutHypixelPlayerComparer\DonutHypixelPlayerComparer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

See [docs/RESEARCH.md](docs/RESEARCH.md) for API research and [docs/MINECRAFT_CLIENT_MODULE_PROMPT.md](docs/MINECRAFT_CLIENT_MODULE_PROMPT.md) for the Minecraft module spec.
