# Token Refresher / Localts Account Manager

Windows desktop app for refreshing and managing Minecraft accounts you are authorized to use (Localts-style imports, pool, batch processing, optional DonutSMP bridge tab).

**Repo:** https://github.com/artuurssyt/Token-refresher

## Requirements

- Windows 10/11 x64
- For development: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Recipients of a **self-contained** publish do **not** need the SDK installed

## Clone

```powershell
git clone https://github.com/artuurssyt/Token-refresher.git
cd Token-refresher
dotnet build LocaltsAccountManager.sln -c Release
```

## Publish (do not commit the output)

```powershell
dotnet publish src\LocaltsAccountManager.App\LocaltsAccountManager.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o ".\publish"
```

Distribute the `publish\` folder only. Keep `publish\`, `Done builds\`, and `exports\` out of git (already gitignored).

## Data locations

| Data | Where |
|------|--------|
| Account DB, DPAPI credentials, settings | `%LOCALAPPDATA%\LocaltsAccountManager\` |
| Exports (tokens, ZIPs) | `exports\` next to the `.exe` |

Copying only the exe to another PC starts with an empty library — credentials do not travel with the app folder.

## Optional: Donut tab

The DonutSMP scan tab expects **artuurssclient** with PlayerCheckerBridge enabled on the same machine (localhost bridge). Account refresh/pool features work without it.

## Security

- Never commit `exports\`, access-token ZIPs, or refresh-token TXT files
- Treat leaked refresh tokens as compromised and rotate
- Do not upload Local AppData credential stores to the repo
