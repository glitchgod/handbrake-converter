#!/usr/bin/env bash
# Builds the plugin and packages it for Jellyfin 12.1. Needs the .NET 10 SDK and internet access to nuget.org.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="1.0.0.0"
NAME="HandBrake Converter"
rm -rf out dist
dotnet publish Jellyfin.Plugin.HandBrakeConverter -c Release -o out
mkdir -p dist
cp out/Jellyfin.Plugin.HandBrakeConverter.dll dist/
cat > dist/meta.json <<JSON
{
  "category": "General",
  "changelog": "Initial release.",
  "description": "Re-encodes library videos with HandBrake on a schedule.",
  "guid": "6b1f3e52-8c0a-4d57-9a41-2f7d5c8e1b90",
  "name": "$NAME",
  "overview": "Re-encodes library videos with HandBrake on a schedule.",
  "owner": "glitch",
  "targetAbi": "12.1.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["Jellyfin.Plugin.HandBrakeConverter.dll"]
}
JSON
(cd dist && zip -q -r "../HandBrakeConverter_${VERSION}.zip" .)
echo
echo "Built HandBrakeConverter_${VERSION}.zip"
echo "Install:"
echo "  sudo mkdir -p \"/var/lib/jellyfin/plugins/${NAME}_${VERSION}\""
echo "  sudo cp dist/* \"/var/lib/jellyfin/plugins/${NAME}_${VERSION}/\""
echo "  sudo systemctl restart jellyfin"
