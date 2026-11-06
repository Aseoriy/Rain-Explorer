#!/usr/bin/env bash
# Installs Hyprland (apt) and builds Noctalia (from source, into ~/.local). Ubuntu 26.04.
set -euo pipefail

echo "==> Installing Hyprland, helpers and Noctalia build dependencies (needs sudo)"
sudo apt update
sudo apt install -y \
  hyprland xdg-desktop-portal-hyprland hyprpolkitagent hyprlock hypridle hyprpaper \
  kitty wofi grim slurp wl-clipboard brightnessctl playerctl pipewire wireplumber \
  upower ddcutil gnome-keyring \
  git meson g++ just \
  libwayland-dev wayland-protocols \
  libegl-dev libgles-dev \
  libfreetype-dev libfontconfig-dev \
  libcairo2-dev libpango1.0-dev libharfbuzz-dev \
  libxkbcommon-dev libglib2.0-dev \
  libsecret-1-dev libsodium-dev \
  libsdbus-c++-dev libpipewire-0.3-dev libwireplumber-0.5-dev \
  libpam0g-dev libpolkit-agent-1-dev libpolkit-gobject-1-dev \
  libcurl4-gnutls-dev libwebp-dev libjxl-dev libsndfile1-dev librsvg2-dev \
  libqalculate-dev libxml2-dev \
  libmd4c-dev libtomlplusplus-dev libical-dev \
  nlohmann-json3-dev libstb-dev \
  libjemalloc-dev

echo "==> Building Noctalia into ~/.local"
mkdir -p "$HOME/src"
if [ -d "$HOME/src/noctalia/.git" ]; then
  git -C "$HOME/src/noctalia" pull --ff-only
else
  git clone https://github.com/noctalia-dev/noctalia.git "$HOME/src/noctalia"
fi
cd "$HOME/src/noctalia"
just configure release "$HOME/.local"
just build release
just install release

echo
echo "Done. Log out, pick 'Hyprland' at the login screen (gear/session menu), and log in."
echo "Then start Noctalia with: noctalia   (add 'exec-once = noctalia' to ~/.config/hypr/hyprland.conf to autostart)"
