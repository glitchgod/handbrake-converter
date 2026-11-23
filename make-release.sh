#!/usr/bin/env bash
# CI helper: builds the plugin zip and manifest.json. Needs VERSION and REPO (owner/name) in the environment.
set -euo pipefail
cd "$(dirname "$0")"
NAME="HandBrake Converter"
CHANGELOG="$(head -n1 CHANGELOG.txt | tr -d '"\r')"
ZIP="HandBrakeConverter_${VERSION}.zip"
rm -rf out dist
dotnet publish Jellyfin.Plugin.HandBrakeConverter -c Release -o out -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION"
mkdir -p dist
cp out/Jellyfin.Plugin.HandBrakeConverter.dll dist/
cat > dist/meta.json <<JSON
{
  "category": "General",
  "changelog": "$CHANGELOG",
  "description": "Converts your library in the background with HandBrakeCLI: automatically on a schedule or title by title, keeping the original look.",
  "guid": "6b1f3e52-8c0a-4d57-9a41-2f7d5c8e1b90",
  "name": "$NAME",
  "overview": "Converts your library in the background with HandBrakeCLI: automatically on a schedule or title by title, keeping the original look.",
  "owner": "HandBrake Converter",
  "targetAbi": "12.1.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["Jellyfin.Plugin.HandBrakeConverter.dll"]
}
JSON
(cd dist && zip -q -r "../$ZIP" .)
SUM="$(md5sum "$ZIP" | cut -d' ' -f1)"
cat > manifest.json <<JSON
[
  {
    "guid": "6b1f3e52-8c0a-4d57-9a41-2f7d5c8e1b90",
    "name": "$NAME",
    "description": "Converts your library in the background with HandBrakeCLI: automatically on a schedule or title by title, keeping the original look.",
    "overview": "Converts your library in the background with HandBrakeCLI: automatically on a schedule or title by title, keeping the original look.",
    "owner": "HandBrake Converter",
    "category": "General",
    "versions": [
      {
        "version": "$VERSION",
        "changelog": "$CHANGELOG",
        "targetAbi": "12.1.0.0",
        "sourceUrl": "https://github.com/${REPO}/releases/download/v${VERSION}/${ZIP}",
        "checksum": "$SUM",
        "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      }
    ]
  }
]
JSON
echo "Built $ZIP"
