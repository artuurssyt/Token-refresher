# Localts Account Manager on Arch Linux

This is **not** a native Linux GUI. Localts Account Manager is a **.NET 8 WPF** desktop app. WPF does not run on GTK, Qt, or Wayland. The supported Arch path is **Wine + the Windows x64 .NET 8 Desktop Runtime** installed *inside* a Wine prefix.

Arch packages named `dotnet-runtime`, `dotnet-runtime-8.0`, or `wine-mono` will **not** start this GUI.

- Native `dotnet-runtime` is a Linux runtime. It cannot load `Microsoft.WindowsDesktop.App` / WPF.
- `wine-mono` is Wine's stand-in for the old .NET *Framework*, not .NET 8 WPF.

`dotnet publish -r linux-x64` on the App project produces a Linux host that still requires `Microsoft.WindowsDesktop.App`. That binary is not shipped; it would fail immediately on Arch.

Phase0 is a small Windows diagnostics console that uses DPAPI credential storage. It is not a Linux rewrite of the app and is not included here.

## Quick start (extract the release tarball)

```bash
sudo pacman -S wine winetricks curl

mkdir -p ~/localts-account-manager
cd ~/localts-account-manager
curl -L -o localts-account-manager-linux-arch.tar.gz \
  https://github.com/artuurssyt/Token-refresher/releases/download/v1.1.0-linux-arch/localts-account-manager-linux-arch.tar.gz
tar xf localts-account-manager-linux-arch.tar.gz
cd localts-account-manager-linux-arch
chmod +x localts-account-manager install-dotnet-desktop-runtime.sh

# Once per machine: install Microsoft's Windows Desktop Runtime into this app's Wine prefix
./install-dotnet-desktop-runtime.sh

# Run (does not auto-refresh tokens)
./localts-account-manager
```

Optional desktop entry after extract (so the app appears in your menu):

```bash
mkdir -p ~/.local/share/applications
cp localts-account-manager.desktop ~/.local/share/applications/
# If you did not install with makepkg, point Exec at the extracted launcher:
sed -i "s|^Exec=.*|Exec=$PWD/localts-account-manager|" ~/.local/share/applications/localts-account-manager.desktop
```

## Install with makepkg

From the same extracted directory (PKGBUILD lives next to the exe):

```bash
sudo pacman -S --needed base-devel wine winetricks curl
makepkg -si
localts-account-manager-setup
localts-account-manager
```

`makepkg -si` copies the Windows exe to `/usr/share/localts-account-manager/` and a launcher to `/usr/bin/localts-account-manager`. You still must run `localts-account-manager-setup` (or `./install-dotnet-desktop-runtime.sh`) so Wine has the **Windows** .NET 8 Desktop Runtime.

## What the setup script does

`install-dotnet-desktop-runtime.sh` (installed as `localts-account-manager-setup`):

1. Creates `~/.local/share/localts-account-manager/wineprefix` (`WINEARCH=win64`) if needed.
2. Downloads Microsoft's [Windows x64 .NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (`https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe`).
3. Runs that installer with `wine`.
4. If `winetricks` is present, installs Arial (WPF text is often blank without a core font).

Override the installer URL if you already have a specific patch build:

```bash
export DOTNET_DESKTOP_RUNTIME_URL='https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.21/windowsdesktop-runtime-8.0.21-win-x64.exe'
./install-dotnet-desktop-runtime.sh
```

Manual equivalent:

```bash
export WINEPREFIX="$HOME/.local/share/localts-account-manager/wineprefix"
export WINEARCH=win64
wineboot --init
curl -L -o /tmp/windowsdesktop-runtime-8.0-win-x64.exe \
  https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe
wine /tmp/windowsdesktop-runtime-8.0-win-x64.exe /install /quiet /norestart
winetricks -q arial
```

## Where data lives

| What | Location under Wine |
|------|---------------------|
| Account database, settings, DPAPI-protected tokens, Localts API key | `$WINEPREFIX/drive_c/users/$USER/AppData/Local/LocaltsAccountManager/` |
| Default Wine prefix used by the launcher | `~/.local/share/localts-account-manager/wineprefix` |
| Exports | `exports\` next to the exe if that folder is writable; otherwise the LocalAppData path above |

This is **not** `~/.local/share/LocaltsAccountManager` on the Linux side. DPAPI blobs are scoped to the Wine "Windows user". Copying only the exe to another machine starts with an empty library.

To wipe the Wine prefix and start over:

```bash
rm -rf ~/.local/share/localts-account-manager/wineprefix
./install-dotnet-desktop-runtime.sh
```

## Launch notes

- First launch does **not** refresh tokens. Import a TXT, then click **Start Processing** (Batch) or **Refresh Pool**.
- WPF on Wine needs a 64-bit prefix (`WINEARCH=win64`). Do not create a 32-bit prefix.
- If the window is missing or text is invisible, install `winetricks` and run `WINEPREFIX=... winetricks -q arial`.
- SmartScreen does not apply on Linux. Unsigned exe warnings are a Windows-only note.

## Verify the exe hash

The tarball includes `SHA256SUMS`. Compare it to the Windows release asset; they are the same framework-dependent `LocaltsAccountManager.App.exe`.

```
SHA-256  LocaltsAccountManager.App.exe
8dbb07049d0dc78f1e03302fae2356e269b5b3e437ee78ec7c4329836e491f66
```
