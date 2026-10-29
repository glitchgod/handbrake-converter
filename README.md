# HandBrake Converter (Jellyfin 12.1 plugin)

Adds a **HandBrake Converter** entry to the admin dashboard sidebar (like the Reports plugin) with these tabs:

- **Overview**: status, master switch, a HandBrake check (installed? which version?), retry limit, history tools.
- **Libraries (auto-convert)**: per library, an **Auto-convert** switch and optional settings specific to that library (e.g. anime at 720p). Libraries without their own settings use Default settings.
- **Manual convert**: browse a library (movie, series > season > episode) and convert specific titles on demand with their own settings, including **burning in one subtitle track**. A series or season expands to all its episodes. Jobs go to the Queue and run before Auto-convert.
- **Queue**: live progress, reorder queued jobs (top / up / down), tick several jobs and remove them at once, stop the running one, clear finished.
- **History**: every converted, skipped and failed file with size saved, time taken and the reason; Retry for failures.
- **HDR**: HDR and Dolby Vision videos are left alone by default; "keep" re-encodes them in 10-bit with the HDR information and verifies it survived.
- **Result checks** before an original is replaced: length, audio tracks, subtitle tracks, chapters and HDR must match the source.
- **Encoders**: the encoder list comes from the installed HandBrake (hardware encoders only show when found), with a Test button that runs a 2-second sample encode. An encoder HandBrake doesn't have is reported once instead of failing every file.
- **Suggested marks from your playback** (Encoder, Resolution and Audio dropdowns): reads Jellyfin's own `FFmpeg.Transcode-*` / `FFmpeg.Remux-*` logs (a short summary of each is kept in `transcodes.json`, because Jellyfin deletes old logs) and marks "(suggested from your playback)" on the encoder, resolution and audio choice that match what clients are actually served.
- **Pause / Continue** (Overview status box): freezes the running encode and blocks new starts until you continue. Survives restarts.
- **Default settings**: video, audio, subtitles, container and what happens to the original. Most dropdowns have a **Same as source** choice (resolution, audio, subtitles, container).
- **Original files**: everything about looking after originals, in order: before converting (skip small files, free space), after converting (only-if-smaller, minimum saving, same-length check), and replaced originals (days held, holding folder, restore list).
- **Schedule & performance**: optional time windows (per weekday, midnight crossing, skipped date ranges; off = convert any time), what to do when a window ends mid-file, pause while someone is watching, low priority.

Why a sidebar page and not the 3-dot menu on a title card: Jellyfin has no plugin hook for the item context menu; it would need injected JavaScript in the web client, which breaks on every web update. The Convert tab does the same job (per-title options) without that.

## Build and install (on the Ubuntu Jellyfin box)

```bash
sudo apt install dotnet-sdk-10.0 zip handbrake-cli
./build.sh
# then run the three install lines it prints, e.g.
sudo mkdir -p "/var/lib/jellyfin/plugins/HandBrake Converter_1.0.0.0"
sudo cp dist/* "/var/lib/jellyfin/plugins/HandBrake Converter_1.0.0.0/"
sudo systemctl restart jellyfin
```

If your server is not exactly 12.1.x, change the `Jellyfin.Controller` / `Jellyfin.Model` versions in the csproj and `targetAbi` in `build.yaml` and `build.sh`, otherwise Jellyfin marks the plugin NotSupported.

## HandBrake

The plugin does not install HandBrake. Install it once on the server with `sudo apt install handbrake-cli` (update it with your normal `apt upgrade`). The Overview tab shows whether HandBrakeCLI was found, its version and path, and has a **Check again** button. If HandBrakeCLI lives somewhere unusual, set its full path on the same tab.

## Behaviour

- Loop guard: a state database (plugin data folder, `state.json`) records every source and output path. A converted file is never auto-queued again, even if Jellyfin rescans it under a new item id.
- Original file: keep = converted copy saved as `Name - HandBrake.mkv` next to the original (Jellyfin shows it as a version). Replace = original replaced; encodes write to a `.hbtmp` file first.
- Checks before an original is touched (all must pass, otherwise the result is discarded and the original stays): HandBrake exit 0; **size guard** (result must be smaller, optionally by N %; exempt: manual jobs that keep the original or burn subtitles); **duration check** with Jellyfin's own ffprobe (within max(3 s, 1 %)); and **free space** (file size + spare GB, checked before the encode starts, on the library and holding drives).
- Replaced originals go to a **holding folder** for N days (default 7, one global setting on the Original files tab; 0 = delete at once) instead of being deleted. Default folder: a hidden `.hb-originals` folder inside the video's own library folder, with an empty `.ignore` file so Jellyfin skips it. It is on the same drive as the video, so holding an original is an instant rename (no copying, nothing written to the system disk). You can set a custom folder, but a different drive means a full copy of every original. Restore puts the file back beside the converted one as `Name - Original.ext`; expired ones are purged automatically.
- Queue jobs run before auto-processing. Jobs persist across restarts (`jobs.json`).
- Replacing a file someone is watching: with "delete original" on, the swap never happens while that exact item is open in a player (direct-play clients re-read the file by path). The finished temp file waits and the swap completes when they stop. Status shows "Finishing".
- When replacing, the output keeps the original's exact name and container (MKV stays MKV, MP4/M4V stay MP4), whatever the container setting says, so Jellyfin keeps watch history. Only formats that can't hold the result (AVI, TS, ...) change extension to the profile's container, which makes Jellyfin see a new item.
- Auto-process backlog is cached: the library is re-queried only after Jellyfin reports library changes (once they settle for 20 s), when settings change, or every 10 minutes. Playback start/stop events wake the scheduler immediately (suspend/resume within about a second instead of the next poll).
- Streaming: while anyone is playing, new jobs don't start and a running encode is suspended (SIGSTOP) and resumed afterwards. Playing means Jellyfin's `NowPlayingItem` is set (verified in the Jellyfin source); paused sessions and audio-only playback are ignored by default (both configurable).
- Failures: an auto-processed file is skipped after N failed attempts; a failed queue job shows its reason. The original is never touched on failure.
- Subtitle burn-in: by language it uses `--subtitle-lang-list <lang> --first-subtitle --subtitle-burned`; by exact track it uses `--subtitle N --subtitle-burned=1`. Both verified on HandBrake 1.7.2 with a made-up video carrying English and Japanese tracks (the right text was burned in each time).
- Resolution caps use `--maxHeight N --non-anamorphic` so you get square pixels (720p to 854x480). Without `--non-anamorphic` HandBrake keeps the source width and stores non-square pixels (1280x480), which many players mishandle.

## Tests

- `Tests/`: schedule, argument-builder and per-library profile logic (`dotnet run`).
- `Harness/`: end-to-end run of the real service, queue and API controller against a fake HandBrakeCLI and stubbed Jellyfin interfaces (`dotnet run`).
- `PageTest/test_page.py`: loads the real admin page in headless Chromium with a fake Jellyfin client and drives every tab (needs `pip install playwright`).
- `RealRun/`: end-to-end with the **real** HandBrakeCLI and ffmpeg on generated videos (`sudo apt install handbrake-cli ffmpeg`, then `./make_media.sh && dotnet run`; add `STRESS=1` for the long suspend/resume/cancel run). Checks outputs with ffprobe and saves frames to /tmp/e2e/frames so you can see the burned-in subtitles.
