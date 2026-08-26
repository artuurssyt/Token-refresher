# API and automation research

Research date: 2026-08-09

This document records the design basis for the packaged application. API responses evolve, so the implementation reads JSON defensively and treats missing fields as unavailable rather than as zero-value proof.

## 1. Hypixel data available directly

Primary sources: [Hypixel Public API reference](https://api.hypixel.net/) and [Hypixel API policy](https://developer.hypixel.net/policies).

The application uses these official endpoints:

| Endpoint | Use |
|---|---|
| `GET /v2/skyblock/profiles?uuid=` | Profile names, selected profile, member progression, purse/currencies, inventory containers, armor/equipment, sacks and pets when exposed |
| `GET /v2/skyblock/museum?profile=` | Museum assets when the player's API settings expose them |
| `GET /v2/skyblock/auction?player=` | A player's active auction items |
| `GET /v2/skyblock/bazaar` | Live Bazaar order summaries and computed prices |
| `GET /v2/skyblock/auctions?page=` | Current active auctions used to derive a lowest active BIN floor |
| `GET /v2/resources/skyblock/items` | Item identifiers and official NPC sell-price fallback |

The profile response can contain accessible inventory, Ender Chest, backpack/storage, wardrobe, bag, armor, equipment, sack and pet data. Exact sections depend on the player's in-game API/privacy settings and on Hypixel's current schema. Bank balance is profile-level; purse/currencies are member-level.

The API does not promise that every economically meaningful asset or item modifier is exposed. Private sections, minions and their contents, some account-bound progression, transient mechanics, and future fields can be absent. The app reports unknown stacks and does not fabricate values.

## 2. DonutSMP data available directly

Primary sources: [DonutSMP Swagger UI](https://api.donutsmp.net/) and its [OpenAPI document](https://api.donutsmp.net/doc.json).

The official API documents Bearer authentication and a limit of 250 requests per minute per key. Relevant endpoints are:

| Endpoint | Documented data |
|---|---|
| `GET /v1/stats/{user}` | money, shards, playtime, kills, deaths, mobs killed, blocks placed/broken, money made through sell, money spent in shop |
| `GET /v1/lookup/{user}` | username, rank and current/location information exposed by `/findplayer` |
| `GET /v1/auction/list/{page}` | current auction item, count, display name, item details, seller, price and time left |
| `GET /v1/auction/transactions/{page}` | recent transaction item, seller, price and sale time; documented as pages 1–10 with 100 entries per page |
| `/v1/leaderboards/.../{page}` | money, shards, playtime, kills, deaths, blocks, mobs and shop/sell leaderboards |

The documented player-stat and lookup models contain no inventory, Ender Chest, armor, base, private storage, orders, or complete held-asset field. Auction entries can identify assets a player currently lists, but they cannot reveal assets the player keeps elsewhere.

## 3. UUID and name resolution

The app accepts a Java username, compact UUID, or hyphenated UUID.

- Names are resolved through the Microsoft-owned Minecraft profile lookup at `https://api.minecraftservices.com/minecraft/profile/lookup/name/{name}`.
- The legacy official Mojang profile endpoint is used as a compatibility fallback: `https://api.mojang.com/users/profiles/minecraft/{name}`.
- UUID input is normalized to 32 lowercase hexadecimal characters and resolved through `https://sessionserver.mojang.com/session/minecraft/profile/{uuid}` so the current canonical name can be displayed.
- Successful identity responses are cached for one day because names/UUIDs do not need per-scan refetching.

## 4. SkyBlock net-worth method

The application values only the selected SkyBlock profile so assets are not summed across mutually separate profiles.

1. Liquid value is accessible purse plus accessible profile bank.
2. NBT item blobs are decoded locally. Item IDs and stack counts are extracted from inventory, storage/Ender Chest/backpacks/vault/wardrobe/bags, armor/equipment and other accessible containers.
3. Sack counts and pets are normalized into item identifiers.
4. Museum and active player-auction items are included when enabled and accessible.
5. Bazaar items use the selected computed Bazaar mode from the official `quick_status` fields.
6. Non-Bazaar items use the lowest current BIN found in the configured official auction snapshot.
7. Official NPC sell price is the final known-price fallback.
8. An item with no public match receives zero and increases the unvalued-stack count.

This is deliberately a transparent, conservative estimator. It is not a substitute for a mature third-party item-variant appraisal engine. Enchantments, recombobulation, skins, attributes, rarity upgrades, exotic history and special item metadata can make an individual item worth more than its base-item floor.

## 5. DonutSMP net-worth method

The highest-confidence estimate possible from the documented official API is:

`money + visible current auction asking value + (shards × configured shard value)`

Auction asking value is not guaranteed sale value. Shards default to zero unit value because the official API does not publish a stable conversion into money. Inventory and base assets remain explicitly excluded because they are not exposed.

The transaction endpoint can describe market sales, but without a player's inventory it cannot transform market prices into that player's missing assets. Using it to invent inventory value would be misleading.

## 6. Is an automated Minecraft client necessary?

No. The official DonutSMP API already mirrors `/stats` and `/findplayer` and provides auction data. Those are the read-only commands proposed for a fallback client. A bot would not make undocumented inventory data reliably available and would introduce account authentication, server-rule, anti-cheat and credential risk.

Accordingly, the application contains no Minecraft protocol client, automated commands, account login or background bot.

## 7. Safest authentication if a future client were authorized

If DonutSMP explicitly approved a future client integration in writing, it should use the normal Microsoft identity OAuth flow—preferably device authorization through a registered application—followed by the supported Xbox/Minecraft service exchange. Microsoft's primary reference for device authorization is [Microsoft identity platform device authorization grant](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code).

The client should use the operating-system browser/device-code experience and OS-protected credential storage. It should never ask a user to paste a launcher access token, Minecraft session token, refresh token, password or cookie. Because no client is necessary or currently authorized, none of this flow is implemented.

## 8. LiquidProxy compatibility and role

Primary source: [LiquidProxy documentation](https://liquidbounce.net/docs/tutorials/liquidproxy) and [LiquidProxy product page](https://liquidproxy.net/).

LiquidProxy can route Minecraft Java traffic through proxy credentials or a generated route address. It does not authenticate the Minecraft account; normal Microsoft/Minecraft authentication is still required. Proxy credentials only select the network route.

LiquidProxy states that it rejects non-Minecraft traffic and is not designed for botting. It therefore cannot serve as the HTTPS proxy for the Hypixel or DonutSMP APIs, is unnecessary for this API-only application, and is not integrated. The optional proxy field in Settings accepts a normal HTTP/HTTPS proxy for API networking only.

## 9. Rate limits and request handling

- DonutSMP documents 250 requests per minute per API key. The app defaults below that ceiling and caps its setting at 250.
- Hypixel assigns application limits through the Developer Dashboard; the API documents HTTP 429 for key or global throttles rather than one universal permanent number. The app's limit is configurable and defaults conservatively.
- Both clients pace requests before sending them.
- HTTP 429, 408-like timeout conditions and server failures are retried with exponential backoff and jitter; `Retry-After` is honored when supplied.
- Player and market response caches have separate configurable lifetimes.
- One Bazaar/auction market snapshot is shared across all players in a scan.
- Duplicate usernames are removed before network work begins.

## 10. Terms, policies and restrictions

Hypixel's [API policy](https://developer.hypixel.net/policies) requires effective caching, says the API is for player-facing use rather than automated collection at scale, prohibits embedding an API key in distributed code, and requires projects not to compromise game integrity or exist solely to track targeted players. This app is local, user-directed, uses typed-in player lists, encrypts a user-authorized key, caches responses, and applies bounded rate limits. A broadly distributed/public service should obtain an appropriate production application and re-check the policy before release.

The publicly posted [DonutSMP terms](https://donutsmp.co.uk/terms) prohibit macros, scripts, auto-clickers and other automated gameplay input. DonutSMP's authenticated web surfaces also warn against automated access. These restrictions reinforce the API-only architecture. Anyone extending the project must obtain explicit server permission before adding a Minecraft client or command automation.

API and server policies can change. Users and distributors remain responsible for reviewing the current official documents and configuring request volume appropriately.
