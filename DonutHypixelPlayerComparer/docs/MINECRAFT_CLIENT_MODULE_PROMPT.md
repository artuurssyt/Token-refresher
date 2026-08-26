# Minecraft Client Module Prompt (artuurssclient)

Paste this entire document into a Cursor chat opened on `F:\artuurssclient` to implement the DonutSMP player-checker bridge module.

---

## Goal

Create a **Misc** module named **PlayerCheckerBridge** in artuurssclient that connects to the desktop app **DonutHypixelPlayerComparer** over localhost HTTP while the user is on DonutSMP. The desktop app queues usernames during a scan; this module pulls jobs, runs in-game lookups, and posts parsed stats back.

Hypixel SkyBlock is handled by the desktop app via API. **Only DonutSMP stats** come through this module.

## Bridge API (desktop app)

Base URL: `http://127.0.0.1:47891` (port configurable in desktop Settings → Donut bridge)

The desktop app listens on both `127.0.0.1` and `[::1]`, so `localhost` works regardless of
whether Java resolves it to IPv4 or IPv6. Prefer `127.0.0.1` anyway to remove one variable.

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/v1/heartbeat` | Every ~2s while enabled. JSON: `{ "clientVersion", "serverAddress", "playerName" }` |
| GET | `/api/v1/status` | `{ active, queueDepth, currentUsername, commandTemplate, commandDelayMs, clientConnected }` |
| GET | `/api/v1/job` | Returns `{ "jobId", "username" }` or HTTP 204 if idle |
| POST | `/api/v1/job/{jobId}/complete` | Submit parsed stats (see payload below) |
| POST | `/api/v1/job/{jobId}/fail` | JSON: `{ "error": "reason" }` |

### Complete payload JSON

```json
{
  "money": 2200000000,
  "shards": 42,
  "playtimeSeconds": 442800,
  "kills": 406,
  "deaths": 166,
  "mobsKilled": 500,
  "brokenBlocks": 1200,
  "placedBlocks": 800,
  "moneyMadeFromSell": 0,
  "moneySpentOnShop": 0,
  "rank": "",
  "location": "",
  "rawGuiText": {
    "10": ["Money", "$ 2.2B"],
    "11": ["Shards", "42"]
  }
}
```

`rawGuiText` keys are container slot indices as strings. Include all stat slot lore for debugging.

## In-game flow (three phases)

> ## Context — this module is the fallback, and it still matters
>
> DonutSMP ships an **official public API** that returns exactly what `/stats` shows:
> `GET https://api.donutsmp.net/v1/stats/{user}` with `Authorization: Bearer {KEY}`.
> Keys are created in game with `/api`, and the documented limit is **250 requests per minute**.
> The desktop app tries it first.
>
> That API is not reliably available in practice, so this module is what the app falls back to for
> any player the API errors on or returns nothing for. It is still worth building — just be aware
> that it will often handle only part of a scan rather than all of it.
>
> Note also that DonutSMP's rules prohibit "macros, scripts, auto-clickers", so a module that sends
> `/stats` and clicks through the resulting GUI on a loop carries some rule risk.
>
> ## CRITICAL — the screen may not be a container at all
>
> Do **not** assume the GUI is a chest inventory, and do not assume it is a widget screen either.
> Since Java 1.21.6 a vanilla server can drive **two different** kinds of screen:
>
> - **Container screens** (`AbstractContainerScreen` / Yarn `HandledScreen`) opened by
>   `ClientboundOpenScreenPacket`. These hold `ItemStack`s in slots and carry no buttons, so
>   "View Full Profile" would be an item's display name and must be activated by clicking a slot.
> - **Server-driven Dialogs** (Yarn `net.minecraft.client.gui.screen.dialog.DialogScreen`,
>   delivered by `ShowDialogS2CPacket`). These are built from real `ButtonWidget`s, so
>   "View Full Profile" would be an actual button that is pressed, not an item that is clicked.
>
> The first implementation failed **every** job at a flat 2000 ms with `"Summary screen / View Full
> Profile not found within timeout"` while the menu was visibly open. That is a detection bug, not
> a timeout that was too short — lengthening it will not help. Note also that `Screen` keeps
> separate `renderables`, `children`, and `narratables` collections, so a widget that is visible
> is not guaranteed to appear in `children()`.
>
> **Phase A0 below must run first.** Nothing else in this document should be implemented until its
> output tells you which of the two paths applies.

### Phase A0 — identify the screen before writing any parsing

Add this diagnostic and run `/stats <player>` manually with the module enabled. Send the output to
`/fail` (or the client log) verbatim.

```java
Screen s = client.currentScreen;
if (s != null) {
    LOGGER.info("[stats-debug] class={} title={}", s.getClass().getName(), s.getTitle().getString());
    LOGGER.info("[stats-debug] dialog={} handled={} children={}",
        s instanceof DialogScreen<?>, s instanceof HandledScreen<?>, s.children().size());
    if (s instanceof HandledScreen<?> hs) {
        var handler = hs.getScreenHandler();
        LOGGER.info("[stats-debug] syncId={} slots={}", handler.syncId, handler.slots.size());
        for (int i = 0; i < handler.slots.size(); i++) {
            ItemStack stack = handler.getSlot(i).getStack();
            if (!stack.isEmpty()) LOGGER.info("[stats-debug] slot={} name={}", i, stack.getName().getString());
        }
    }
}
```

- Class under `net.minecraft.client.gui.screen.dialog` → **Dialog path**. Ignore every slot and
  lore instruction below; find the `ButtonWidget` whose message is "View Full Profile" and press it,
  and read the stats from the dialog body text rather than from `rawGuiText`.
- A `HandledScreen` subclass → **Container path**, i.e. Phases A–C as written.

### Phase A — `/stats {username}` (container path)

1. Desktop enqueues a username.
2. Module sends chat: `/stats {username}` (template from `/api/v1/status`).
3. Poll `Minecraft.getInstance().screen` every tick until the screen **changes**, then branch on
   its type as in Phase A0. Do not require an exact title; DonutSMP may format it with colour
   codes or the player's rank.
4. Poll screen state, never elapsed time. Keep a 10 second (200 tick) watchdog purely for abort.
   If it expires, include `screen.getClass().getName()` and `screen.getTitle().getString()` in the
   `/fail` error so the desktop log shows what actually opened.

### Phase B — click the View Full Profile item

1. Iterate the container's slots via `screen.getMenu().slots` (Yarn: `getScreenHandler().slots`).
2. For each slot, read `slot.getItem()` and compare the **stripped** display name — and the lore —
   against `"view full profile"`, case-insensitively. Strip `§` colour codes before comparing.
3. Click that slot server-side:

```java
minecraft.gameMode.handleInventoryMouseClick(
    screen.getMenu().containerId, slot.index, 0, ClickType.PICKUP, minecraft.player);
```

4. Do **not** click Back, and do not call `onPress()` on anything.
5. If no slot matches, `/fail` with the screen title plus every non-empty slot's display name.
   That single error tells the desktop exactly how the GUI is laid out.

### Phase C — `{USERNAME} Stats` chest GUI

1. Wait for the container screen to **change** (the menu's `containerId` will differ from Phase A).
   Match on the container changing rather than on the title text. Opening a container and receiving
   its contents are separate packets, so also wait until the slot you need is non-empty rather than
   parsing as soon as the screen appears.
2. Read item **lore** from container slots (no hover needed — lore is on `ItemStack` via `DataComponents.LORE`).
3. Map slots. **These indices are hints, not a contract.** The desktop app matches on the lore
   label text first and searches every slot, so send the lore for *all* slots in `rawGuiText`
   and the app will find the stats even if DonutSMP moves them:

| Slot | Item | Field |
|------|------|-------|
| 10 | Emerald | money |
| 11 | Amethyst shard | shards |
| 12 | Diamond sword | kills |
| 13 | Player head | deaths |
| 14 | Clock | playtimeSeconds |
| 15 | Stone/andesite | placedBlocks |
| 16 | Cobblestone | brokenBlocks |
| 19 | Zombie head | mobsKilled |
| 27 | Red stained glass | close — ignore, use ESC |

4. Parse compact numbers: `2.2B`, `500k`, `1,234.56`. Parse playtime: `5d 3h` → seconds.
5. POST `/api/v1/job/{jobId}/complete` with payload.
6. Press ESC to close GUI (do not click red glass).
7. Wait `commandDelayMs` from status, then poll next job.

## Module settings

Register in `ModuleRegistry` (new class or `BuiltinModules.register()`):

| Setting | Default |
|---------|---------|
| Host | `127.0.0.1` |
| Port | `47891` |
| Command template | `/bal {username}` |
| Summary wait ticks | `200` (10s) |
| Profile wait ticks | `200` (10s) |
| Command delay ms | `1500` |
| Require DonutSMP | on |

Do not use short waits such as 40 ticks (2s). DonutSMP frequently takes several seconds to
open the summary screen, and a short wait makes every job fail before the GUI appears.
The desktop app allows 60s per job, so waiting 10s per phase is safe.

Category: **Misc**. Id: `player-checker-bridge`.

## Implementation notes

- Use `Module.onTick()` state machine; **one job at a time**.
- HTTP client bound to localhost only.
- Never store Minecraft credentials.
- Use `connection().sendChat(...)` for commands (see `DillydaddleModules.java`).
- Parse lore with `AutismItemNbtInspector` or 1.21 component APIs.
- Fail job with descriptive error + `rawGuiText` if button or chest not found within timeout.
- Reference files:
  - `Module.java`, `ModuleRegistry.java`
  - `AutismSelectWorldScreenMixin.java` (widget click)
  - `AutismInventoryHelper.java` (container slots)
  - `AutismDupeRadar.java` / `AutismDiscordLogin.java` (localhost HTTP patterns)

## Failure reporting (important)

The desktop app shows whatever string you send in `/fail` directly in its scan log, and writes
every request body to `%LOCALAPPDATA%\DonutHypixelPlayerComparer\bridge-log.txt`. Make failures
diagnosable:

- Say which phase failed: `"summary screen did not open within 10s"`,
  `"View Full Profile button not found"`, `"stats chest did not open"`.
- Include the screen title and, when a screen was open, the slot lore you did see.
- On `/complete`, always send `rawGuiText` with the lore of every stat slot, even when your own
  parsing succeeded. The desktop app re-parses it and it is the only way to debug wrong numbers.
- Never post `/complete` with all-zero fields and no `rawGuiText`; the app rejects that as
  "no usable stats" because it cannot tell a real new player from a failed parse.

## Acceptance checklist

- [ ] Enable module on DonutSMP → desktop status shows bridge connected
- [ ] Scan 3 usernames → 3× `/stats` → View Full Profile → chest parsed → grid fills Donut columns
- [ ] ESC closes GUI between jobs
- [ ] Fail path returns slot lore in desktop log via `rawGuiText`
- [ ] Desktop `bridge-log.txt` shows a `completion body` line with real lore for each player
