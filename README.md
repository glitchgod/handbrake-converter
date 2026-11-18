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
- **Never auto-convert**: an Exclude button on any movie, series, season or episode keeps it out of Auto-convert (you can still convert it by hand). The list is shown on the Libraries tab.
- **Preview first**: the Overview has a button that shows how many files would be converted, how much space they take and an estimated saving, without touching anything.
- **Control the result**: video encoder, quality, speed, resolution cap, H.264 compatibility level, audio, subtitles, container and HDR handling.
- **Clear audio choices**: each option is labelled by channel layout: same as source (keeps Dolby Atmos, TrueHD and DTS-HD untouched), AAC or AC3 as stereo (2 channels), AAC or AC3 as 5.1 surround (6 channels), or AAC as 7.1 (8 channels). A smaller source is never upmixed.
- **Audio languages**: optionally keep only the audio languages you list (3-letter codes such as `eng, jpn`), which drops extra languages and commentary tracks to save space. Tracks with no language tag are kept, and if nothing matches nothing is removed.
- **Interlaced video**: DVDs and TV recordings are detected automatically and only their combed frames are cleaned up (HandBrake's comb detection and decomb). Progressive video is never touched. Can be set to always or never.
- **Tidy subtitles**: keep only the languages you list (3-letter codes, e.g. `eng`) plus forced tracks, so a file with dozens of subtitle tracks ends up with a few. Text subtitles are converted to SRT (or copied as-is where the container allows, and then the fonts that styled subtitles need are copied too), which avoids the unusual formats that make some players fall back to transcoding. Picture subtitles (PGS/VobSub) are kept in MKV and dropped in MP4.
- **Separate subtitle files**: optionally write each text subtitle track as its own `.srt` file next to the video (`Name.eng.srt`, `Name.spa.forced.srt`), either as well as inside the file or instead of it. This is the most reliable setup for players such as Roku. Existing subtitle files are never overwritten. In keep-original mode, subtitle files that sit next to the original are copied to the converted copy's name.
- **Fix subtitles without re-encoding**: when a file was already converted, changing only the subtitle settings re-writes just the subtitle tracks with a fast stream copy. Video and audio are untouched, and the result is verified before it replaces the file.
- **Run when it suits you**: weekly time windows, blackout dates, low CPU priority, and automatic pausing while someone is watching.
- **Parallel conversions**: optionally convert up to 4 files at the same time (default 1; only worth raising on a server with plenty of spare CPU and fast disks). The Overview shows every running conversion, and the free-space check counts all of them.
- **Replace or keep**: overwrite the original in place (Jellyfin keeps the same item, metadata and watch history) or write a separate "- HandBrake" copy next to it. Optionally switch the file type too (MKV to MP4 or back). Jellyfin then sees a new item, so the plugin copies the watched status, resume position, favourites, rating, collections and "date added" across to it (best effort).
- **Linked files**: a hard-linked file (one file with two names, for example shared with a download folder) can be replaced 1-for-1: the converted video is written into that same file, so every name shrinks and no space is wasted. A real copy of the old file is held for the usual days. Other options are to replace only the library's file, or to leave linked files alone. The converted file keeps the original's permissions, and a hung encode is stopped after a number of minutes of silence that you set (default 30). Temporary files left by a crash or power cut are cleaned up at the next start, and a half-finished in-place overwrite is put back from its backup. Stream links (.strm) and disc images or folders are skipped with a clear note.
- **HDR and Dolby Vision**: by default HDR and Dolby Vision videos are left alone, because a normal re-encode would wash out the picture. You can set them to keep HDR (needs an H.265 or AV1 encoder with a 10-bit mode, which the plugin switches to automatically), convert them anyway, or skip them. Dolby Vision can't be kept by any re-encode, so it is only converted if you choose to ignore HDR.
- **Deleted while converting**: if you delete a video, or remove it from the Jellyfin library, while it is converting, the encode stops within a few seconds and everything it made (temporary video, half-written subtitle files) is removed. The job shows as Skipped with a note and the file is never written back, so a deleted video can't come back.
- **Streaming-friendly**: conversions pause while anyone is watching (music playback can be ignored, and you choose whether a paused stream still counts), and a finished file is never swapped in while that item is being played. The finished file waits and is swapped in once playback stops.
- **Stay safe**: the result must finish cleanly, be smaller, and match the original's length, audio, subtitles, chapters and HDR before anything is swapped.
- **Undo mistakes**: replaced originals are held for a number of days, then deleted, or restored with one click. When a held original is deleted, the subtitle files that sat next to it (`Name.eng.srt`) go with it, but only in languages the converted video still has; other subtitle files are never touched.
- **Never repeat work**: a file already converted with the same video settings is skipped and marked finished instead of being re-encoded.
- **See what's happening**: live progress, time-left estimates, and a full history of what was converted, skipped or failed and why.
- **Pick settings that work**: test which encoders your server really supports, and see which formats your clients actually transcode, read from Jellyfin's own logs.

## 🔎 What happens when...

| Situation | What the plugin does |
|---|---|
| The converted file isn't smaller | Keeps the original, marks it finished and notes why. Manual jobs that keep the original, or burn subtitles in, are exempt. |
| The video is being played when the encode finishes | Waits with the finished file, then swaps once playback stops. |
| Someone starts watching mid-encode | Freezes the encode (and resumes it afterwards) if "pause while streaming" is on. This never counts as a hung encode. |
| HandBrake prints nothing for the stall limit (default 30 min) | Stops it, deletes the partial file, records a failure. The original is untouched. Time spent paused doesn't count. |
| The file is a hard link or symlink | By default the converted video is written into the same file, so every name shrinks (see Linked files). A held copy of the old file is kept so it can be restored. |
| Jellyfin or the server restarts mid-encode | The job goes back to the queue. Leftover temp files are cleaned at the next start, and a half-finished in-place overwrite is put back from its backup. |
| The disk is nearly full | Waits instead of starting. With several conversions running, the check adds up what all of them need. |
| The same file fails repeatedly | After a few failures it is left alone until you retry it from History. |
| A job is cancelled or "Stop automatic & start now" is used | The partial file is deleted. Cancelled manual jobs stay cancelled; an interrupted automatic file is picked up again later. |
| A file is HDR, Dolby Vision, a `.strm` stream link or a disc image/folder | Skipped (HDR is configurable) with a plain-language reason in History. |
| You delete the video or remove it from the library mid-encode | Stops and cleans up (see above). |
| You delete a held original | Its own covered-language subtitle files go with it. Restoring the original keeps them. |

## 🧭 The plugin page, tab by tab

| Tab | What it's for |
|---|---|
| **Overview** | Master on/off switch, Pause/Continue, a Preview button for what would be converted, what is converting right now (and whether it is a manual job or an automatic library), estimated time for everything waiting, space saved so far, and HandBrake status. |
| **Libraries (auto-convert)** | One card per library: turn auto-convert on, optionally give it its own settings. Each card states in plain words what it will do right now. Below the cards is the list of items excluded from auto-convert. |
| **Manual convert** | Search or browse your libraries, tick movies, episodes, seasons or series, choose settings, and add them to the queue. Can start immediately outside the schedule, or force a re-convert. |
| **Queue** | One ordered list ("Order of work"). Manual jobs and each auto-convert library are rows in the same list, so you can put jobs in any order: manual, then a library, then more manual jobs, then another library. A library row shows the library name, the file it is on and how many are waiting, and expands to show the next files. Reorder (Top / ▲ / ▼), stop the running job, remove several at once. **Stop automatic & start now** ends the running automatic encode so your manual job begins immediately; the interrupted file is picked up again afterwards. |
| **History** | Every converted, skipped and failed file with size before and after, time taken and the reason. Retry failures from here. |
| **Default settings** | The shared video, audio, subtitle and file settings used by libraries and manual jobs unless overridden. |
| **Original files** | What happens to originals: replace or keep, only-if-smaller, minimum saving, length / audio / subtitle / chapter checks, and how long replaced originals are held. Restore or delete held files here. |
| **Schedule & performance** | Allowed days and hours, blackout dates, pause while streaming, low priority, how many conversions run at once, the stall limit, minimum file size and free-space guard. |
| **HandBrake & tools** | Shows whether HandBrakeCLI was found and its version, lets you set a custom path, and adds extra HandBrake options for advanced use. |
| **Encoder test** | Lists the encoders your HandBrake offers and runs a short sample encode to prove each one works. Test one, test all, or retest. Encoders that fail are marked "not supported" in the settings dropdowns. |

## 🧰 Set up HandBrake on your Jellyfin server

The plugin drives **HandBrakeCLI** on the same machine that runs Jellyfin. Subtitle clean-up also uses **ffmpeg** (already present with Jellyfin). These steps are for Ubuntu/Debian.

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
8. **Choose settings.** In **Default settings** pick an encoder, quality and resolution. A good starting point is x265 with quality 22, audio on "Same as source", and subtitles set to keep your language (`eng`) plus forced tracks.
9. **Try one file first.** In **Manual convert** queue a single episode, watch it in the **Queue**, then check **History** and play the result before turning on auto-convert for a whole library.

> Running Jellyfin in Docker? HandBrakeCLI has to exist inside that container. Use an image or mod that includes it, since packages installed by hand are lost when the container is rebuilt.

## 📦 Install the plugin

1. In Jellyfin open **Dashboard → Plugins → Repositories → +**.
2. Name: `HandBrake Converter`
3. URL: `https://raw.githubusercontent.com/glitchgod/handbrake-converter/main/manifest.json`
4. Open **Catalog**, install **HandBrake Converter**, then restart Jellyfin from the Dashboard.
5. Open **HandBrake Converter** in the dashboard sidebar.

**Requirements:** Jellyfin 12.1, HandBrakeCLI and ffmpeg on the server. The plugin doesn't bundle either.

## 🧪 Good to know

- **Dates:** the converted file gets a new modification time, so Jellyfin sees it as changed and refreshes it. Permissions and group are copied from the original.
- **Attachments:** fonts and other attachments are only kept in MKV. MP4 can't hold them, and picture subtitles are dropped there too.
- **Burned-in subtitles:** if you choose to burn subtitles in, the result is exempt from the "must be smaller" guard, since that is a deliberate choice.
- **Recommended starting point on a slow server:** leave parallel conversions at 1, keep "pause while streaming" on, and use a schedule window overnight.
- **Nothing is deleted without a copy:** unless you set the hold period to 0 days, a replaced original is kept in a holding folder and can be restored from **Original files**.
- **Troubleshooting:** if nothing happens, check the Overview (is the master switch on, is it paused, is it outside the schedule window), then **HandBrake & tools** (is HandBrakeCLI found), then **History** (a skipped or failed file always says why).

## 🛡️ How a file is protected

1. HandBrake must finish cleanly and produce a real file.
2. The result must be smaller (and save at least your minimum, if set).
3. Length, audio, subtitles, chapters and HDR are compared with the original.
4. If the file is being played, the swap waits.
5. If the original was deleted or removed from the library meanwhile, everything is discarded instead.
6. Only then is the original replaced, and a copy is held for the days you chose.

No telemetry and no outbound network calls. The plugin only reads your media and Jellyfin's local transcode logs.

<details>
<summary><b>Updating</b></summary>

Jellyfin shows an update under **Dashboard → Plugins** when a newer release is published. Click **Update**, then restart Jellyfin. If the new version doesn't appear, remove and re-add the repository URL to refresh Jellyfin's cached list.

</details>

<details>
<summary><b>Building from source</b></summary>

Needs the .NET 10 SDK. Run `./build.sh`; the plugin DLL and `meta.json` land in `dist/`. Releases are built automatically by the GitHub Action in `.github/workflows/release.yml` (it runs `make-release.sh`) whenever `VERSION` is bumped.

</details>

## 📄 License

GPL-3.0. See [LICENSE](LICENSE).
