# Localts Account Manager on Arch Linux

Native Avalonia GUI (not Wine). Extract this directory and run:

```bash
sudo pacman -S --needed libx11 libice libsm libxext libxrandr libxi libxcursor \
  libxrender libxkbcommon fontconfig freetype2 harfbuzz icu mesa ttf-dejavu xorg-xwayland
chmod +x localts-account-manager
./localts-account-manager
```

Data: `~/.local/share/LocaltsAccountManager/`

Tokens do not refresh on launch. Full notes: https://github.com/artuurssyt/Token-refresher/blob/main/docs/ARCH.md
