<div align="center">

# 🎞️ HandBrake Converter

**A Jellyfin plugin that re-encodes your library in the background with HandBrake.**

![Jellyfin](https://img.shields.io/badge/Jellyfin-12.1-00A4DC?logo=jellyfin&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![HandBrake](https://img.shields.io/badge/HandBrakeCLI-required-F2A900)
![License](https://img.shields.io/github/license/glitchgod/handbrake-converter)
![Release](https://img.shields.io/github/v/release/glitchgod/handbrake-converter)

</div>

Choose which libraries to convert, pick a format, set when it may run, and the plugin works through your videos quietly in the background. An original is only replaced after the new file passes safety checks, and the old one can be held for a few days so you can undo it. Everything is managed from the Jellyfin dashboard.

> Unofficial community plugin. Not affiliated with or endorsed by Jellyfin or HandBrake.

## ✨ Highlights

- **Auto-convert or by hand**: switch libraries on to convert on a schedule, or queue single movies, episodes, seasons or series.
- **Quick apply**: pick the Jellyfin app you watch on (Roku, Fire TV, Apple TV, Xbox, browsers and more, with HD and 4K models listed separately) and the settings are set to what it plays directly.
- **Keeps the original look**: no cropping, 10-bit stays 10-bit, Atmos and DTS:X are copied, styled subtitles keep their fonts and positions.
- **Safe by default**: the result must be smaller and match the original's length, audio, subtitles, chapters and HDR before anything is swapped, and replaced originals are held so you can undo.
- **Stays out of the way**: time windows, low CPU priority, and pausing while anyone is watching.
- **Learns from your playback**: suggestions in every dropdown come from Jellyfin's own transcode reasons and logs.

## 📦 Install

1. In Jellyfin open **Dashboard → Plugins → Repositories → +**.
2. Name: `HandBrake Converter`
3. URL: `https://raw.githubusercontent.com/glitchgod/handbrake-converter/main/manifest.json`
4. Open **Catalog**, install **HandBrake Converter**, then restart Jellyfin.
5. Open **HandBrake Converter** in the dashboard sidebar.

**Requirements:** Jellyfin 12.1, plus HandBrakeCLI and ffmpeg on the server (not bundled; ffmpeg comes with Jellyfin).

<details>
<summary><b>🧰 Set up HandBrake on your server (Ubuntu/Debian)</b></summary>

1. **Install it** on the Jellyfin server:
   ```bash
   sudo apt update
   sudo apt install handbrake-cli
   ```
2. **Check it runs:** `HandBrakeCLI --version` should print a version number.
3. **Let Jellyfin's user write to your media.** Converted files are written next to the originals, so the service user (normally `jellyfin`) needs read and write access. Quick test:
   ```bash
   sudo -u jellyfin touch /path/to/your/media/test.tmp && sudo rm /path/to/your/media/test.tmp
   ```
   If that errors, fix the folder permissions or group first.
4. **Leave free space.** An encode writes a temporary file beside the original before swapping it in; the plugin waits if free space is below the limit you set.

> Running Jellyfin in Docker? HandBrakeCLI has to exist inside that container. Use an image or mod that includes it, since packages installed by hand are lost when the container is rebuilt.

</details>

## 🚀 Quick start

1. **Tools → HandBrake**: confirm it says HandBrake is installed. If not, enter the full path (for example `/usr/bin/HandBrakeCLI`).
2. **Tools → Encoder test**: press **Test all encoders**. Encoders that fail are marked "not supported" in the dropdowns.
3. **Settings → Conversion settings**: use **Quick apply** for your main player, or pick an encoder, quality and resolution yourself (x265 at quality 22 is a good start).
4. **Convert titles**: queue one episode, watch it in **Status → Queue**, then check **History** and play the result.
5. **Libraries**: once you're happy, turn on Auto-convert for a whole library.

## 📖 More detail

<details>
<summary><b>🎬 Converting: what you can choose</b></summary>

- **Auto-convert libraries**: switch conversion on per library, each with its own settings or the shared ones.
- **Convert by hand**: browse to a movie, an episode, a season or a whole series and queue just those, with settings for that job only.
- **Never auto-convert**: an Exclude button on any movie, series, season or episode keeps it out of Auto-convert (you can still convert it by hand). The list is on the Libraries tab.
- **Preview first**: Tools → Preview Auto-convert shows how many files would be converted, the estimated saving and the order, without touching anything.
- **Replace or keep**: overwrite the original in place (Jellyfin keeps the same item, metadata and watch history) or write a separate "- HandBrake" copy next to it. Switching the file type (MKV ↔ MP4) makes a new item, so watched status, resume position, favourites, rating, collections and "date added" are copied across (best effort).
- **Never repeat work**: a file already converted with the same settings is skipped. Auto-convert also skips videos already in the chosen format (an H.265 file with an H.265 encoder) unless they are over the resolution cap.
- **Parallel conversions**: up to 4 at once (default 1; only worth raising on a server with plenty of spare CPU and fast disks).

</details>

<details>
<summary><b>🖼️ Video</b></summary>

- Encoder, quality, speed, resolution cap and H.264 compatibility level are all yours to set.
- **Never cropped.**
- **10-bit** sources stay 10-bit when the encoder has a 10-bit version (H.265, AV1), or are converted to 8-bit if you choose that for older devices.
- **Interlaced video** (DVDs, TV recordings) is detected automatically and only combed frames are cleaned up; progressive video is never touched. Can be set to always or never.
- **HDR and Dolby Vision** are left alone by default, because a normal re-encode would wash out the picture. You can keep HDR (needs an H.265 or AV1 10-bit encoder, switched to automatically), convert anyway, or skip. Dolby Vision can't survive any re-encode.

</details>

<details>
<summary><b>🔊 Audio</b></summary>

- Options are labelled by channel layout: same as source (keeps Dolby Atmos, TrueHD and DTS-HD untouched), AAC or AC3 stereo, AAC or AC3 5.1, or AAC 7.1.
- Tracks already in the right format are copied, nothing is upmixed, and Atmos / DTS:X tracks are always copied when the container can hold them.
- **Languages**: optionally keep only the languages you list (`eng, jpn`), dropping extras and commentary. Untagged tracks are kept, and if nothing matches nothing is removed.

</details>

<details>
<summary><b>💬 Subtitles</b></summary>

- **Three settings.** *Subtitles*: keep, burn one in, or remove. *Languages to keep*: e.g. `eng` (forced subtitles for foreign-language lines are always kept). *Look*:
  - **Original look**: styled subtitles (ASS/SSA: fonts, colours, positions) stay exactly as made; plain ones become SRT and follow your Jellyfin subtitle settings.
  - **Original look, plus a simple text copy**: also adds a plain version of the styled ones.
  - **Simple text only**: every subtitle follows your Jellyfin settings.
- **MP4** can't hold styled subtitles or SRT, so they are written as files next to the video (`.ass`, `.srt`); picture subtitles stay as they are (inside MKV, or a `.sup` next to an MP4).
- **Checked every time**: each kept track is compared with the original (canvas, every style's font, size, colours and position, every line and its timing, or the exact pictures) and the original is kept if anything changed. SRT is written without font tags, so it's drawn at your Jellyfin subtitle size.
- **Subtitle-only changes** re-write just the subtitle tracks with a fast stream copy; video and audio aren't re-encoded.
- **Tools → Repair giant subtitles** finds files older versions left with oversized subtitles and fixes them without re-encoding. **Tools → Check a title** shows any title's tracks, styles and fonts, and what your settings would do to them.

</details>

<details>
<summary><b>🎯 Suggestions, Quick apply and the settings check</b></summary>

- **Quick apply** sets video, audio, subtitles and container to what a chosen Jellyfin app plays directly, based on [Jellyfin's codec support list](https://jellyfin.org/docs/general/clients/codec-support/). Roku, Android TV / Fire TV, Apple TV and Xbox are split into HD/older and 4K models. Quality, speed, languages and originals are left alone; nothing is saved until you press Save.
- **One suggestion per dropdown**, aiming for files that play directly on as many players as possible. When your own playback points somewhere (Jellyfin's transcode reason for each play, plus its ffmpeg logs) the choice is marked "(suggested from your playback)"; hover to see why.
- **Tools → Update suggestions** reads Jellyfin's newest transcode logs on demand (100 by default) with a progress bar and moves the marks.
- **Settings check**: clashing settings (HDR with an H.264 encoder, a schedule with no time window...) are shown in bold red with a note above Save. Nothing is changed for you.

</details>

<details>
<summary><b>⏰ Scheduling and streaming</b></summary>

- Weekly time windows, blackout dates and low CPU priority.
- Conversions pause while anyone is watching (music can be ignored, and you choose whether a paused stream counts).
- A finished file is never swapped in while that title is playing; it waits until playback stops.
- A hung encode is stopped after a number of minutes of silence you set (default 30).

</details>

<details>
<summary><b>🛡️ How a file is protected</b></summary>

1. HandBrake must finish cleanly and produce a real file.
2. The result must be smaller (and save at least your minimum, if set).
3. Length, audio, subtitles, chapters and HDR are compared with the original, and every kept subtitle track must keep its styles, sizes, lines and timing.
4. If the file is being played, the swap waits.
5. If the original was deleted or removed from the library meanwhile, everything is discarded instead.
6. Only then is the original replaced, and a copy is held for the days you chose (restore with one click from Settings → Original files).

Also: hard-linked files can be replaced 1-for-1 so no space is wasted (or left alone), the converted file keeps the original's permissions, temporary files from a crash are cleaned up at the next start, and a half-finished in-place overwrite is restored from its backup. When a held original is deleted, subtitle files next to it (`Name.eng.srt`) go with it only in languages the converted video still has.

No telemetry and no outbound network calls. The plugin only reads your media, Jellyfin's local transcode logs and its own playback sessions.

</details>

<details>
<summary><b>🔎 What happens when...</b></summary>

| Situation | What the plugin does |
|---|---|
| The converted file isn't smaller | Keeps the original, marks it finished and notes why. Manual jobs that keep the original, or burn subtitles in, are exempt. |
| The video is playing when the encode finishes | Waits with the finished file, then swaps once playback stops. |
| Someone starts watching mid-encode | Freezes the encode and resumes it afterwards (if "pause while streaming" is on). Never counts as a hung encode. |
| HandBrake prints nothing for the stall limit | Stops it, deletes the partial file, records a failure. The original is untouched. |
| The file is a hard link or symlink | By default the converted video is written into the same file, so every name shrinks. A copy of the old file is held. |
| Jellyfin or the server restarts mid-encode | The job goes back to the queue; leftovers are cleaned at the next start. |
| The disk is nearly full | Waits instead of starting. |
| The same file fails repeatedly | After a few failures it's left alone until you retry it from History. |
| A job is cancelled, or "Stop automatic & start now" is used | The partial file is deleted. An interrupted automatic file is picked up again later. |
| The file is HDR, Dolby Vision, a `.strm` link or a disc image/folder | Skipped (HDR is configurable) with a plain reason in History. |
| You delete the video or remove it from the library mid-encode | Stops within seconds and cleans up; the file never comes back. |

</details>

<details>
<summary><b>🧭 The plugin page, tab by tab</b></summary>

Five tabs. Longer tabs are split into sections that fold open and closed.

| Tab | What it's for |
|---|---|
| **Status** | On/off switch, Pause, what is converting now, time left and space saved. **Queue**: manual jobs and auto-convert library rows in one ordered list; reorder, stop, remove. **History**: every converted, skipped and failed file with the reason; retry failures. |
| **Libraries** | One card per library: turn auto-convert on and optionally give it its own settings. Excluded items are listed below. |
| **Convert titles** | Browse or search, tick titles, choose settings, add them to the queue. |
| **Settings** | **Conversion settings** (Quick apply, video, audio, subtitles, file), **Original files** (checks, hold time, restore), **Schedule & performance** (time windows, pausing, priority, conversions at once). |
| **Tools** | HandBrake check, Encoder test, Check a title, Update suggestions, Repair giant subtitles, Preview Auto-convert, Auto-convert housekeeping. |

</details>

<details>
<summary><b>🧪 Good to know</b></summary>

- **Dates:** the converted file gets a new modification time, so Jellyfin refreshes it. Permissions and group are copied from the original.
- **Attachments:** fonts are only kept in MKV. Styled subtitles next to an MP4 use the player's fonts, and VobSub picture subtitles are dropped there.
- **Burned-in subtitles** are exempt from the "must be smaller" check, since that's a deliberate choice.
- **Slow server?** Leave parallel conversions at 1, keep "pause while streaming" on, and use an overnight time window.
- **Held originals** end in `.held`, so Jellyfin never lists them as extra episodes or movies.
- **Troubleshooting:** if nothing happens, check **Status** (switch on? paused? outside the time window?), then **Tools** (HandBrakeCLI found?), then **History** (a skipped or failed file always says why).

</details>

<details>
<summary><b>⬆️ Updating</b></summary>

Jellyfin shows an update under **Dashboard → Plugins** when a newer release is published. Click **Update**, then restart Jellyfin. If the new version doesn't appear, remove and re-add the repository URL to refresh Jellyfin's cached list.

</details>

<details>
<summary><b>🔧 Building from source</b></summary>

Needs the .NET 10 SDK. Run `./build.sh`; the plugin DLL and `meta.json` land in `dist/`. Releases are built automatically by the GitHub Action in `.github/workflows/release.yml` (it runs `make-release.sh`) whenever `VERSION` is bumped.

</details>

## 📄 License

GPL-3.0. See [LICENSE](LICENSE).
