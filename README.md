# Localts Account Manager

Windows desktop app for importing Microsoft/Minecraft account credentials you are authorized to use, refreshing them, and exporting usernames or tokens. Repo name on GitHub is **Token-refresher**.

You do not need to message the owner to use this. Download or build, then follow the steps below.

## Download

**[Download LocaltsAccountManager.App.exe (Windows x64)](https://github.com/artuurssyt/Token-refresher/releases/latest/download/LocaltsAccountManager.App.exe)**

Also listed on the **[Releases](https://github.com/artuurssyt/Token-refresher/releases/latest)** page. The `.exe` is not in the git repo (it used to be ~148 MB and broke `git push`).

1. Install the **[.NET 8 Desktop Runtime (Windows x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)** if Windows says the app cannot start or a runtime is missing. You want **.NET Desktop Runtime 8**, not the SDK, unless you plan to build from source.
2. Save the exe somewhere writable (for example `Downloads` or a folder you create). Exports are written to an `exports\` folder next to that exe.
3. Double-click `LocaltsAccountManager.App.exe`.
4. If **SmartScreen** or antivirus blocks it, that is normal for an unsigned desktop app you built or downloaded from GitHub. Use **More info → Run anyway** only if you trust this repo. Add an exclusion if your AV quarantines the file.

If the Releases page has no attached exe yet, skip to [Build from source](#build-from-source).

**Requirements:** Windows 10 or 11, 64-bit.

## Build from source

Need the latest code, or there is no Release asset:

1. Install the **[.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)** (includes the Desktop workload used by WPF).
2. Clone and publish a single-file Windows x64 build:

```powershell
git clone https://github.com/artuurssyt/Token-refresher.git
cd Token-refresher

dotnet publish src\LocaltsAccountManager.App\LocaltsAccountManager.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\publish"
```

3. Run `publish\LocaltsAccountManager.App.exe`. A self-contained publish does **not** need the Desktop Runtime installed on the target PC. It is much larger than the framework-dependent Release exe.

Framework-dependent publish (smaller, needs the Desktop Runtime on the PC that runs it):

```powershell
dotnet publish src\LocaltsAccountManager.App\LocaltsAccountManager.App.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -o ".\publish"
```

Do not commit `publish\`, `Done builds\`, or `exports\`. They are gitignored.

**Solution:** `LocaltsAccountManager.sln`

| Project | Role |
|---------|------|
| `src/LocaltsAccountManager.App` | WPF UI (this is what you publish) |
| `src/LocaltsAccountManager.Core` | Models and settings |
| `src/LocaltsAccountManager.Infrastructure` | Import, auth, SQLite, export |
| `src/DonutComparer.Core` | Donut / Hypixel scan library used by the Donut tab |
| `src/LocaltsAccountManager.Phase0` | Optional diagnostics console — you do not need it to run the app |
| `tests/LocaltsAccountManager.Core.Tests` | Unit tests |
| `DonutHypixelPlayerComparer/` | Separate WinForms comparer solution (optional, not the main app) |

```powershell
dotnet build LocaltsAccountManager.sln -c Release
```

## First launch

The window title is **Localts Account Manager**. You should see four tabs: **Batch**, **Pool**, **Accounts**, **Donut**. The status line at the top should say authentication is verified.

**Tokens do not refresh when you open the app.** Opening only loads saved data. Nothing is sent to Microsoft/Minecraft until you click **Start Processing**, **Refresh Pool**, import on the Accounts tab (that import starts a refresh), or you turn on the Pool auto-refresh checkbox yourself.

What is empty vs saved:

- First run on this Windows user: tabs are empty until you import.
- Later runs: Pool and Accounts come back from the local database. The last Batch is reloaded if one exists. Click **Start Processing** only if you want that batch to run again.

Where files live (this Windows user only):

| What | Where |
|------|--------|
| Account database, settings, DPAPI-protected tokens, Localts API key | `%LOCALAPPDATA%\LocaltsAccountManager\` (`accounts.db`, `appsettings.json`, `authentication_profile.json`, `credentials\`) |
| Exports (TXT / ZIP) | `exports\` next to the `.exe`. If that folder cannot be created, the app falls back to `%LOCALAPPDATA%\LocaltsAccountManager\exports\` |

Copying only the exe to another PC or another Windows user starts with an empty library. Secrets do not travel with the exe.

## Credential formats

Import a `.txt` (UTF-8). Blank lines and lines starting with `#` or `===` are skipped. The parser splits `username:token` on the **first** colon.

| What you have | Example (fake) | What to do |
|---------------|----------------|------------|
| Clean MSA refresh (preferred) | `Steve:M.C512_xxx` | **Import TXT** on Batch, Pool, or Accounts |
| Token-only MSA refresh | `M.C512_xxx` | Same Import TXT. Username is filled in after a successful refresh |
| Dump line with a refresh field | `alex@example.com:password \| MCTOKEN: eyJxxx \| REFRESHTOKEN: M.C512_xxx` | Import TXT accepts these, or click **Extract MSA Refresh Tokens** first to write a clean `username:M.C…` file |
| Lines that already say `REFRESHTOKEN: M.C512_xxx` | same as above | Same — Import TXT or Extract |

**Not a refresh token** (these will not refresh an account):

| Looks like | Why it fails |
|------------|----------------|
| Minecraft access JWT (`eyJ…`) or a dump field `MCTOKEN:` only | Short-lived Minecraft session (~24 hours). Use it as a session, or refresh from an `M.C…` token instead |
| `email:password` with no `REFRESHTOKEN:` field | This app does not log in with passwords |
| Random text, empty `REFRESHTOKEN:` | Import marks the line malformed / missing refresh |

MSA refresh tokens usually start with `M.C` (sometimes they contain `MsaArtifacts`). Treat every refresh token as a password.

## How to use (by tab)

### Batch

One-off list. **Import TXT here does not add accounts to Pool.** After import, click **Start Processing**.

| Button | What it does |
|--------|----------------|
| **Import TXT** | Load credentials into this batch only. Then Start Processing |
| **Extract MSA Refresh Tokens** | Read a dump file, write clean `username:M.C…` lines, optionally import that file into this batch |
| **Start Processing** | Refresh each line (Microsoft → Xbox → Minecraft). Slow on purpose (one at a time) to avoid HTTP 429 |
| **Cancel** | Stop the current run |
| **Retry Failed** | Re-run accounts that failed in a retryable way (not “wrong token type”) |
| **Export Usernames** | `successful_usernames.txt` |
| **Export Access Tokens** | Minecraft JWTs: `successful_minecraft_access_tokens.txt` + `.json` |
| **Export Refresh Tokens** | MSA refresh lines `username:M.C…` → `successful_refresh_tokens.txt` |
| **Export Ready ZIP** | One `.txt` per username (Minecraft access JWT only) inside a ZIP. Safe while the batch is still running |
| **Export Failed Records** | Original input lines that failed → `failed_records.txt` (secrets) |
| **Diagnostics** | Reachability check for Microsoft, Minecraft, and Localts |

Right-click a row → **Refresh account** to retry one line. Counters at the top show Total / Pending / Processing / Succeeded / Failed / Rate limited.

When a batch finishes, the app also writes usernames, `errors.txt`, `results.txt`, access-token files, and a ZIP when tokens exist.

### Pool

Persistent library of accounts you want to keep. Import here **does** save them across launches.

| Button | What it does |
|--------|----------------|
| **Import from Localts** | Pull packaged orders from your Localts account (needs API key). Then the app refreshes the pool |
| **Import TXT to Pool** | Same credential formats as Batch, stored on the Pool |
| **Refresh Pool** | Refresh every pool account now. Use this; do not expect it on launch |
| **Cancel** | Stop a pool refresh |
| **Export Pool ZIP** | Ready Minecraft access JWTs, one file per username |
| **Export Pool Refresh Tokens** | Long-lived `username:M.C…` secrets |
| **Export Pool Usernames** | Minecraft names for the pool |
| **Export Meteor SESSION** | Minecraft JWTs only (~24h) for artuurssclient **Accounts → Session** — not the Refresh Token field |
| **Reload list** | Re-read the database |

**Auto-refresh pool in background** is **off by default**. When you turn it on, a timer refreshes tokens that are close to expiry. It still does **not** run the moment you open the app. If the pool “started refreshing by itself,” this checkbox is on (or you clicked Refresh Pool / Import from Localts).

### Localts API key (on the Pool tab)

1. Paste your Localts API key in **LOCALTS API KEY**.
2. Click **Save key**, then **Test**.
3. Status should show the Localts username. Then **Import from Localts** works.

The key is stored with DPAPI under Local AppData. It is not written into the git repo.

### Accounts

Skin/list view of accounts that currently have a **valid stored Minecraft access token**. Expired tokens are hidden.

| Button | What it does |
|--------|----------------|
| **Import TXT** | Import into the active library **and start refreshing immediately** (unlike Batch Import TXT) |
| **Export Meteor SESSION** | Same JWT list as on Pool / Donut |
| **Export ZIP** | Ready access tokens, one `.txt` per username |
| **Export Usernames** | Names from accounts that are currently ready |
| **Refresh list** | Reload the view |

The **⋮** menu on a row copies username, UUID, Minecraft access JWT (Session), or the long-lived OAuth refresh token.

### Donut

Optional Donut SMP / Hypixel scan tab. Refresh/pool features work without it.

Typical path:

1. Refresh accounts on Batch or Pool first so Minecraft usernames exist.
2. **Load from Batch**, **Load from Pool**, or **Load from Accounts** — or paste usernames in the left box.
3. Optional: paste a Hypixel API key and/or Donut API key (`/api` in game), then **Save keys**.
4. Join DonutSMP in **artuurssclient** with **PlayerCheckerBridge** enabled on this machine (localhost bridge).
5. **Start scan** / **Stop**. **Export CSV** when finished.

Other buttons: **Export Meteor SESSION**, **Export username:uuid:token**, **Copy username**, **Copy access token** (JWT for Session, not Refresh Token).

## Exports

Default folder: `exports\` next to the exe (then Local AppData if that path is not writable). Meteor/session exports ask you where to save.

| Export | Typical file | Contents |
|--------|----------------|----------|
| Batch usernames | `successful_usernames.txt` | One Minecraft/provided name per line |
| Batch access tokens | `successful_minecraft_access_tokens.txt` (+ `.json`) | `username:eyJ…` Minecraft JWTs (~24h) |
| Batch refresh tokens | `successful_refresh_tokens.txt` | `username:M.C…` long-lived secrets |
| Ready / library / pool ZIP | `access_tokens_*.zip` or `pool_access_tokens_*.zip` | One `.txt` per username; file body is the Minecraft JWT only |
| Failed records | `failed_records.txt` | Original import lines that failed (secrets) |
| Batch errors / results | `errors.txt`, `results.txt` | Written when a batch finishes |
| Extract MSA | `msa_refresh_extracted_*.txt` | Clean `username:M.C…` |
| Pool refresh tokens | `pool_refresh_tokens_*.txt` | Pool `username:M.C…` |
| Pool / library usernames | `pool_usernames_*.txt`, `library_usernames_*.txt` | Names only |
| Meteor SESSION | `meteor_sessions_*.txt` | Raw Minecraft JWTs, one per line |
| Detailed session | `session_lines_*.txt` | `username:uuid:accessToken` |

Do not upload any of these files. Do not attach them to GitHub issues.

## Privacy / security

- **Refresh tokens (`M.C…`) are secrets.** Anyone with one can refresh that Microsoft/Minecraft login until it is revoked. Do not paste them in issues, Discord, or screenshots.
- **Minecraft access JWTs (`eyJ…`) are session secrets** (~24 hours). Still do not share them.
- Tokens and the Localts API key are stored with **Windows DPAPI for the current Windows user**. Another Windows account or another PC cannot decrypt `%LOCALAPPDATA%\LocaltsAccountManager\credentials\`.
- Do not zip and share `exports\`, `accounts.db`, `appsettings.json`, or the LocalAppData folder.
- This app is for accounts **you are authorized to use**.

## FAQ

**SmartScreen / “Windows protected your PC”**  
Unsigned exe. More info → Run anyway if you intended to download this repo’s build.

**Missing .NET runtime / the window never opens**  
Install [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). If you published `--self-contained true`, you should not need that runtime. A crash before the window usually shows a message box; if the exe was quarantined, restore it from antivirus.

**Nothing happens when I open the app**  
That is expected. Tabs load; tokens do not refresh by themselves. Import a TXT, then **Start Processing** (Batch) or **Refresh Pool**.

**The pool started refreshing as soon as I opened the app**  
Current builds do **not** auto-refresh on launch. Auto-refresh is an opt-in checkbox on Pool and uses a background timer only. If you still see an immediate refresh, you clicked **Refresh Pool** or **Import from Localts**, or an older install had auto-refresh on (newer builds turn that old default off once).

**Why is processing so slow?**  
Minecraft login is rate-limited. The app runs about one account at a time and backs off on HTTP 429. Wait; use **Retry Failed** after rate limits clear. Do not start many copies of the exe.

**Expired MSA grant / `invalid_grant` / Reauthentication required**  
The refresh token was revoked, expired, or is no longer accepted. A password dump line without `REFRESHTOKEN: M.C…` will not fix this. Get a new MSA refresh token and import again.

**Dump file vs clean token file**  
A dump is a long line with email, password, `MCTOKEN`, `REFRESHTOKEN`, etc. Import TXT can pull `REFRESHTOKEN: M.C…` out of those lines. **Extract MSA Refresh Tokens** writes a clean file you can re-import. A clean file is just `Steve:M.C512_xxx` per line.

**I imported `eyJ…` / MCTOKEN and nothing useful happens**  
That is a Minecraft access JWT, not an MSA refresh token. Use **Extract MSA Refresh Tokens** on the original dump, or import `M.C…` lines.

**Where are my tokens stored?**  
Encrypted blobs under `%LOCALAPPDATA%\LocaltsAccountManager\credentials\`, indexed by `accounts.db`. Exports you click are plaintext files under `exports\` (or the save dialog location).

**I copied the folder to another PC and accounts vanished**  
DPAPI is per Windows user/machine. The database may copy but secrets will not decrypt. Re-import refresh tokens on the new PC.

**Can I share refresh tokens or upload AppData?**  
No.

**There is no exe on GitHub / git clone has no `Done builds`**  
Binaries are gitignored (GitHub file-size limits). Use [Releases](https://github.com/artuurssyt/Token-refresher/releases/latest) or [Build from source](#build-from-source).

**Antivirus says the exe is malware**  
Common false positive on unsigned .NET single-file publishes. Check the hash against the Release asset you downloaded. Build from source if you do not want to trust a binary.

**Top of the window says BLOCKED / Phase 0**  
A normal first launch writes a working `authentication_profile.json` and should show that auth is verified. If you see BLOCKED, close the app, delete `%LOCALAPPDATA%\LocaltsAccountManager\authentication_profile.json` if it is empty or damaged, and start again. You do not need to run the Phase0 project.

**Accounts Import TXT started refreshing by itself**  
Yes. On the Accounts tab, Import TXT starts processing immediately. On the Batch tab it does not — you click **Start Processing**.

**Donut scan does nothing / bridge errors**  
Account refresh does not need Donut. For scans: join DonutSMP, enable PlayerCheckerBridge in artuurssclient on this PC, then Start scan. Hypixel/Donut API keys are optional extras on that tab.

**Linux or macOS?**  
No. This is a Windows WPF app (x64).

**How do I update?**  
Download the new exe from Releases (or publish again). Your library stays in `%LOCALAPPDATA%\LocaltsAccountManager\` as long as you are the same Windows user. You can delete the old exe.

**Where do I get a Localts API key?**  
From your account on [localts.store](https://localts.store). Paste it on the Pool tab. This repo does not issue keys.
