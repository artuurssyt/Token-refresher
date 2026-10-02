# Localts Account Manager on Arch Linux

This is a **native Avalonia** desktop GUI (`linux-x64`), not Wine and not WPF. The same Batch / Pool / Accounts / Donut tabs as the Windows app.

The older **v1.1.0-linux-arch** tarball was a Wine wrapper around the Windows exe. That release is kept for history. Use **v1.2.0-linux-arch** (or newer) for the native app.

## Quick start (extract the release tarball)

```bash
sudo pacman -S --needed xorg-xwayland libx11 libice libsm libxext libxrandr libxi libxcursor \
  libxrender libxkbcommon fontconfig freetype2 harfbuzz icu mesa ttf-dejavu

mkdir -p ~/localts-account-manager
cd ~/localts-account-manager
curl -L -o localts-account-manager-linux-arch.tar.gz \
  https://github.com/artuurssyt/Token-refresher/releases/download/v1.2.0-linux-arch/localts-account-manager-linux-arch.tar.gz
tar xf localts-account-manager-linux-arch.tar.gz
cd localts-account-manager-linux-arch
chmod +x localts-account-manager
./localts-account-manager
```

The binary is **self-contained**. You do **not** need Arch `dotnet-runtime`, Wine, or `wine-mono`.

Optional desktop entry after extract:

```bash
mkdir -p ~/.local/share/applications
cp localts-account-manager.desktop ~/.local/share/applications/
sed -i "s|^Exec=.*|Exec=$PWD/localts-account-manager|" ~/.local/share/applications/localts-account-manager.desktop
```

## Install with makepkg

From the extracted directory:

```bash
sudo pacman -S --needed base-devel
makepkg -si
localts-account-manager
```

## Where data lives

| What | Location |
|------|----------|
| Account database, settings, encrypted tokens, Localts API key | `~/.local/share/LocaltsAccountManager/` (`accounts.db`, `appsettings.json`, `authentication_profile.json`, `credentials/`) |
| Donut/Hypixel API keys | `~/.local/share/DonutHypixelPlayerComparer/` |
| Exports | `exports/` next to the binary if writable; otherwise `~/.local/share/LocaltsAccountManager/exports/` |

Override the data root with `XDG_DATA_HOME` (then the app uses `$XDG_DATA_HOME/LocaltsAccountManager`).

Linux secrets are AES-GCM files under `credentials/` (mode `0600`) plus a `store.key` file. They are **not** Windows DPAPI blobs and will not decrypt if you copy a Windows `%LOCALAPPDATA%\LocaltsAccountManager\credentials` folder onto Linux.

Copying only the binary to another user or machine starts with an empty library.

## Launch notes

- First launch does **not** refresh tokens. Import a TXT, then click **Start Processing** (Batch) or **Refresh Pool**.
- **Auto-refresh pool in background** is off by default and does not run on open.
- On Wayland, the app uses X11 via XWayland (`xorg-xwayland`). A pure Wayland session without XWayland may fail to open a window.
- If fonts look missing, install `ttf-dejavu` (or another TTF package) and `fontconfig`.

## Packages (native GUI)

Self-contained binary still needs these **OS** libraries:

```bash
sudo pacman -S --needed libx11 libice libsm libxext libxrandr libxi libxcursor libxrender \
  libxkbcommon fontconfig freetype2 harfbuzz icu mesa ttf-dejavu xorg-xwayland
```

| Package | Why |
|---------|-----|
| `libx11` `libice` `libsm` `libxext` `libxrandr` `libxi` `libxcursor` `libxrender` | X11 / windowing |
| `libxkbcommon` | Keyboard |
| `fontconfig` `freetype2` `harfbuzz` `ttf-dejavu` | Text |
| `icu` | .NET globalization |
| `mesa` | Skia / GL fallback |
| `xorg-xwayland` | Window on Wayland compositors |

You do **not** need `wine`, `winetricks`, or `dotnet-runtime`.

## Verify the binary hash

The tarball includes `SHA256SUMS`. Compare it to the GitHub Release asset.

```
SHA-256  localts-account-manager
84116333ba8dfea439e03f1d92d8fadb25ee180d031171add61a232ef22bfcf0

SHA-256  localts-account-manager-linux-arch.tar.gz
a50c3a6cf760bbf5bdb6547cbef4f1a31a7dea03e69453c824c8639785613548
```

## Build from source (Linux)

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

```bash
git clone https://github.com/artuurssyt/Token-refresher.git
cd Token-refresher
dotnet publish src/LocaltsAccountManager.Desktop/LocaltsAccountManager.Desktop.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./publish-linux
chmod +x publish-linux/LocaltsAccountManager.Desktop
./publish-linux/LocaltsAccountManager.Desktop
```
