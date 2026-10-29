// End-to-end with the REAL HandBrakeCLI and ffprobe/ffmpeg on made-up videos, through the plugin's real code path:
// controller.Enqueue -> JobStore -> ConversionService.TickAsync -> HandBrakeCLI -> finalise/rename -> state.
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.HandBrakeConverter;
using Jellyfin.Plugin.HandBrakeConverter.Api;
using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using Jellyfin.Plugin.HandBrakeConverter.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

int fails = 0;
void Check(string n, bool ok, string detail = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (detail != "" ? "   [" + detail + "]" : "")); if (!ok) fails++; }

var root = "/tmp/e2e"; var media = Path.Combine(root, "media"); var frames = Path.Combine(root, "frames");
Directory.CreateDirectory(frames); foreach (var f in Directory.GetFiles(frames)) File.Delete(f);
var data = Path.Combine(root, "data"); if (Directory.Exists(data)) Directory.Delete(data, true); Directory.CreateDirectory(data);

// Wrapper that logs the exact HandBrakeCLI command line, then runs the real one.
var logFile = Path.Combine(root, "hb-commands.log"); File.Delete(logFile);
var wrapper = Path.Combine(root, "hb-wrapper.sh");
File.WriteAllText(wrapper, $"#!/bin/bash\nprintf '%q ' \"$@\" >> {logFile}; echo >> {logFile}\nexec /usr/bin/HandBrakeCLI \"$@\"\n");
File.SetUnixFileMode(wrapper, (UnixFileMode)0b111_101_101);

string Run(string exe, params string[] args)
{
    var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return o.Trim();
}
(int w, int h, string sar, string vcodec, string acodec, List<string> subs) Probe(string file)
{
    var j = JsonDocument.Parse(Run("ffprobe", "-v", "error", "-show_streams", "-of", "json", file)).RootElement.GetProperty("streams");
    int w = 0, h = 0; string sar = "1:1", vc = "", ac = ""; var subs = new List<string>();
    foreach (var s in j.EnumerateArray())
    {
        var t = s.GetProperty("codec_type").GetString();
        if (t == "video") { w = s.GetProperty("width").GetInt32(); h = s.GetProperty("height").GetInt32(); vc = s.GetProperty("codec_name").GetString()!; if (s.TryGetProperty("sample_aspect_ratio", out var sr)) sar = sr.GetString()!; }
        if (t == "audio") ac = s.GetProperty("codec_name").GetString()!;
        if (t == "subtitle") subs.Add(s.GetProperty("codec_name").GetString()! + "/" + (s.TryGetProperty("tags", out var tg) && tg.TryGetProperty("language", out var l) ? l.GetString() : "?"));
    }
    return (w, h, sar, vc, ac, subs);
}
string Frame(string file, string name) { var o = Path.Combine(frames, name + ".png"); Run("ffmpeg", "-loglevel", "error", "-y", "-ss", "3", "-i", file, "-frames:v", "1", o); return o; }

// ---- fake Jellyfin library mirroring the made-up folder layout
var libMovies = Guid.NewGuid(); var libAnime = Guid.NewGuid();
var lib = new FakeLib();
var series = new Series { Id = Guid.NewGuid(), Name = "Test Show" };
var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1 };
var epDir = Path.Combine(media, "Anime", "TestShow", "Season 1");
var ep1 = new Episode { Id = Guid.NewGuid(), Name = "Pilot", IndexNumber = 1, Path = Path.Combine(epDir, "Test Show - S01E01 - Pilot.mkv") };
var ep2 = new Episode { Id = Guid.NewGuid(), Name = "Second", IndexNumber = 2, Path = Path.Combine(epDir, "Test Show - S01E02 - Second.mkv") };
var movie = new Movie { Id = Guid.NewGuid(), Name = "Test Movie", ProductionYear = 2020, Path = Path.Combine(media, "Movies", "Test Movie (2020)", "Test Movie (2020).mkv") };
foreach (var i in new BaseItem[] { series, season, ep1, ep2, movie }) lib.ById[i.Id] = i;
lib.Parent[libAnime] = new List<BaseItem> { series }; lib.Parent[series.Id] = new List<BaseItem> { season };
lib.Parent[season.Id] = new List<BaseItem> { ep1, ep2 }; lib.Recursive[series.Id] = new List<BaseItem> { ep1, ep2 }; lib.Recursive[libAnime] = new List<BaseItem> { ep1, ep2 };
lib.Parent[libMovies] = new List<BaseItem> { movie };
var origEp1 = new FileInfo(ep1.Path!).Length; var origEp2 = new FileInfo(ep2.Path!).Length; var origMovie = new FileInfo(movie.Path!).Length;

FakeLib.MediaRoot = media;
var state = new StateStore(new Paths(data), NullLogger<StateStore>.Instance);
var jobs = new JobStore(state, NullLogger<JobStore>.Instance);
var loc = new HandBrakeLocator();
var sess = new FakeSessions();
var trash = new TrashStore(state, NullLogger<TrashStore>.Instance);
var probe = new MediaProbe(new RealEncoder(), NullLogger<MediaProbe>.Instance);
var watcher = new PlaybackWatcher(sess, NullLogger<PlaybackWatcher>.Instance);
var backlog = new BacklogScanner(lib, state);
var history = new HistoryStore(state, NullLogger<HistoryStore>.Instance);
var transcodes = new TranscodeStore(new Paths(data), state, NullLogger<TranscodeStore>.Instance);
var caps = new HandBrakeCapabilities(new RealEncoder(), state, NullLogger<HandBrakeCapabilities>.Instance);
var svc = new ConversionService(lib, state, jobs, loc, trash, probe, watcher, backlog, history, caps, NullLogger<ConversionService>.Instance);

var cfg = new PluginConfiguration
{
    Enabled = true, HandBrakePath = wrapper, ScheduleEnabled = false, LowPriority = true,
    Defaults = new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Encoder = "x265", EncoderPreset = "ultrafast", Quality = 28, Container = "mkv", SubtitleMode = "keep" } },
    Libraries = new[]
    {
        new LibrarySettings { LibraryId = libMovies.ToString(), Name = "Movies", AutoProcess = true, UseCustom = false },
        new LibrarySettings { LibraryId = libAnime.ToString(), Name = "Anime", AutoProcess = false, UseCustom = true,
            Profile = new ConversionProfile { DeleteOriginal = false, OutputSuffix = "- HandBrake", HandBrake = new HandBrakeSettings { Encoder = "x264", EncoderPreset = "ultrafast", Quality = 26, MaxHeight = 480, SubtitleMode = "burn", BurnLanguage = "jpn" } } }
    }
};
Plugin.Instance = new Plugin { Configuration = cfg };
var ctl = new HandBrakeConverterController(svc, backlog, history, caps, transcodes, loc, state, jobs, trash, lib);
var tick = typeof(ConversionService).GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
async Task<bool> Tick() => await (Task<bool>)tick.Invoke(svc, new object[] { CancellationToken.None })!;
string Cmd() => File.Exists(logFile) ? File.ReadAllLines(logFile).Last() : "";

Console.WriteLine("HandBrake: " + await HandBrakeLocator.VersionAsync(loc.Resolve(wrapper)!, CancellationToken.None));
string D((int w, int h, string sar, string vcodec, string acodec, List<string> subs) p) => $"{p.w}x{p.h} sar={p.sar} v={p.vcodec} a={p.acodec} subs=[{string.Join(",", p.subs)}]";
Console.WriteLine("SOURCE ep1: " + D(Probe(ep1.Path!)));

// ================= STEP A: Convert tab -> queue Episode 1 using the Anime library's own settings (effective profile), burn JAPANESE by language
var animeProfile = cfg.EffectiveProfile(cfg.FindLibrary(libAnime.ToString()));
Check("A0 effective profile for Anime = its custom one", animeProfile.HandBrake.MaxHeight == 480 && animeProfile.HandBrake.BurnLanguage == "jpn");
var qa = ctl.Enqueue(new QueueRequest { ItemIds = new[] { ep1.Id.ToString("N") }, Profile = animeProfile }) as OkObjectResult;
Check("A1 queued 1 job", JsonSerializer.Serialize(qa!.Value).Contains("\"Added\":1"));
Check("A2 tick runs it", await Tick());
Console.WriteLine("CMD A: " + Cmd());
var outA = Path.Combine(epDir, "Test Show - S01E01 - Pilot - HandBrake.mkv");
Check("A3 named '<name> - HandBrake.mkv' next to original", File.Exists(outA));
Check("A4 original kept untouched", File.Exists(ep1.Path!) && new FileInfo(ep1.Path!).Length == origEp1);
var pa = Probe(outA); Console.WriteLine("OUT A: " + D(pa));
Check("A5 480p with SQUARE pixels and 16:9 kept (854x480)", pa.h == 480 && pa.sar == "1:1" && Math.Abs(pa.w - 854) <= 2, D(pa));
Check("A6 encoder x264 applied", pa.vcodec == "h264");
Check("A7 subtitles burned in (no soft sub streams left)", pa.subs.Count == 0);
Check("A8 audio copied/kept", pa.acodec == "aac");
Check("A9 command line has the burn + height flags", Cmd().Contains("--subtitle-lang-list jpn") && Cmd().Contains("--first-subtitle") && Cmd().Contains("--subtitle-burned") && Cmd().Contains("--maxHeight 480 --non-anamorphic") && Cmd().Contains("-e x264"));
Check("A10 job Done", jobs.List().First(j => j.Path == ep1.Path).Status == "Done");
Frame(outA, "A_burn_jpn_by_language");

// ================= STEP B: queue Episode 2 with its own one-off settings: burn exact track 1 (ENGLISH), replace original, MP4, AAC re-encode, 360p
var qb = ctl.Enqueue(new QueueRequest { ItemIds = new[] { ep2.Id.ToString("N") }, IgnoreSchedule = true, Profile = new ConversionProfile {
    DeleteOriginal = true, HandBrake = new HandBrakeSettings { Encoder = "x264", EncoderPreset = "ultrafast", Quality = 30, Container = "mp4", AudioMode = "aac", AudioBitrate = 96, MaxHeight = 360, SubtitleMode = "burn", BurnTrack = 1 } } });
Check("B1 tick runs it", await Tick());
Console.WriteLine("CMD B: " + Cmd());
var outB = ep2.Path!;   // replacing keeps the exact path/container (profile asked for MP4, but the source is MKV)
Check("B2 replaced in place: same path, no .mp4 created, original held in the holding folder", File.Exists(outB) && !File.Exists(Path.ChangeExtension(outB, ".mp4")) && trash.List().Any(t => t.OriginalPath == outB && t.Size == origEp2));
Check("B2b held original sits in a hidden .ignore'd folder inside the library (same drive), nothing on the data disk", trash.List().Any(t => t.TrashedPath.StartsWith(Path.Combine(media, ".hb-originals") + "/")) && File.Exists(Path.Combine(media, ".hb-originals", ".ignore")) && !Directory.Exists(Path.Combine(data, "plugins", "HandBrakeConverter", "trash")));
var pb = Probe(outB); Console.WriteLine("OUT B: " + D(pb));
Check("B3 360p square pixels (640x360), h264, aac, no soft subs", pb.h == 360 && pb.sar == "1:1" && Math.Abs(pb.w - 640) <= 2 && pb.vcodec == "h264" && pb.acodec == "aac" && pb.subs.Count == 0, D(pb));
Check("B4 command: track 1 burned, 96k aac", Cmd().Contains("--subtitle 1") && Cmd().Contains("--subtitle-burned=1") && Cmd().Contains("--aencoder av_aac") && Cmd().Contains("-B 96"));
Check("B5 smaller than source", new FileInfo(outB).Length < origEp2);
Frame(outB, "B_burn_track1_english");

// ================= STEP C: "same as source" everywhere (container, audio, subtitles) + 360p, keep the original and add a labelled copy
var epC = Path.Combine(epDir, "Test Show - S01E03 - Same.mkv");
File.Copy(Path.Combine(root, "preset-src.mkv"), epC, true);
var ep3 = new Episode { Id = Guid.NewGuid(), Name = "Same", IndexNumber = 3, Path = epC }; lib.ById[ep3.Id] = ep3;
var origC = new FileInfo(epC).Length;
ctl.Enqueue(new QueueRequest { ItemIds = new[] { ep3.Id.ToString("N") }, IgnoreSchedule = true, Profile = new ConversionProfile { DeleteOriginal = false, OutputSuffix = "- Small", HandBrake = new HandBrakeSettings { Container = "source", AudioMode = "copy", SubtitleMode = "keep", MaxHeight = 360, Encoder = "x264", EncoderPreset = "ultrafast", Quality = 30 } } });
Check("C1 tick runs it", await Tick());
Console.WriteLine("CMD C: " + Cmd());
var outC = Path.Combine(epDir, "Test Show - S01E03 - Same - Small.mkv");
Check("C2 output named with the custom label, same container as the source", File.Exists(outC));
var pc = Probe(outC); Console.WriteLine("OUT C: " + D(pc));
Check("C3 360p square pixels, audio copied, both subtitle tracks kept", pc.h == 360 && pc.sar == "1:1" && pc.acodec == "aac" && pc.subs.Count == 2, D(pc));
Check("C4 command: --all-subtitles, audio copy", Cmd().Contains("--all-subtitles") && Cmd().Contains("--aencoder copy"));
Check("C5 original untouched", new FileInfo(epC).Length == origC);

// ================= STEP D: auto-process the Movies library (Defaults: x265, keep subs, replace original in place); Anime has Auto OFF
var pending = backlog.FindPending(cfg, 10);
Check("D1 auto backlog = only the Movies file (Anime auto is off)", pending.Count == 1 && pending[0].Library == "Movies");
Check("D2 tick auto-converts it", await Tick());
Console.WriteLine("CMD D: " + Cmd());
var pd = Probe(movie.Path!); Console.WriteLine("OUT D: " + D(pd));
Check("D3 replaced in place (same name), x265/hevc, 1280x720 untouched, subtitle track kept", pd.vcodec == "hevc" && pd.subs.Count == 1 && pd.h == 720 && pd.w == 1280, D(pd));
Check("D4 smaller than the original", new FileInfo(movie.Path!).Length < origMovie);
Check("D5 no temp files anywhere", !Directory.GetFiles(media, "*.hbtmp", SearchOption.AllDirectories).Any());
Check("D6 command used Defaults (x265, all-subtitles, no maxHeight)", Cmd().Contains("-e x265") && Cmd().Contains("--all-subtitles") && !Cmd().Contains("maxHeight"));

// ================= STEP E: loop guard + idle
lib.Parent[libMovies].Add(new Movie { Id = Guid.NewGuid(), Name = "same file, new id after rescan", Path = movie.Path });
Check("E1 loop guard: rescan with a new item id is not re-queued", backlog.FindPending(cfg, 10).Count == 0);
Check("E2 tick is idle", !await Tick());
var (conv, saved, failed) = state.Stats();
Check("E3 stats recorded", conv == 4 && saved > 0 && failed == 0, $"converted={conv} saved={saved / 1024}KB failed={failed}");
Check("E4 queue shows 3 Done jobs", jobs.List().Count(j => j.Status == "Done") == 3);


// ================= STEP G: real HDR10 + chapters + encoder list/test
Run("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=d=8:s=640x360:r=24", "-f", "lavfi", "-i", "sine=d=8", "-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-preset", "ultrafast",
    "-x265-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400:log-level=error", "-c:a", "aac", Path.Combine(root, "hdr0.mkv"));
File.WriteAllText(Path.Combine(root, "chap.txt"), ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=3000\ntitle=Intro\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=3000\nEND=8000\ntitle=Episode\n");
Run("ffmpeg", "-loglevel", "error", "-y", "-i", Path.Combine(root, "hdr0.mkv"), "-i", Path.Combine(root, "chap.txt"), "-map_metadata", "1", "-map_chapters", "1", "-c", "copy", Path.Combine(root, "hdr.mkv"));
var hdrDir = Path.Combine(media, "Movies", "HDR Movie (2021)"); Directory.CreateDirectory(hdrDir);
(string tr, string pf, int ch) HdrInfo(string f) { var d = JsonDocument.Parse(Run("ffprobe", "-v", "error", "-show_streams", "-show_chapters", "-of", "json", f)).RootElement; var v = d.GetProperty("streams").EnumerateArray().First(x => x.GetProperty("codec_type").GetString() == "video"); return (v.GetProperty("color_transfer").GetString() ?? "", v.GetProperty("pix_fmt").GetString() ?? "", d.GetProperty("chapters").GetArrayLength()); }
Episode HdrEp(string name) { var path = Path.Combine(hdrDir, name + ".mkv"); File.Copy(Path.Combine(root, "hdr.mkv"), path, true); var e = new Episode { Id = Guid.NewGuid(), Name = name, Path = path }; lib.ById[e.Id] = e; return e; }
ConversionProfile HdrProf(string mode) => new() { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Encoder = "x265", EncoderPreset = "ultrafast", Quality = 28, Container = "source", HdrMode = mode } };
cfg.KeepReplacedDays = 0;

var hdrSkip = HdrEp("hdr-skip"); var hs = new FileInfo(hdrSkip.Path!).Length;
ctl.Enqueue(new QueueRequest { ItemIds = new[] { hdrSkip.Id.ToString("N") }, IgnoreSchedule = true, Profile = HdrProf("skip") });
await Tick();
var jSkip = jobs.List().First(j => j.Path == hdrSkip.Path);
Check("G1 real HDR10 source is detected and left alone by default", jSkip.Status == "Skipped" && new FileInfo(hdrSkip.Path!).Length == hs, jSkip.Error);

var hdrKeep = HdrEp("hdr-keep");
ctl.Enqueue(new QueueRequest { ItemIds = new[] { hdrKeep.Id.ToString("N") }, IgnoreSchedule = true, Profile = HdrProf("keep") });
await Tick();
var jKeep = jobs.List().First(j => j.Path == hdrKeep.Path);
var hk = HdrInfo(hdrKeep.Path!);
Check("G2 'keep HDR' re-encodes with x265_10bit and the result is still HDR10 10-bit", jKeep.Status == "Done" && Cmd().Contains("-e x265_10bit") && hk.tr == "smpte2084" && hk.pf.Contains("10"), $"{jKeep.Status} {jKeep.Error} {hk}");
Check("G3 chapters survive the real conversion (and passed the plugin's check)", hk.ch == 2, "chapters=" + hk.ch);

var hdrIgn = HdrEp("hdr-ignore");
ctl.Enqueue(new QueueRequest { ItemIds = new[] { hdrIgn.Id.ToString("N") }, IgnoreSchedule = true, Profile = HdrProf("ignore") });
await Tick();
var jIgn = jobs.List().First(j => j.Path == hdrIgn.Path);
Check("G4 'convert anyway' runs the plain encoder", jIgn.Status is "Done" or "Skipped" && Cmd().Contains("-e x265 "), jIgn.Status + " " + jIgn.Error);

var encs = await caps.EncodersAsync(loc.Resolve(wrapper)!, CancellationToken.None);
Check("G5 real HandBrake encoder list is read (x265_10bit present, no NVENC without a GPU)", encs.Contains("x265_10bit") && encs.Contains("x264") && !encs.Any(e => e.StartsWith("nvenc")), string.Join(",", encs));
var (okT, msgT) = await caps.TestAsync(loc.Resolve(wrapper)!, "x264", CancellationToken.None);
Check("G6 real sample-encode test passes for x264", okT, msgT);
Check("G7 history has real entries with sizes", history.List(50).Any(h => h.Result == "Converted" && h.OldBytes > h.NewBytes && h.Seconds > 0) && history.List(50).Any(h => h.Result == "Skipped"));


// ================= STEP H: real ffmpeg output parsed like a Jellyfin transcode log
var logDirReal = Path.Combine(data, "log"); Directory.CreateDirectory(logDirReal);
string RealLog(string prefix, string src, string vEnc, string aEnc, string vf)
{
    var args = new List<string> { "-y", "-analyzeduration", "200M", "-i", "file:" + src, "-map_metadata", "-1", "-map_chapters", "-1", "-threads", "0", "-map", "0:0", "-map", "0:1", "-codec:v:0", vEnc };
    if (vf != "") { args.Add("-vf"); args.Add(vf); }
    args.AddRange(new[] { "-codec:a:0", aEnc, "-f", "mpegts", Path.Combine(root, "tc-out.ts") });
    var cmd = "/usr/lib/jellyfin-ffmpeg/ffmpeg " + string.Join(" ", args.Select(a => a.Contains(' ') || a.Contains('(') ? "\"" + a + "\"" : a == "file:" + src ? "file:\"" + src + "\"" : a));
    var text = cmd + "\n\n" + Run("ffmpeg", args.ToArray());
    var name = $"FFmpeg.{prefix}-{DateTime.Now.AddHours(-1):yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.log";
    File.WriteAllText(Path.Combine(logDirReal, name), text);
    return name;
}
var hdrSrc = Path.Combine(root, "hdr.mkv");
var lg1 = RealLog("Transcode", hdrSrc, "libx264", "aac", "scale=-2:180");
var lg2 = RealLog("Remux", hdrSrc, "copy", "aac", "");
RealLog("DirectStream", hdrSrc, "copy", "copy", "");
Check("H1 real ffmpeg logs: two playbacks found, direct stream ignored", new TranscodeStore(new Paths(data), state, NullLogger<TranscodeStore>.Instance) is var tsr && tsr.Scan() == 2);
var realSessions = new TranscodeStore(new Paths(data), state, NullLogger<TranscodeStore>.Instance).Sessions(2);
var rv = realSessions.First(x => x.VideoTranscoded); var rr = realSessions.First(x => !x.VideoTranscoded);
Check("H2 real video transcode read right: HEVC HDR 360p source -> H.264 at 180p, audio re-encoded", rv.SrcVideo == "hevc" && rv.SrcHdr && rv.SrcHeight == 360 && rv.DstVideo == "h264" && rv.DstHeight == 180 && rv.AudioTranscoded && rv.DstAudio == "aac" && rv.SrcAudio == "aac" && rv.Path == hdrSrc, System.Text.Json.JsonSerializer.Serialize(rv));
Check("H3 real remux read right: video copied, audio re-encoded", rr.AudioTranscoded && rr.DstVideo == "" && rr.Path == hdrSrc);

// ================= STEP F (STRESS=1): real HandBrake, long encode: suspend while streaming, resume, then cancel mid-encode
if (Environment.GetEnvironmentVariable("STRESS") == "1")
{
    var big = Path.Combine(media, "Movies", "Stress Test (2021).mkv");
    Run("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=120", "-f", "lavfi", "-i", "sine=duration=120", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", big);
    var stress = new Movie { Id = Guid.NewGuid(), Name = "Stress", Path = big }; lib.ById[stress.Id] = stress;
    var bigSize = new FileInfo(big).Length;
    var slow = new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Encoder = "x265", EncoderPreset = "slow", Quality = 22, SubtitleMode = "none" } };
    int[] HbPids() => Run("pgrep", "-x", "HandBrakeCLI").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    string StateOf(int pid) { try { var t = File.ReadAllText($"/proc/{pid}/stat"); return t[(t.LastIndexOf(')') + 2)..].Split(' ')[0]; } catch { return "gone"; } }

    // F1: suspend while streaming, resume afterwards
    ctl.Enqueue(new QueueRequest { ItemIds = new[] { stress.Id.ToString("N") }, IgnoreSchedule = true, Profile = slow });
    var t1 = Tick();
    await Task.Delay(5000);
    var pids = HbPids();
    Check("F1 real HandBrake process is running", pids.Length == 1 && svc.Snapshot().State == "Encoding" && svc.Snapshot().Progress > 0, $"pids={string.Join(",", pids)} progress={svc.Snapshot().Progress:F1}%");
    sess.Playing = true;                      // someone starts watching
    await Task.Delay(4500);                   // monitor polls every 3 s
    Check("F2 viewer starts: HandBrake process is stopped (state T) and plugin says suspended", pids.Length == 1 && StateOf(pids[0]) == "T" && svc.Snapshot().Suspended, $"state={StateOf(pids[0])}");
    var p1 = svc.Snapshot().Progress; await Task.Delay(4000); var p2 = svc.Snapshot().Progress;
    Check("F3 progress frozen while someone watches", Math.Abs(p2 - p1) < 0.01, $"{p1:F1}% -> {p2:F1}%");
    sess.Playing = false;
    await Task.Delay(4500);
    Check("F4 viewer stops: process resumed and progress moves again", StateOf(pids[0]) != "T" && svc.Snapshot().Progress > p2 && !svc.Snapshot().Suspended, $"state={StateOf(pids[0])} {p2:F1}% -> {svc.Snapshot().Progress:F1}%");

    // F5: cancel mid-encode from the Queue tab
    var job = jobs.List().First(j => j.Status == "Running");
    ctl.RemoveJobs(new RemoveRequest { Ids = new[] { job.Id } });
    await t1;
    Check("F5 cancel: job Cancelled, HandBrake process gone", jobs.List().First(j => j.Id == job.Id).Status == "Cancelled" && HbPids().Length == 0);
    Check("F6 cancel: original untouched, no temp file, not marked converted", new FileInfo(big).Length == bigSize && !Directory.GetFiles(Path.GetDirectoryName(big)!, "*.hbtmp").Any() && !state.IsProcessed("x", big));

}

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;

class Paths : IApplicationPaths { public Paths(string d) { DataPath = d; } public string DataPath { get; } public string LogDirectoryPath => System.IO.Path.Combine(DataPath, "log"); }
class FakeLib : ILibraryManager
{
    public Dictionary<Guid, List<BaseItem>> Parent = new(); public Dictionary<Guid, List<BaseItem>> Recursive = new(); public Dictionary<Guid, BaseItem> ById = new();
    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery q) => q.Recursive && Recursive.TryGetValue(q.ParentId, out var r) ? r.ToList() : Parent.TryGetValue(q.ParentId, out var l) ? l.ToList() : new List<BaseItem>();
    public BaseItem? GetItemById(Guid id) => ById.TryGetValue(id, out var i) ? i : null;
    public void QueueLibraryScan() { }
    public List<MediaBrowser.Model.Entities.VirtualFolderInfo> GetVirtualFolders() => new() { new MediaBrowser.Model.Entities.VirtualFolderInfo { Locations = new[] { MediaRoot } } };
    public static string MediaRoot = "";
    public event EventHandler<ItemChangeEventArgs>? ItemAdded; public event EventHandler<ItemChangeEventArgs>? ItemUpdated; public event EventHandler<ItemChangeEventArgs>? ItemRemoved;
}
class RealEncoder : MediaBrowser.Controller.MediaEncoding.IMediaEncoder { public string EncoderPath => "/usr/bin/ffmpeg"; public string ProbePath => "/usr/bin/ffprobe"; }
class FakeSessions : ISessionManager
{
    public event EventHandler<PlaybackProgressEventArgs>? PlaybackStart; public event EventHandler<PlaybackProgressEventArgs>? PlaybackProgress; public event EventHandler<PlaybackStopEventArgs>? PlaybackStopped;
    public bool Playing; public IEnumerable<SessionInfo> Sessions => Playing ? new[] { new SessionInfo { NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = Guid.NewGuid(), MediaType = Jellyfin.Data.Enums.MediaType.Video }, PlayState = new PlayState() } } : Array.Empty<SessionInfo>();
}
