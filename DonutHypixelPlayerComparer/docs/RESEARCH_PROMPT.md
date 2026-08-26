# Research prompt — DonutSMP `/stats` GUI automation

Copy everything below the line into an AI assistant with web access.

---

I am writing a client-side Fabric mod for **Minecraft 1.21.11** that automates reading another
player's stats on the **DonutSMP** server (`donutsmp.net`), a Paper/Spigot-based survival server.

## What the mod does

It runs `/stats <username>` in chat, waits for the resulting server GUI to open, clicks a
**"View Full Profile"** element, then reads item lore out of the second GUI to extract money,
shards, kills, deaths, playtime, blocks placed/broken, and mobs killed.

## Current failure

Every attempt fails at a flat ~2000 ms with `Summary screen / View Full Profile not found within
timeout`, even though the menu is **visibly opening on screen**. The current implementation scans
`screen.children()` for an `AbstractWidget` / `Button` whose message contains "View Full Profile".

My working theory is that this scan can never match, because a vanilla-protocol server can only
open a **container screen** (`ClientboundOpenScreenPacket` carries a `MenuType` from the vanilla
registry), so "View Full Profile" must be an `ItemStack` in a chest slot rather than a button
widget. I want this confirmed or corrected, and I want the concrete 1.21.11 API details.

## Questions

Please answer each, and **cite sources** (Fabric/Yarn docs, Minecraft Wiki protocol pages, mod
source on GitHub, DonutSMP wiki or videos). Flag anything you are inferring rather than verifying.

1. **Protocol confirmation.** In Minecraft 1.21.x, can a server cause a vanilla client to open
   anything other than a container screen (chest, hopper, anvil, etc.) or the standard
   sign/book/merchant screens? Specifically, is there any way a Paper plugin makes the client show
   a custom `Screen` subclass with real `Button` widgets? I want a definitive yes/no.

2. **What does DonutSMP `/stats <player>` actually open?** Is it a single chest GUI or two
   chained GUIs? What is the window title, what size is it (9x3, 9x6?), and which slot index holds
   "View Full Profile"? Screenshots, videos, or wiki pages showing the actual layout are ideal.
   Also confirm whether the command is `/stats`, `/pstats`, `/profile`, or something else, and
   whether it works on players who are offline.

3. **Exact slot layout of the full profile GUI.** I currently assume: 10 = money, 11 = shards,
   12 = kills, 13 = deaths, 14 = playtime, 15 = placed blocks, 16 = broken blocks, 19 = mobs
   killed. Please verify against real DonutSMP output and correct it. I also need the **exact
   text format of the lore lines** — for example is money rendered as `$2.2B`, `2,200,000,000`, or
   `Money: 2.2B`, and is the value on the same line as the label or the line below it?

4. **Correct 1.21.11 API for clicking a container slot from client code.** Confirm the current
   signature and class names for both Mojmap and Yarn:
   - Mojmap: `minecraft.gameMode.handleInventoryMouseClick(containerId, slotId, button, ClickType.PICKUP, player)`
   - Yarn: `client.interactionManager.clickSlot(syncId, slotIndex, button, SlotActionType.PICKUP, player)`
   Has either changed in 1.21.11? Is there a required delay or packet-ordering constraint after
   the click before the next GUI opens?

5. **Reading lore in 1.21.11.** The item component API changed in 1.20.5+. What is the correct way
   to read lore now — `stack.get(DataComponents.LORE)` returning `ItemLore`, then `.lines()`? And
   the display name via `stack.getHoverName()`? Give a working snippet for 1.21.11 specifically.

6. **Timing and reliability.** How long does a Paper server GUI typically take to open after a
   chat command, and what is the correct way to detect it — polling `client.screen` each tick,
   or hooking `ClientboundOpenScreenPacket` / Fabric's `ScreenEvents.AFTER_INIT`? Note that slots
   may be populated by a *later* `ContainerSetContent` packet than the one that opens the screen,
   so what is the reliable signal that slot contents are fully loaded?

7. **Anti-automation risk.** Does DonutSMP have known rules or detection against client mods that
   send commands and click GUI slots automatically? Is there a rate limit or antispam on `/stats`?
   I am checking accounts I am considering buying, roughly 20 lookups in a few minutes.

## Output format

For each question: a direct answer, the supporting citation, and a confidence level. Where you
give code, target **Minecraft 1.21.11 Fabric** and state whether it is Mojmap or Yarn.
