#!/usr/bin/env bash
# Builds dist/Sillon_<version>.zip (plugin dll + meta.json) from a Release publish.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="0.8.0.0"
target_abi="12.2.0.0"
guid="1daed904-1b05-483c-8544-16cb6dcced54"

publish="$root/dist/publish"
stage="$root/dist/stage"
rm -rf "$publish" "$stage"
mkdir -p "$stage"

dotnet publish "$root/Jellyfin.Plugin.Sillon/Jellyfin.Plugin.Sillon.csproj" -c Release -o "$publish" "$@"

cp "$publish/Jellyfin.Plugin.Sillon.dll" "$stage/"
cat > "$stage/meta.json" <<JSON
{
  "guid": "$guid",
  "name": "Sillon",
  "version": "$version",
  "targetAbi": "$target_abi",
  "framework": "net10.0",
  "owner": "Overlabing",
  "category": "Music",
  "overview": "Music requests for the Sillon app.",
  "description": "Connects Jellyfin to Lidarr so music requests made from the Sillon app can be fulfilled automatically.",
  "changelog": "A search also finds albums by the title of a song, through MusicBrainz, and a new endpoint lists an album's tracks. Every same-title release group is offered, with what tells them apart; singles are hidden by default. A waiting request tells whether it is still searching or not found yet. A failed request recovers on its own once its album is imported. Requesting an album monitors its artist; cancelling a request unmonitors the album.",
  "status": "Active",
  "autoUpdate": false
}
JSON

zip="$root/dist/Sillon_${version}.zip"
rm -f "$zip"
(cd "$stage" && zip -q -j "$zip" Jellyfin.Plugin.Sillon.dll meta.json)
echo "OK: $zip"
