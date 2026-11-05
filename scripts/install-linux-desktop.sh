#!/usr/bin/env bash
set -euo pipefail

# Bundled next to the executable; a source checkout can pass its build/publish directory.
app_dir="$(cd -- "${1:-$(dirname -- "${BASH_SOURCE[0]}")}" && pwd)"
data_dir="${XDG_DATA_HOME:-$HOME/.local/share}"
app_id="DinoPlayer.MP"
app_executable="$app_dir/Dino Player"
if [[ ! -x "$app_executable" ]]; then app_executable="$app_dir/DinoPlayer.MP"; fi

if [[ ! -x "$app_executable" || ! -f "$app_dir/DinoPlayer.MP.png" ]]; then
  printf 'Expected the Dino Player executable and DinoPlayer.MP.png in %s\n' "$app_dir" >&2
  exit 1
fi

# Desktop entries have string escaping as well as Exec argument quoting.
desktop_string() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "$value"
}

desktop_command() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//\$/\\\$}"
  value="${value//\`/\\\`}"
  value="${value//%/%%}"
  desktop_string "\"$value\""
}

applications_dir="$data_dir/applications"
icons_dir="$data_dir/icons/hicolor/512x512/apps"
mkdir -p -- "$applications_dir" "$icons_dir"
install -m 644 -- "$app_dir/DinoPlayer.MP.png" "$icons_dir/$app_id.png"

desktop_file="$applications_dir/$app_id.desktop"
cat > "$desktop_file" <<EOF
[Desktop Entry]
Type=Application
Name=Dino Player
Comment=Play videos and export clips
Exec=/bin/sh -c $(desktop_command 'app=$1; shift; exec "$app" "$@"') DinoPlayer.MP $(desktop_command "$app_executable") %f
Icon=$app_id
MimeType=video/mp4;video/x-matroska;video/webm;video/quicktime;video/x-msvideo;video/mp2t;video/x-m4v;
Terminal=false
Categories=AudioVideo;Video;Player;
StartupNotify=true
StartupWMClass=$app_id
EOF
chmod 644 -- "$desktop_file"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$applications_dir"
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1 && [[ -f "$data_dir/icons/hicolor/index.theme" ]]; then
  gtk-update-icon-cache -f -t "$data_dir/icons/hicolor"
fi
printf 'Installed Dino Player launcher: %s\n' "$desktop_file"
