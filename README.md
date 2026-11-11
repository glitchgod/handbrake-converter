<div align="center">

# 🎞️ HandBrake Converter

**A Jellyfin plugin that re-encodes your library in the background with HandBrake.**

![Jellyfin](https://img.shields.io/badge/Jellyfin-12.1-00A4DC?logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![HandBrake](https://img.shields.io/badge/HandBrakeCLI-required-F2A900)
![License](https://img.shields.io/github/license/glitchgod/handbrake-converter)
![Release](https://img.shields.io/github/v/release/glitchgod/handbrake-converter)

</div>

Choose which libraries to convert, pick a format, set when it may run, and the plugin works through your videos quietly in the background. An original is only replaced after the new file passes safety checks, and the old one can be held for a few days so you can undo it.

> Unofficial community plugin. Not affiliated with or endorsed by Jellyfin or HandBrake.

## 🎯 Goal

Save disk space and reduce transcoding on your server without babysitting a converter. Everything is managed from the Jellyfin dashboard, uses the HandBrake already on your server, and never replaces a file it can't verify.

## ✨ What it can do

- **Auto-convert libraries**: switch conversion on per library, each with its own settings or the shared defaults.
- **Convert by hand**: browse to one movie, an episode, a season or a whole series and queue just those.
- **Control the result**: video encoder, quality, speed, resolution cap, H.264 compatibility level, audio (same as source, or AAC/AC3 as stereo or 5.1 surround), subtitles, container and HDR handling.
- **Run when it suits you**: weekly time windows, blackout dates, low CPU priority, and automatic pausing while someone is watching.
- **Replace or keep**: overwrite the original in place (Jellyfin keeps the same item, metadata and watch history) or write a separate "- HandBrake" copy next to it. Optionally switch the file type too (MKV to MP4 or back). Jellyfin then sees a new item, so the plugin copies the watched status, resume position, favourites, rating, collections and "date added" across to it (best effort).
- **Stay safe**: the result must finish cleanly, be smaller, and match the original's length, audio, subtitles, chapters and HDR before anything is swapped.
- **Undo mistakes**: replaced originals are held for a number of days, then deleted, or restored with one click.
- **Never repeat work**: a file already converted with the same settings is skipped and marked finished instead of being re-encoded.
- **See what's happening**: live progress, time-left estimates, and a full history of what was converted, skipped or failed and why.
- **Pick settings that work**: test which encoders your server really supports, and see which formats your clients actually transcode, read from Jellyfin's own logs.

## 🧭 The plugin page, tab by tab

| Tab | What it's for |
|---|---|
| **Overview** | Master on/off switch, Pause/Continue, what is converting right now, estimated time for everything waiting, space saved so far, and HandBrake status. |
| **Libraries (auto-convert)** | One card per library: turn auto-convert on, optionally give it its own settings. Each card states in plain words what it will do right now. |
| **Manual convert** | Search or browse your libraries, tick movies, episodes, seasons or series, choose settings, and add them to the queue. Can start immediately outside the schedule, or force a re-convert. |
| **Queue** | What is converting and what is waiting, each with title, progress and target format. Reorder (Top / ▲ / ▼), stop the running job, remove several at once. |
| **History** | Every converted, skipped and failed file with size before and after, time taken and the reason. Retry failures from here. |
| **Default settings** | The shared video, audio, subtitle and file settings used by libraries and manual jobs unless overridden. |
| **Original files** | What happens to originals: replace or keep, only-if-smaller, minimum saving, length / audio / subtitle / chapter checks, and how long replaced originals are held. Restore or delete held files here. |
| **Schedule & performance** | Allowed days and hours, blackout dates, pause while streaming, low priority, minimum file size and free-space guard. |
| **HandBrake & tools** | Shows whether HandBrakeCLI was found and its version, lets you set a custom path, and adds extra HandBrake options for advanced use. |
| **Encoder test** | Lists the encoders your HandBrake offers and runs a short sample encode to prove each one works. Test one, test all, or retest. Encoders that fail are marked "not supported" in the settings dropdowns. |

## 🧰 Set up HandBrake on your Jellyfin server

The plugin drives **HandBrakeCLI** on the same machine that runs Jellyfin. These steps are for Ubuntu/Debian.

1. **Install it.** On the Jellyfin server:
   ```bash
   sudo apt update
   sudo apt install handbrake-cli
   ```
2. **Check it runs.**
   ```bash
   HandBrakeCLI --version
   ```
   You should see a version number.
3. **Let Jellyfin's user reach your media.** The plugin runs as Jellyfin's service user (normally `jellyfin`), so that user needs read and **write** access to the media folders, because converted files are written next to the originals. Quick test:
   ```bash
   sudo -u jellyfin touch /path/to/your/media/test.tmp && sudo rm /path/to/your/media/test.tmp
   ```
   If that errors, fix the folder permissions or group first.
4. **Make sure there's free space.** An encode writes a temporary file beside the original before swapping it in. The plugin waits if free space is below the limit you set.
5. **Install the plugin** (see below) and restart Jellyfin.
6. **Confirm detection.** Open the plugin, go to **HandBrake & tools**. It should say HandBrake is installed with the version. If not, type the full path (for example `/usr/bin/HandBrakeCLI`) into the path box.
7. **Test your encoders.** Open **Encoder test** and press **Test all encoders**. Pick only encoders that pass. CPU encoders (x264, x265, SVT-AV1) work everywhere; NVENC, QSV and VAAPI need matching hardware and drivers on the server.
8. **Choose settings.** In **Default settings** pick an encoder, quality and resolution. A good starting point is x265 with quality 22 and audio/subtitles on "Same as source".
9. **Try one file first.** In **Manual convert** queue a single episode, watch it in the **Queue**, then check **History** and play the result before turning on auto-convert for a whole library.

> Running Jellyfin in Docker? HandBrakeCLI has to exist inside that container. Use an image or mod that includes it, since packages installed by hand are lost when the container is rebuilt.

## 📦 Install the plugin

1. In Jellyfin open **Dashboard → Plugins → Repositories → +**.
2. Name: `HandBrake Converter`
3. URL: `https://raw.githubusercontent.com/glitchgod/handbrake-converter/main/manifest.json`
4. Open **Catalog**, install **HandBrake Converter**, then restart Jellyfin from the Dashboard.
5. Open **HandBrake Converter** in the dashboard sidebar.

**Requirements:** Jellyfin 12.1 and HandBrakeCLI on the server. The plugin doesn't bundle HandBrake or ffmpeg.

## 🛡️ How a file is protected

1. HandBrake must finish cleanly and produce a real file.
2. The result must be smaller (and save at least your minimum, if set).
3. Length, audio, subtitles, chapters and HDR are compared with the original.
4. If the file is being played, the swap waits.
5. Only then is the original replaced, and a copy is held for the days you chose.

No telemetry and no outbound network calls. The plugin only reads your media and Jellyfin's local transcode logs.

<details>
<summary><b>Updating</b></summary>

Jellyfin shows an update under **Dashboard → Plugins** when a newer release is published. Click **Update**, then restart Jellyfin. If the new version doesn't appear, remove and re-add the repository URL to refresh Jellyfin's cached list.

</details>

<details>
<summary><b>Building from source</b></summary>

Needs the .NET 10 SDK. Run `./build.sh`; the plugin DLL and `meta.json` land in `dist/`. Releases are built automatically by the GitHub Action in `.github/workflows/release.yml` whenever `VERSION` is bumped.

</details>

## 📄 License

GPL-3.0. See [LICENSE](LICENSE).
