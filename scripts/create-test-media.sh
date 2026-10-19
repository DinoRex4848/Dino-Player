#!/usr/bin/env bash
set -euo pipefail

output_dir="${1:-Test Media Ω}"
mkdir -p "$output_dir"

ffmpeg -hide_banner -y \
  -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=12" \
  -f lavfi -i "sine=frequency=220:sample_rate=48000:duration=12" \
  -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=12" \
  -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=12" \
  -f lavfi -i "sine=frequency=550:sample_rate=48000:duration=12" \
  -map 0:v:0 -map 1:a:0 -map 2:a:0 -map 3:a:0 -map 4:a:0 \
  -metadata:s:a:0 title="Game" \
  -metadata:s:a:1 title="Microphone" \
  -metadata:s:a:2 title="Chat" \
  -metadata:s:a:3 title="Music" \
  -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p \
  -c:a aac -b:a 128k -shortest \
  "$output_dir/Four tracks – café.mkv"

ffmpeg -hide_banner -y \
  -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=8" \
  -an -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p \
  "$output_dir/No audio sample.mp4"

printf 'Created test fixtures in %s\n' "$output_dir"
printf 'These validate controls and path handling, not real OBS or 4K performance.\n'
