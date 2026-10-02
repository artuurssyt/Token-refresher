#!/usr/bin/env bash
# Install Microsoft's Windows x64 .NET 8 Desktop Runtime into this app's Wine prefix.
# Arch's own dotnet-runtime / wine-mono cannot run this WPF app.
set -euo pipefail

APP_NAME="Localts Account Manager"
DATA_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/localts-account-manager"
PREFIX="${WINEPREFIX:-$DATA_ROOT/wineprefix}"
export WINEPREFIX="$PREFIX"
export WINEARCH="${WINEARCH:-win64}"

RUNTIME_URL="${DOTNET_DESKTOP_RUNTIME_URL:-https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe}"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/localts-account-manager"
INSTALLER="$CACHE/windowsdesktop-runtime-8.0-win-x64.exe"

if ! command -v wine >/dev/null 2>&1; then
  echo "$APP_NAME: wine is not installed. On Arch: sudo pacman -S wine" >&2
  exit 1
fi

mkdir -p "$WINEPREFIX" "$CACHE"
if [[ ! -d "$WINEPREFIX/drive_c" ]]; then
  echo "$APP_NAME: initializing Wine prefix at $WINEPREFIX"
  wineboot --init
fi

fetch() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -L --fail --retry 3 --retry-delay 2 -o "$dest" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget -O "$dest" "$url"
  else
    echo "$APP_NAME: install curl or wget to download the Desktop Runtime." >&2
    exit 1
  fi
}

echo "Wine prefix: $WINEPREFIX"
echo "Downloading .NET 8 Desktop Runtime (Windows x64 installer)..."
echo "URL: $RUNTIME_URL"
fetch "$RUNTIME_URL" "$INSTALLER"

echo "Installing into Wine (quiet). This is NOT Arch's dotnet-runtime package."
wine "$INSTALLER" /install /quiet /norestart

if command -v winetricks >/dev/null 2>&1; then
  echo "Installing Arial via winetricks (helps WPF text under Wine)..."
  winetricks -q arial || echo "winetricks arial failed; you can retry later."
else
  echo "Optional: sudo pacman -S winetricks && WINEPREFIX=\"$WINEPREFIX\" winetricks -q arial"
fi

echo
echo "Done. Start the app with:"
echo "  ./localts-account-manager"
echo "or, after makepkg -si:"
echo "  localts-account-manager"
