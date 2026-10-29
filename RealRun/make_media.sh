#!/usr/bin/env bash
# Generates the made-up test videos used by the end-to-end run (720p, AAC audio, English + Japanese SRT tracks).
set -euo pipefail
R=/tmp/e2e; rm -rf "$R/media" "$R/frames"
mkdir -p "$R/media/Anime/TestShow/Season 1" "$R/media/Movies/Test Movie (2020)" "$R/frames"; cd "$R"
printf '1\n00:00:00,000 --> 00:00:12,000\nENGLISH SUBTITLE TRACK\n' > eng.srt
printf '1\n00:00:00,000 --> 00:00:12,000\nJAPANESE TRACK TEXT\n' > jpn.srt
mk() { out="$1"; shift
  args=(-y -f lavfi -i "testsrc2=size=1280x720:rate=24:duration=12" -f lavfi -i "sine=frequency=440:duration=12")
  maps=(-map 0:v -map 1:a); i=2
  for s in "$@"; do args+=(-i "$s"); maps+=(-map $i:s); i=$((i+1)); done
  meta=(); n=0
  for s in "$@"; do l=$(basename "$s" .srt); meta+=(-metadata:s:s:$n language=$l -metadata:s:s:$n title=$l); n=$((n+1)); done
  ffmpeg -loglevel error "${args[@]}" "${maps[@]}" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -c:s srt "${meta[@]}" "$out"; }
mk "media/Anime/TestShow/Season 1/Test Show - S01E01 - Pilot.mkv" eng.srt jpn.srt
mk "media/Anime/TestShow/Season 1/Test Show - S01E02 - Second.mkv" eng.srt jpn.srt
mk "media/Movies/Test Movie (2020)/Test Movie (2020).mkv" eng.srt
cp "media/Anime/TestShow/Season 1/Test Show - S01E01 - Pilot.mkv" preset-src.mkv
