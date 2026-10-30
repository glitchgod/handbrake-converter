<div align="center">

# 🎞️ HandBrake Converter

**A Jellyfin plugin that shrinks your library in the background with HandBrake.**

![Jellyfin](https://img.shields.io/badge/Jellyfin-12.1-00A4DC?logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![HandBrake](https://img.shields.io/badge/HandBrakeCLI-required-F2A900)
![License](https://img.shields.io/github/license/glitchgod/handbrake-converter)
![Release](https://img.shields.io/github/v/release/glitchgod/handbrake-converter)

</div>

Pick your libraries, set a schedule, and the plugin converts videos to a smaller, more compatible format while you sleep. Originals are only replaced after the result passes safety checks, and a copy is held for a few days in case you change your mind.

> Unofficial community plugin. Not affiliated with or endorsed by Jellyfin or HandBrake.

## 🎯 Goal

Save disk space and cut transcoding without babysitting a converter. Everything runs inside the Jellyfin dashboard, uses the HandBrake you already have, and never touches a file it can't verify.

## ✨ Features

- **Auto-convert per library**: turn it on for Movies, off for Home Videos, etc.
- **Manual convert**: browse a movie, season or series and convert just those, with their own settings.
- **Queue**: live progress, reorder (top / up / down), remove several at once, stop the running job, etc.
- **Schedule**: run windows per weekday, pause while someone is watching, low CPU priority, etc.
- **Safety checks**: length, audio tracks, subtitles, chapters and HDR must match before an original is replaced.
- **Held originals**: replaced files wait in a hidden folder for a set number of days, then restore or delete.
- **History**: every converted, skipped and failed file with space saved, time taken and the reason, etc.
- **HDR aware**: leave HDR / Dolby Vision alone, or keep it with a 10-bit encode.
- **Smart suggestions**: dropdowns mark what your clients actually transcode, read from Jellyfin's own logs.

## ⚙️ Options

| Setting | Examples |
|---|---|
| Video | Encoder (x264, x265, AV1, hardware if found), quality, speed, max resolution, etc. |
| Audio | Copy, AAC, AC3, bitrate, etc. |
| Subtitles | Keep, remove, burn in one track, etc. |
| File | Keep container, MKV, MP4, output suffix, etc. |
| Originals | Replace, keep, only if smaller, minimum saving, days held, etc. |
| Performance | Time windows, pause while streaming, low priority, etc. |

Most dropdowns have a **Same as source** choice, and each library can use its own settings.

## 📦 Install

1. In Jellyfin open **Dashboard → Plugins → Repositories → +**.
2. Name: `HandBrake Converter`
3. URL: `https://raw.githubusercontent.com/glitchgod/handbrake-converter/main/manifest.json`
4. Open **Catalog**, install **HandBrake Converter**, then **Dashboard → Restart**.
5. Open **HandBrake Converter** in the dashboard sidebar.

**Requirements:** Jellyfin 12.1 and HandBrakeCLI on the server (`sudo apt install handbrake-cli` on Ubuntu/Debian). The plugin doesn't bundle HandBrake or ffmpeg.

## 🛡️ How it stays safe

1. HandBrake must finish cleanly and produce a real file.
2. The result must be smaller (and save at least your minimum, if set).
3. Length, audio, subtitles, chapters and HDR are compared with the original.
4. It waits if the file is being played.
5. Only then is the original swapped out, and a copy is held for the number of days you choose.

No telemetry and no outbound network calls. It only reads your media and Jellyfin's local transcode logs.

<details>
<summary><b>Tabs in the plugin page</b></summary>

Overview, Libraries, Manual convert, Queue, History, Default settings, Original files, Schedule & performance, HandBrake & tools.

</details>

<details>
<summary><b>Updating</b></summary>

Jellyfin shows an update under **Dashboard → Plugins** whenever a newer version is released here. Click **Update**, then restart Jellyfin.

</details>

<details>
<summary><b>Building from source</b></summary>

Needs the .NET 10 SDK. Run `./build.sh`; the plugin DLL and `meta.json` land in `dist/`. Releases are built automatically by the GitHub Action in `.github/workflows/release.yml` whenever `VERSION` is bumped.

</details>

## 📄 License

GPL-3.0. See [LICENSE](LICENSE).
