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
using System.Reflection;
using System.Text.Json;

int fails = 0;
void Check(string n, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + n); if (!ok) fails++; }

var root = Path.Combine(Path.GetTempPath(), "hbtest-" + Guid.NewGuid().ToString("N"));
var data = Path.Combine(root, "data"); var media = Path.Combine(root, "media");
Directory.CreateDirectory(data); Directory.CreateDirectory(media);
var argsFile = Path.Combine(root, "args.txt");

// Fake HandBrakeCLI: logs its args, prints \r-separated progress, writes ~300KB output to the -o path.
var fake = Path.Combine(root, "fakeHB.sh");
File.WriteAllText(fake, @"#!/bin/bash
export PATH=/usr/bin:/bin:$PATH
if [ ""$1"" = ""--version"" ]; then echo 'HandBrake 1.9.0 (fake)'; exit 0; fi
if [ ""$1"" = ""--help"" ]; then echo '   -e, --encoder <string>  Select video encoder:'; for e in ${HB_ENCODERS:-x264 x265 x265_10bit svt_av1}; do echo ""                               $e""; done; echo '       --encoder-preset <string>'; exit 0; fi
[ -n ""$HB_ARGS_FILE"" ] && echo ""$@"" >> ""$HB_ARGS_FILE""
while [ $# -gt 0 ]; do case ""$1"" in -o) out=""$2""; shift;; esac; shift; done
if [ -n ""$HB_FAIL"" ]; then echo 'boom' >&2; exit 3; fi
for p in 10.00 50.50 99.00; do printf 'Encoding: task 1 of 1, %s %% (30.0 fps, avg 31.0 fps, ETA 00h00m01s)\r' ""$p""; sleep ${HB_SLEEP:-0.2}; done
head -c 300000 /dev/urandom > ""$out""
");
File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
Environment.SetEnvironmentVariable("HB_ARGS_FILE", argsFile);

var libA = Guid.NewGuid(); var libB = Guid.NewGuid(); var libC = Guid.NewGuid();
var lib = new FakeLib();
lib.Roots = new[] { media };
Episode Add(Guid parent, string name, string ext = ".mkv", int kb = 600)
{
    var p = Path.Combine(media, name + ext); File.WriteAllBytes(p, new byte[kb * 1024]);
    var e = new Episode { Id = Guid.NewGuid(), Path = p, Name = name };
    lib.Parent.GetOrAdd(parent).Add(e); lib.ById[e.Id] = e; lib.Raise(); return e;
}

var sess = new FakeSessions();
var state = new StateStore(new Paths(data), NullLogger<StateStore>.Instance);
var jobs = new JobStore(state, NullLogger<JobStore>.Instance);
var loc = new HandBrakeLocator();
var trash = new TrashStore(state, NullLogger<TrashStore>.Instance);
// Fake ffprobe: 100 s for everything; FP_SHORT makes temp outputs look truncated (20 s); FP_NONE makes it fail.
var fakeProbe = Path.Combine(root, "fakeffprobe.sh");
File.WriteAllText(fakeProbe, @"#!/bin/bash
[ -n ""$FP_NONE"" ] && exit 1
for a in ""$@""; do last=""$a""; done
out=; [[ ""$last"" == *.hbtmp ]] && out=1
dur=100.0; [ -n ""$out"" ] && [ -n ""$FP_SHORT"" ] && dur=20.0
aud=${FP_AUDIO:-2}; subs=${FP_SUBS:-2}; ch=${FP_CHAPTERS:-3}; tr=bt709; pf=yuv420p
[ -n ""$FP_HDR"" ] && { tr=smpte2084; pf=yuv420p10le; }
if [ -n ""$out"" ]; then
  [ -n ""$FP_OUT_AUDIO"" ] && aud=$FP_OUT_AUDIO
  [ -n ""$FP_OUT_SUBS"" ] && subs=$FP_OUT_SUBS
  [ -n ""$FP_OUT_CHAPTERS"" ] && ch=$FP_OUT_CHAPTERS
  [ -n ""$FP_OUT_SDR"" ] && { tr=bt709; pf=yuv420p; }
  [ -n ""$FP_OUT_8BIT"" ] && pf=yuv420p
fi
side=''; [ -z ""$out"" ] && [ -n ""$FP_DV"" ] && side=',""side_data_list"":[{""side_data_type"":""DOVI configuration record""}]'
s='{""streams"":[{""codec_type"":""video"",""codec_name"":""hevc"",""pix_fmt"":""'$pf'"",""color_transfer"":""'$tr'""'$side'}'
for ((i=0;i<aud;i++)); do s+=',{""codec_type"":""audio"",""codec_name"":""aac""}'; done
for ((i=0;i<subs;i++)); do s+=',{""codec_type"":""subtitle"",""codec_name"":""subrip""}'; done
s+='],""chapters"":['
for ((i=0;i<ch;i++)); do [ $i -gt 0 ] && s+=','; s+='{""id"":'$i'}'; done
s+='],""format"":{""duration"":""'$dur'""}}'
echo ""$s""
");
File.SetUnixFileMode(fakeProbe, (UnixFileMode)0b111_101_101);
var probe = new MediaProbe(new FakeEncoder(fakeProbe), NullLogger<MediaProbe>.Instance);
BacklogScanner.QuietSeconds = 0;
var watcher = new PlaybackWatcher(sess, NullLogger<PlaybackWatcher>.Instance);
var backlog = new BacklogScanner(lib, state);
var history = new HistoryStore(state, NullLogger<HistoryStore>.Instance);
var transcodes = new TranscodeStore(new Paths(data), state, NullLogger<TranscodeStore>.Instance);
var caps = new HandBrakeCapabilities(new FakeEncoder(fakeProbe), state, NullLogger<HandBrakeCapabilities>.Instance);
var svc = new ConversionService(lib, state, jobs, loc, trash, probe, watcher, backlog, history, caps, NullLogger<ConversionService>.Instance);
void Clear() { lib.Raise(); lib.Parent.Clear(); lib.ById.Clear(); lib.Recursive.Clear(); foreach (var f in Directory.GetFiles(media)) File.Delete(f); File.Delete(argsFile); jobs.Remove(jobs.List().Select(j => j.Id)); }

var cfg = new PluginConfiguration
{
    Enabled = true, HandBrakePath = fake, ScheduleEnabled = false, LowPriority = false, PauseWhileStreaming = true,
    Defaults = new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Container = "mkv" } },
    Libraries = new[]
    {
        new LibrarySettings { LibraryId = libA.ToString(), Name = "Movies", AutoProcess = true, UseCustom = false },
        new LibrarySettings { LibraryId = libB.ToString(), Name = "Anime", AutoProcess = true, UseCustom = true,
            Profile = new ConversionProfile { DeleteOriginal = false, OutputSuffix = "- 720p", HandBrake = new HandBrakeSettings { MaxHeight = 720, SubtitleMode = "burn", BurnLanguage = "jpn", Container = "mkv" } } },
        new LibrarySettings { LibraryId = libC.ToString(), Name = "Home videos", AutoProcess = false }
    }
};
Plugin.Instance = new Plugin { Configuration = cfg };

var tick = typeof(ConversionService).GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
async Task<bool> Tick() => await (Task<bool>)tick.Invoke(svc, new object[] { CancellationToken.None })!;
string Args() => File.Exists(argsFile) ? File.ReadAllText(argsFile) : "";

// ---- 1) streaming blocks start
var m1 = Add(libA, "Movie A");
sess.Playing = true;
Check("streaming: tick does no work", !await Tick());
sess.Playing = false;

// ---- 2) per-library profiles + auto flag
var anime1 = Add(libB, "Anime Ep1"); var home1 = Add(libC, "Home clip");
var pend = backlog.FindPending(cfg, 10);
Console.WriteLine("pending: " + string.Join(", ", pend.Select(p => Path.GetFileName(p.Path) + "/" + p.Library)));
Check("auto: Movies + Anime pending", pend.Count == 2);
Check("auto: library with Auto off is ignored", !pend.Any(p => p.Library == "Home videos"));
Check("per-library: Movies uses Defaults (delete original)", pend.First(p => p.Library == "Movies").Profile.DeleteOriginal);
Check("per-library: Anime uses its own (720p burn, keep original)", pend.First(p => p.Library == "Anime").Profile is { DeleteOriginal: false } pr && pr.HandBrake.MaxHeight == 720);

// ---- 3) auto-convert both, each with its own settings
Check("tick converts first (Movie A, defaults)", await Tick());
Check("Movie A: replaced in place", File.Exists(m1.Path!) && new FileInfo(m1.Path!).Length < 600 * 1024);
Check("Movie A args: no burn, no maxHeight", !Args().Contains("subtitle-burned") && !Args().Contains("maxHeight") && Args().Contains("--all-subtitles"));
File.Delete(argsFile);
Check("tick converts second (Anime, custom)", await Tick());
Check("Anime: original kept, '- 720p' copy created", File.Exists(anime1.Path!) && File.Exists(Path.Combine(media, "Anime Ep1 - 720p.mkv")));
Check("Anime args: maxHeight 720 + burn jpn", Args().Contains("--maxHeight 720") && Args().Contains("--subtitle-lang-list jpn") && Args().Contains("--subtitle-burned"));

// ---- 4) loop guard: rescan shows the converted copy as a new item
lib.Parent[libB].Add(new Episode { Id = Guid.NewGuid(), Path = Path.Combine(media, "Anime Ep1 - 720p.mkv"), Name = "copy" }); lib.Raise();
Check("loop guard: nothing pending after rescan", backlog.FindPending(cfg, 10).Count == 0);
Check("idle tick = no work", !await Tick());

// ---- 5) manual job in a library with Auto OFF, own settings (burn track 3, 480p, delete original)
Clear(); state.Reset();
var h = Add(libC, "Show S01E01", ".mkv");
var jobProfile = new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { MaxHeight = 480, SubtitleMode = "burn", BurnTrack = 3, Container = "mkv" } };
var (added, skipped) = jobs.Add(new[] { new Job { ItemId = h.Id.ToString("N"), Path = h.Path!, Title = h.Name!, Profile = jobProfile } });
Check("job added", added == 1 && skipped == 0);
var (a2, s2) = jobs.Add(new[] { new Job { ItemId = h.Id.ToString("N"), Path = h.Path!, Title = h.Name!, Profile = jobProfile } });
Check("job dedupe: same file not queued twice", a2 == 0 && s2 == 1);
Check("tick runs the queued job", await Tick());
Check("job args: track 3 burned at 480p", Args().Contains("--subtitle 3") && Args().Contains("--subtitle-burned=1") && Args().Contains("--maxHeight 480"));
Check("job: Done and output replaced", jobs.List().First().Status == "Done" && new FileInfo(h.Path!).Length < 600 * 1024);
Check("job: file recorded so auto never re-processes", state.IsProcessed("x", h.Path!));

// ---- 6) schedule: queued job waits outside window unless 'run now'
Clear(); state.Reset();
var w1 = Add(libC, "Waits"); var w2 = Add(libC, "RunsNow");
cfg.ScheduleEnabled = true; cfg.Windows = Array.Empty<ScheduleWindow>(); // never inside a window
jobs.Add(new[] { new Job { ItemId = "w1", Path = w1.Path!, Title = "Waits", Profile = jobProfile } });
Check("outside window: normal job does not run", !await Tick() && jobs.QueuedCount() == 1);
Check("outside window: status Waiting (1 queued)", svc.Snapshot().State == "Waiting" && svc.Snapshot().Message.Contains("1 queued"));
jobs.Add(new[] { new Job { ItemId = "w2", Path = w2.Path!, Title = "RunsNow", Profile = jobProfile, IgnoreSchedule = true } });
Check("outside window: 'run now' job runs", await Tick());
Check("outside window: only the run-now job ran", jobs.List().Count(j => j.Status == "Done") == 1 && jobs.List().Any(j => j.Title == "Waits" && j.Status == "Queued"));
cfg.ScheduleEnabled = false;

// ---- 7) cancel a running job
Clear(); state.Reset();
var c1 = Add(libC, "Cancelme");
var cj = new Job { ItemId = "c1", Path = c1.Path!, Title = "Cancelme", Profile = jobProfile };
jobs.Add(new[] { cj });
Environment.SetEnvironmentVariable("HB_SLEEP", "3");
var t = Tick();
await Task.Delay(1500);
Check("cancel: job shows Running", jobs.List().First().Status == "Running" && svc.Snapshot().CurrentJobId == cj.Id);
jobs.Remove(new[] { cj.Id });
await t;
Environment.SetEnvironmentVariable("HB_SLEEP", null);
Check("cancel: job Cancelled", jobs.List().First().Status == "Cancelled");
Check("cancel: original untouched, no temp", new FileInfo(c1.Path!).Length == 600 * 1024 && !Directory.GetFiles(media, "*.hbtmp").Any());
Check("cancel: not recorded as converted", !state.IsProcessed("x", c1.Path!));

// ---- 8) failure: job marked Failed with reason, original intact
Clear();
var f1 = Add(libC, "Fails");
jobs.Add(new[] { new Job { ItemId = "f1", Path = f1.Path!, Title = "Fails", Profile = jobProfile } });
Environment.SetEnvironmentVariable("HB_FAIL", "1");
await Tick();
Environment.SetEnvironmentVariable("HB_FAIL", null);
var fj = jobs.List().First();
Check("fail: job Failed with reason", fj.Status == "Failed" && fj.Error.Contains("exit 3") && fj.Error.Contains("boom"));
Check("fail: original untouched", new FileInfo(f1.Path!).Length == 600 * 1024);

// ---- 9) suspend while streaming mid-encode
Clear(); state.Reset();
var s1 = Add(libC, "Streamy");
jobs.Add(new[] { new Job { ItemId = "s1", Path = s1.Path!, Title = "Streamy", Profile = jobProfile } });
Environment.SetEnvironmentVariable("HB_SLEEP", "2");
var ts = Tick();
await Task.Delay(1200);
sess.Playing = true;
await Task.Delay(3800);
Check("stream: encode suspended", svc.Snapshot().Suspended);
sess.Playing = false;
Check("stream: finishes after resume", await ts);
Environment.SetEnvironmentVariable("HB_SLEEP", null);
Check("stream: job Done", jobs.List().First().Status == "Done");

// ---- 9b) window closes mid-encode
Clear(); state.Reset();
var wc = Add(libC, "WindowCloses");
jobs.Add(new[] { new Job { ItemId = "wc", Path = wc.Path!, Title = "WindowCloses", Profile = jobProfile } });
cfg.FinishCurrentAfterWindow = false;                 // cancel when the window ends
Environment.SetEnvironmentVariable("HB_SLEEP", "3");
var tw = Tick();                                      // starts while the schedule is off (= allowed)
await Task.Delay(1500);
cfg.ScheduleEnabled = true; cfg.Windows = Array.Empty<ScheduleWindow>();   // the window "closes"
await tw;
Check("window close (cancel mode): job back to Queued", jobs.List().First().Status == "Queued");
Check("window close: original untouched, no temp file", new FileInfo(wc.Path!).Length == 600 * 1024 && !Directory.GetFiles(media, "*.hbtmp").Any());
Check("window close: status Waiting", svc.Snapshot().State == "Waiting");

Clear(); state.Reset(); cfg.ScheduleEnabled = false;
var wf = Add(libC, "WindowFinishes");
jobs.Add(new[] { new Job { ItemId = "wf", Path = wf.Path!, Title = "WindowFinishes", Profile = jobProfile } });
cfg.FinishCurrentAfterWindow = true;                  // let the current file finish
var tf = Tick();
await Task.Delay(1500);
cfg.ScheduleEnabled = true; cfg.Windows = Array.Empty<ScheduleWindow>();
await tf;
Check("window close (finish mode): current file still completes", jobs.List().First().Status == "Done" && new FileInfo(wf.Path!).Length < 600 * 1024);
Check("window close (finish mode): nothing new starts afterwards", !await Tick());
cfg.ScheduleEnabled = false; Environment.SetEnvironmentVariable("HB_SLEEP", null);

// ---- 9c) streaming rules (start-blocking), every combination
Clear(); state.Reset(); cfg.ScheduleEnabled = false; cfg.PauseWhileStreaming = true; cfg.PausedCountsAsStreaming = false;
Job RunNow(Episode e) => new Job { ItemId = e.Id.ToString("N"), Path = e.Path!, Title = e.Name!, Profile = jobProfile, IgnoreSchedule = true };

var r1e = Add(libC, "Rule1"); jobs.Add(new[] { RunNow(r1e) });
sess.Playing = true; sess.Paused = false;
Check("playing: queued job (even 'run now') does not start", !await Tick() && jobs.List().First().Status == "Queued" && svc.Snapshot().Message.Contains("playing"));
sess.Paused = true;
Check("paused playback does NOT block by default", await Tick() && jobs.List().First().Status == "Done");

Clear(); state.Reset();
var r2e = Add(libC, "Rule2"); jobs.Add(new[] { RunNow(r2e) });
cfg.PausedCountsAsStreaming = true; sess.Playing = true; sess.Paused = true;
Check("paused counts as streaming when enabled -> blocked", !await Tick() && jobs.List().First().Status == "Queued");
cfg.PausedCountsAsStreaming = false;

cfg.PauseWhileStreaming = false; sess.Paused = false;
Check("'pause while streaming' OFF -> runs despite playback", await Tick() && jobs.List().First().Status == "Done");
cfg.PauseWhileStreaming = true;

Clear(); state.Reset();
var r3e = Add(libC, "Rule3"); jobs.Add(new[] { RunNow(r3e) });
sess.Playing = false; sess.IdleClient = true;    // a connected client that is not playing anything
Check("idle connected client does not block", await Tick() && jobs.List().First().Status == "Done");
sess.IdleClient = false;

Clear(); state.Reset();
var auto1 = Add(libA, "AutoWhilePlaying");
sess.Playing = true; sess.Paused = false;
Check("auto-process also blocked while playing", !await Tick() && new FileInfo(auto1.Path!).Length == 600 * 1024);
sess.Playing = false;
Check("auto-process starts once playback stops", await Tick() && new FileInfo(auto1.Path!).Length < 600 * 1024);

// ---- 9d) never swap a file someone is watching; audio-only playback handling
Clear(); state.Reset(); cfg.ScheduleEnabled = false; cfg.PauseWhileStreaming = true;
var au = Add(libC, "Audio only"); jobs.Add(new[] { RunNow(au) });
sess.Playing = true; sess.Paused = false; sess.Audio = true; cfg.IgnoreAudioPlayback = true;
Check("music playing: ignored by default (job runs)", await Tick() && jobs.List().First().Status == "Done");
Clear(); state.Reset();
var au2 = Add(libC, "Audio blocks"); jobs.Add(new[] { RunNow(au2) });
cfg.IgnoreAudioPlayback = false;
Check("music playing + 'ignore audio' OFF -> blocked", !await Tick() && jobs.List().First().Status == "Queued");
cfg.IgnoreAudioPlayback = true; sess.Playing = false; sess.Audio = false;

// someone watches THIS item while its encode finishes (user turned 'suspend while streaming' off, or started in the last few seconds)
Clear(); state.Reset(); cfg.PauseWhileStreaming = false;
var watched = Add(libC, "Being watched");
var delJob = new Job { ItemId = watched.Id.ToString("N"), Path = watched.Path!, Title = "Being watched", Profile = jobProfile, IgnoreSchedule = true };
jobs.Add(new[] { delJob });
Environment.SetEnvironmentVariable("HB_SLEEP", "1");
var twatch = Tick();
await Task.Delay(1200);
sess.Playing = true; sess.Paused = false; sess.PlayingItemId = watched.Id;   // viewer opens this very item
await Task.Delay(9000);                                                          // encode (3 s) is long finished
Check("watched item: encode finished but original NOT replaced", new FileInfo(watched.Path!).Length == 600 * 1024 && File.Exists(watched.Path + ".hbtmp"));
Check("watched item: status says it is waiting", svc.Snapshot().State == "Finishing" && svc.Snapshot().Message.Contains("waiting for playback"));
Check("watched item: job not marked Done yet and not recorded as converted", jobs.List().First().Status == "Running" && !state.IsProcessed("x", watched.Path!));
sess.Playing = false;                                                            // viewer stops
Check("viewer stops: swap completes", await twatch && new FileInfo(watched.Path!).Length < 600 * 1024 && jobs.List().First().Status == "Done");
Check("viewer stops: no temp file left", !File.Exists(watched.Path + ".hbtmp"));

// someone watches a DIFFERENT item: swap is not delayed
Clear(); state.Reset();
var other = Add(libC, "Other item");
jobs.Add(new[] { new Job { ItemId = other.Id.ToString("N"), Path = other.Path!, Title = "Other item", Profile = jobProfile, IgnoreSchedule = true } });
sess.Playing = true; sess.PlayingItemId = Guid.NewGuid();
var tother = Tick();
Check("different item playing: swap not delayed (job completes)", await tother && jobs.List().First().Status == "Done" && new FileInfo(other.Path!).Length < 600 * 1024);
sess.Playing = false; cfg.PauseWhileStreaming = true; Environment.SetEnvironmentVariable("HB_SLEEP", null);

// cancelling a job that is waiting to swap discards the temp file and leaves the original alone
Clear(); state.Reset(); cfg.PauseWhileStreaming = false;
var wc2 = Add(libC, "Cancel while waiting");
var cj2 = new Job { ItemId = wc2.Id.ToString("N"), Path = wc2.Path!, Title = "x", Profile = jobProfile, IgnoreSchedule = true };
jobs.Add(new[] { cj2 });
Environment.SetEnvironmentVariable("HB_SLEEP", "1");
sess.Playing = true; sess.PlayingItemId = wc2.Id;
var tc2 = Tick();
await Task.Delay(7000);
jobs.Remove(new[] { cj2.Id });
await tc2;
Check("cancel while waiting to swap: Cancelled, original intact, temp removed", jobs.List().First().Status == "Cancelled" && new FileInfo(wc2.Path!).Length == 600 * 1024 && !File.Exists(wc2.Path + ".hbtmp"));
sess.Playing = false; cfg.PauseWhileStreaming = true; Environment.SetEnvironmentVariable("HB_SLEEP", null);

// ======================================================= safety / events / cache / container tests
cfg.PauseWhileStreaming = true; cfg.ScheduleEnabled = false; sess.Playing = false;
cfg.KeepReplacedDays = 0;
var plainDel = new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Container = "mkv" } };
Job Plain(Episode e, ConversionProfile? pr = null) => new Job { ItemId = e.Id.ToString("N"), Path = e.Path!, Title = e.Name!, Profile = pr ?? plainDel, IgnoreSchedule = true };

// ---- 13) size guard
Clear(); state.Reset();
var big = Add(libC, "Already small", ".mkv", 200);                   // fake HB writes ~300 KB > 200 KB
jobs.Add(new[] { Plain(big) });
Check("size guard: tick handled it", await Tick());
var bj = jobs.List().First();
Check("size guard: job Skipped with reason", bj.Status == "Skipped" && bj.Error.Contains("not smaller"));
Check("size guard: original untouched, no temp", new FileInfo(big.Path!).Length == 200 * 1024 && !Directory.GetFiles(media, "*.hbtmp").Any());
Check("size guard: remembered (never retried) and counted as skipped", state.IsProcessed("x", big.Path!) && state.SkippedCount() == 1);
Check("size guard: not counted as converted", state.Stats().Converted == 0);

Clear(); state.Reset();
var bigKeep = Add(libC, "Keep both bigger", ".mkv", 200);
jobs.Add(new[] { Plain(bigKeep, new ConversionProfile { DeleteOriginal = false, HandBrake = new HandBrakeSettings { Container = "mkv" } }) });
await Tick();
Check("size guard: manual job that keeps the original is exempt", jobs.List().First().Status == "Done" && File.Exists(Path.Combine(media, "Keep both bigger - HandBrake.mkv")));

Clear(); state.Reset();
var bigBurn = Add(libC, "Burn bigger", ".mkv", 200);
jobs.Add(new[] { Plain(bigBurn, new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Container = "mkv", SubtitleMode = "burn" } }) });
await Tick();
Check("size guard: burn-in job is exempt (replaced even if bigger)", jobs.List().First().Status == "Done" && new FileInfo(bigBurn.Path!).Length > 250 * 1024);

Clear(); state.Reset();
var autoBig = Add(libA, "Auto small", ".mkv", 200);
Check("size guard (auto): runs", await Tick());
Check("size guard (auto): original kept and not pending again", new FileInfo(autoBig.Path!).Length == 200 * 1024 && backlog.FindPending(cfg, 10).Count == 0);

cfg.MinSavingsPercent = 60;                                          // 600 KB -> 300 KB is only a 50 % saving
Clear(); state.Reset();
var sav = Add(libC, "Savings", ".mkv", 600); jobs.Add(new[] { Plain(sav) });
await Tick();
Check("min savings 60 %: 50 % saving is rejected", jobs.List().First().Status == "Skipped" && new FileInfo(sav.Path!).Length == 600 * 1024);
cfg.MinSavingsPercent = 40; Clear(); state.Reset();
var sav2 = Add(libC, "Savings2", ".mkv", 600); jobs.Add(new[] { Plain(sav2) });
await Tick();
Check("min savings 40 %: 50 % saving accepted", jobs.List().First().Status == "Done" && new FileInfo(sav2.Path!).Length < 600 * 1024);
cfg.MinSavingsPercent = 0;

// ---- 14) duration verification
Clear(); state.Reset();
var dv = Add(libC, "Truncated"); jobs.Add(new[] { Plain(dv) });
Environment.SetEnvironmentVariable("FP_SHORT", "1");
await Tick();
Environment.SetEnvironmentVariable("FP_SHORT", null);
var dj = jobs.List().First();
Check("duration: truncated output -> Failed, nothing replaced", dj.Status == "Failed" && dj.Error.Contains("20s") && dj.Error.Contains("100s") && new FileInfo(dv.Path!).Length == 600 * 1024);
Check("duration: temp file removed, not marked converted", !Directory.GetFiles(media, "*.hbtmp").Any() && !state.IsProcessed("x", dv.Path!));

Clear(); state.Reset();
var dn = Add(libC, "NoProbe"); jobs.Add(new[] { Plain(dn) });
Environment.SetEnvironmentVariable("FP_NONE", "1");
await Tick();
Environment.SetEnvironmentVariable("FP_NONE", null);
Check("duration: ffprobe failing -> original kept, clear message", jobs.List().First().Status == "Failed" && jobs.List().First().Error.Contains("ffprobe") && new FileInfo(dn.Path!).Length == 600 * 1024);

Clear(); state.Reset(); cfg.VerifyDuration = false;
var dx = Add(libC, "Unverified"); jobs.Add(new[] { Plain(dx) });
Environment.SetEnvironmentVariable("FP_SHORT", "1");
await Tick();
Environment.SetEnvironmentVariable("FP_SHORT", null); cfg.VerifyDuration = true;
Check("duration: verification can be switched off", jobs.List().First().Status == "Done");
Check("duration tolerance: 100 vs 102 s ok, 100 vs 90 s not", ResultCheck.DurationsMatch(100, 102) && !ResultCheck.DurationsMatch(100, 90) && ResultCheck.DurationsMatch(5000, 5040) && !ResultCheck.DurationsMatch(5000, 4900));

Clear(); state.Reset();
var dauto = Add(libA, "AutoTruncated");
Environment.SetEnvironmentVariable("FP_SHORT", "1");
await Tick(); await Tick(); await Tick();
Environment.SetEnvironmentVariable("FP_SHORT", null);
Check("duration (auto): retried only MaxFailures times then skipped", state.FailureCount(dauto.Path!) == cfg.MaxFailures && backlog.FindPending(cfg, 10).Count == 0 && new FileInfo(dauto.Path!).Length == 600 * 1024);

// ---- 15) holding folder instead of deleting
foreach (var e in trash.List()) trash.DeleteNow(e.Id);
Clear(); state.Reset();
cfg.KeepReplacedDays = 3;
var keepP = new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Container = "mkv" } };
var tr = Add(libC, "Trash me"); jobs.Add(new[] { Plain(tr, keepP) });
await Tick();
var te = trash.List();
Check("trash: job Done, converted file in place", jobs.List().First().Status == "Done" && new FileInfo(tr.Path!).Length < 600 * 1024);
Check("trash: original held (full size) in the holding folder", te.Count == 1 && File.Exists(te[0].TrashedPath) && new FileInfo(te[0].TrashedPath).Length == 600 * 1024);
Check("holding folder is a hidden folder inside the library folder (same drive as the video), not in the plugin data folder", te.Count == 1 && te[0].TrashedPath.StartsWith(Path.Combine(media, ".hb-originals") + "/") && !Directory.Exists(Path.Combine(data, "plugins", "HandBrakeConverter", "trash")));
Check("holding folder has a .ignore file so Jellyfin skips it", File.Exists(Path.Combine(media, ".hb-originals", ".ignore")));
Check("trash: expiry is ~3 days out", te.Count == 1 && (te[0].Expires - DateTime.Now).TotalDays is > 2.9 and < 3.1);
Check("trash: nothing else left in the library folder", Directory.GetFiles(media).Select(Path.GetFileName).SequenceEqual(new[] { "Trash me.mkv" }));
var tctl = new HandBrakeConverterController(svc, backlog, history, caps, transcodes, loc, state, jobs, trash, lib);
Check("trash: listed by the API", J(tctl.GetTrash()).Contains("Trash me.mkv"));
Check("trash: Status reports count", J(await tctl.Status(CancellationToken.None)).Contains("\"TrashCount\":1"));
var restored = (J(tctl.RestoreTrash(te[0].Id)));
Check("trash: restore puts it beside the converted file as '- Original'", restored.Contains("Trash me - Original.mkv") && File.Exists(Path.Combine(media, "Trash me - Original.mkv")) && new FileInfo(Path.Combine(media, "Trash me - Original.mkv")).Length == 600 * 1024 && trash.List().Count == 0);
Check("trash: restored file is not queued for conversion again", state.IsProcessed("x", Path.Combine(media, "Trash me - Original.mkv")));
Check("trash: restore of unknown id = 404", tctl.RestoreTrash("nope") is NotFoundObjectResult);

Clear(); state.Reset();
var tr2 = Add(libC, "Expire me"); jobs.Add(new[] { Plain(tr2, keepP) });
await Tick();
var ex1 = trash.List().Single(); ex1.Expires = DateTime.Now.AddMinutes(-1);
Check("trash: purge removes expired original and its folder", trash.Purge(true) == 1 && !File.Exists(ex1.TrashedPath) && !Directory.Exists(Path.GetDirectoryName(ex1.TrashedPath)!) && trash.List().Count == 0);
Clear(); state.Reset();
var tr3 = Add(libC, "Delete now"); jobs.Add(new[] { Plain(tr3, keepP) });
await Tick();
var ex2 = trash.List().Single();
Check("trash: 'delete now' removes it", tctl.DeleteTrash(ex2.Id) is OkObjectResult && !File.Exists(ex2.TrashedPath));
Clear(); state.Reset();
cfg.KeepReplacedDays = 0;
var tr4 = Add(libC, "Immediate"); jobs.Add(new[] { Plain(tr4) });
await Tick();
Check("trash: 0 days = deleted immediately, nothing held", trash.List().Count == 0 && new FileInfo(tr4.Path!).Length < 600 * 1024);
Clear(); state.Reset();
cfg.KeepReplacedDays = 5;
var tr5 = Add(libC, "Keep original mode"); jobs.Add(new[] { Plain(tr5, new ConversionProfile { DeleteOriginal = false, HandBrake = new HandBrakeSettings { Container = "mkv" } }) });
await Tick();
Check("trash: keep-original mode never uses the holding folder", trash.List().Count == 0 && File.Exists(tr5.Path!) && new FileInfo(tr5.Path!).Length == 600 * 1024);
// custom holding folder
var customTrash = Path.Combine(root, "mytrash"); cfg.TrashFolder = customTrash;
Clear(); state.Reset();
cfg.KeepReplacedDays = 3;
var tr6 = Add(libC, "Custom trash"); jobs.Add(new[] { Plain(tr6, keepP) });
await Tick();
Check("trash: honours a custom holding folder", trash.List().Single().TrashedPath.StartsWith(customTrash) && File.Exists(trash.List().Single().TrashedPath));
foreach (var e in trash.List()) trash.DeleteNow(e.Id); cfg.TrashFolder = "";
// a trash folder that can't be written -> original must stay, job fails, temp removed
var blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "i am a file, not a folder"); cfg.TrashFolder = Path.Combine(blocked, "sub");
Clear(); state.Reset();
var tr7 = Add(libC, "Unwritable trash"); jobs.Add(new[] { Plain(tr7, keepP) });
await Tick();
Check("trash: unusable holding folder -> original kept, Failed, no temp", new FileInfo(tr7.Path!).Length == 600 * 1024 && !Directory.GetFiles(media, "*.hbtmp").Any() && jobs.List().First().Status != "Done");
cfg.TrashFolder = "";

cfg.KeepReplacedDays = 0;
// holding-folder placement rules
var th = new TrashStore(state, NullLogger<TrashStore>.Instance);
Check("holding folder: inside the matching library folder", th.HoldingFolderFor("", "/mnt/hdd/Movies/A/a.mkv", new[] { "/mnt/hdd/Movies", "/mnt/hdd/Shows" }) == "/mnt/hdd/Movies/.hb-originals");
Check("holding folder: the deepest matching library folder wins", th.HoldingFolderFor("", "/mnt/hdd/Media/Movies/a.mkv", new[] { "/mnt/hdd/Media", "/mnt/hdd/Media/Movies/" }) == "/mnt/hdd/Media/Movies/.hb-originals");
Check("holding folder: no match -> beside the file, never the system disk", th.HoldingFolderFor("", "/mnt/x/y/a.mkv", new[] { "/mnt/hdd/Movies" }) == "/mnt/x/y/.hb-originals");
Check("holding folder: a folder name that only shares a prefix doesn't match", th.HoldingFolderFor("", "/mnt/hdd/Movies2/a.mkv", new[] { "/mnt/hdd/Movies" }) == "/mnt/hdd/Movies2/.hb-originals");
Check("holding folder: a custom folder is used as given", th.HoldingFolderFor("/mnt/custom", "/mnt/hdd/Movies/a.mkv", new[] { "/mnt/hdd/Movies" }) == "/mnt/custom");

// ---- 16) disk space
Clear(); state.Reset();
var ds = Add(libC, "No room"); jobs.Add(new[] { Plain(ds) });
cfg.MinFreeSpaceGb = 100_000_000;                                   // absurd: can never be met
Check("disk space: tick reports no work", !await Tick());
Check("disk space: status explains it", svc.Snapshot().State == "Waiting" && svc.Snapshot().Message.Contains("free disk space"));
Check("disk space: job still queued, original untouched, HandBrake not started", jobs.List().First().Status == "Queued" && new FileInfo(ds.Path!).Length == 600 * 1024 && !File.Exists(argsFile));
cfg.MinFreeSpaceGb = 0;
Check("disk space: runs once there is room", await Tick() && jobs.List().First().Status == "Done");
cfg.MinFreeSpaceGb = 2;

// ---- 17) replacing keeps the original container/name
Clear(); state.Reset(); File.Delete(argsFile);
var mp4 = Add(libC, "Keeps mp4", ".mp4"); jobs.Add(new[] { Plain(mp4) });      // profile says MKV
await Tick();
Check("container: .mp4 replaced in place as MP4 (same path, no .mkv appears)", File.Exists(mp4.Path!) && new FileInfo(mp4.Path!).Length < 600 * 1024 && !File.Exists(Path.ChangeExtension(mp4.Path!, ".mkv")) && Args().Contains("-f av_mp4"));
Clear(); state.Reset(); File.Delete(argsFile);
var m4v = Add(libC, "Keeps m4v", ".m4v"); jobs.Add(new[] { Plain(m4v) });
await Tick();
Check("container: .m4v keeps its extension, written as MP4", File.Exists(m4v.Path!) && new FileInfo(m4v.Path!).Length < 600 * 1024 && Args().Contains("-f av_mp4"));
Clear(); state.Reset(); File.Delete(argsFile);
var mkv2 = Add(libC, "Keeps mkv", ".mkv");
jobs.Add(new[] { Plain(mkv2, new ConversionProfile { DeleteOriginal = true, HandBrake = new HandBrakeSettings { Container = "mp4" } }) });   // profile says MP4
await Tick();
Check("container: .mkv replaced in place as MKV even if the profile says MP4", File.Exists(mkv2.Path!) && new FileInfo(mkv2.Path!).Length < 600 * 1024 && !File.Exists(Path.ChangeExtension(mkv2.Path!, ".mp4")) && Args().Contains("-f av_mkv"));
Clear(); state.Reset(); File.Delete(argsFile);
var avi = Add(libC, "Old avi", ".avi"); jobs.Add(new[] { Plain(avi) });
await Tick();
Check("container: .avi has to change extension (-> .mkv), original gone", !File.Exists(avi.Path!) && File.Exists(Path.ChangeExtension(avi.Path!, ".mkv")));
Clear(); state.Reset(); File.Delete(argsFile);
var mp4k = Add(libC, "Keep mode mp4", ".mp4");
jobs.Add(new[] { Plain(mp4k, new ConversionProfile { DeleteOriginal = false, HandBrake = new HandBrakeSettings { Container = "mkv" } }) });
await Tick();
Check("container: keep-original mode still uses the profile's container", File.Exists(Path.Combine(media, "Keep mode mp4 - HandBrake.mkv")) && Args().Contains("-f av_mkv"));

// ---- 18) cached backlog scan
Clear(); state.Reset();
Add(libA, "Cached 1"); Add(libA, "Cached 2");
backlog.FindPending(cfg, 10);
var q0 = lib.Queries;
for (var i = 0; i < 5; i++) backlog.FindPending(cfg, 10);
Check("cache: repeated polls don't re-query the library", lib.Queries == q0);
var firstP = backlog.FindPending(cfg, 1);
Check("cache: limit still honoured and no extra query", firstP.Count == 1 && lib.Queries == q0);
File.Delete(firstP[0].Path);
Check("cache: a vanished file is dropped without a rescan", backlog.FindPending(cfg, 10).Count == 1 && lib.Queries == q0);
Add(libA, "Cached 3");                                              // raises ItemAdded
var q1 = lib.Queries;
Check("cache: library change triggers one rebuild and sees the new file", backlog.FindPending(cfg, 10).Count == 2 && lib.Queries > q1 - 1 && lib.Queries > q0);
var q2 = lib.Queries;
backlog.FindPending(cfg, 10);
Check("cache: ...and then it is cached again", lib.Queries == q2);
BacklogScanner.QuietSeconds = 3600; lib.Raise();
backlog.FindPending(cfg, 10);
Check("cache: while the library is still busy (events streaming in) no rebuild yet", lib.Queries == q2);
BacklogScanner.QuietSeconds = 0;
backlog.FindPending(cfg, 10);
Check("cache: rebuilds once it has been quiet", lib.Queries > q2);
var q3 = lib.Queries;
cfg.Libraries[2].AutoProcess = true;                                // settings change -> immediate rebuild
backlog.FindPending(cfg, 10); cfg.Libraries[2].AutoProcess = false;
Check("cache: changing settings rebuilds immediately", lib.Queries > q3);
backlog.FindPending(cfg, 10);

// ---- 19) playback events wake the loops at once
var swp = System.Diagnostics.Stopwatch.StartNew();
var pt = watcher.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
await Task.Delay(300); sess.RaiseStart();
await pt;
Check("events: a playback event ends a 30 s wait in about a second", swp.Elapsed < TimeSpan.FromSeconds(3));
swp.Restart();
pt = watcher.WaitAsync(TimeSpan.FromMilliseconds(700), CancellationToken.None);
await pt;
Check("events: without an event the wait runs its normal length", swp.Elapsed >= TimeSpan.FromMilliseconds(650));

Clear(); state.Reset(); cfg.PauseWhileStreaming = true; sess.Playing = false;
var ev = Add(libC, "Event suspend"); jobs.Add(new[] { Plain(ev) });
Environment.SetEnvironmentVariable("HB_SLEEP", "3");
var tev = Tick();
await Task.Delay(1500);
await Task.Delay(100);                                              // monitor is now sleeping up to 3 s between polls
sess.Playing = true; sess.RaiseStart();
await Task.Delay(1000);
Check("events: encode suspended within ~1 s of playback starting (not at the next 3 s poll)", svc.Snapshot().Suspended);
sess.Playing = false; sess.RaiseStop();
await Task.Delay(1000);
Check("events: encode resumed within ~1 s of playback stopping", !svc.Snapshot().Suspended);
await tev; Environment.SetEnvironmentVariable("HB_SLEEP", null);
Check("events: encode completes normally afterwards", jobs.List().First().Status == "Done");

// ---- 19b) Pause / Continue from the Overview tab
Clear(); state.Reset(); cfg.PauseWhileStreaming = false; sess.Playing = false;
var pz = Add(libC, "Pause me"); jobs.Add(new[] { Plain(pz) });
Environment.SetEnvironmentVariable("HB_SLEEP", "3");
var tpz = Tick();
await Task.Delay(1500);
svc.SetPaused(true);
await Task.Delay(1000);
Check("pause: running encode is frozen and says Paused", svc.Snapshot().Suspended && svc.Snapshot().UserPaused && svc.Snapshot().State == "Paused");
Check("pause: persisted in the state store", state.Paused);
svc.SetPaused(false);
await Task.Delay(1000);
Check("continue: encode resumes", !svc.Snapshot().Suspended && !svc.Snapshot().UserPaused && svc.Snapshot().State == "Encoding");
await tpz; Environment.SetEnvironmentVariable("HB_SLEEP", null);
Check("continue: encode completes normally", jobs.List().First().Status == "Done");
var pz2 = Add(libC, "Pause 2"); jobs.Add(new[] { Plain(pz2) });
svc.SetPaused(true);
Check("pause: nothing new starts", !await Tick() && svc.Snapshot().State == "Paused" && jobs.List().First().Status == "Queued");
state.Reset();
Check("pause: survives Reset", state.Paused);
var pctl = new HandBrakeConverterController(svc, backlog, history, caps, transcodes, loc, state, jobs, trash, lib);
pctl.Resume();
Check("API resume clears the pause and the next tick converts", !state.Paused && await Tick() && jobs.List().First().Status == "Done");
pctl.Pause();
Check("API pause sets it", state.Paused && J2(await pctl.Status(CancellationToken.None)).Contains("\"Paused\":true"));
pctl.Resume();
string J2(object? o) => JsonSerializer.Serialize((o as ObjectResult)?.Value);

// ---- 19c) result checks: tracks, chapters, HDR
void Env(string k, string? v) => Environment.SetEnvironmentVariable(k, v);
async Task<(Job Job, Episode Ep)> RunJob(string name, ConversionProfile prof, params (string, string)[] env)
{
    Clear(); state.Reset(); File.Delete(argsFile);
    var e = Add(libC, name); jobs.Add(new[] { Plain(e, prof) });
    foreach (var (k, v) in env) Env(k, v);
    await Tick();
    foreach (var (k, _) in env) Env(k, null);
    return (jobs.List().First(), e);
}
ConversionProfile Prof(Action<HandBrakeSettings>? tweak = null, bool replace = true, string container = "mkv") { var pr = new ConversionProfile { DeleteOriginal = replace, HandBrake = new HandBrakeSettings { Container = container } }; tweak?.Invoke(pr.HandBrake); return pr; }
bool Untouched(Episode e) => new FileInfo(e.Path!).Length == 600 * 1024 && !Directory.GetFiles(media, "*.hbtmp").Any();

var (jt1, et1) = await RunJob("Lost audio", Prof(), ("FP_OUT_AUDIO", "1"));
Check("tracks: a dropped audio track -> Failed, original untouched", jt1.Status == "Failed" && jt1.Error.Contains("audio track") && Untouched(et1));
var (jt2, et2) = await RunJob("Lost subs", Prof(), ("FP_OUT_SUBS", "0"));
Check("tracks: dropped subtitles when keeping them -> Failed", jt2.Status == "Failed" && jt2.Error.Contains("subtitle track") && Untouched(et2));
var (jt3, _) = await RunJob("Burned", Prof(h => h.SubtitleMode = "burn"), ("FP_OUT_SUBS", "0"));
Check("tracks: burn-in legitimately has fewer subtitle tracks", jt3.Status == "Done");
var (jt4, _) = await RunJob("Mp4 subs", Prof(null, false, "mp4"), ("FP_OUT_SUBS", "0"));
Check("tracks: MP4 output may drop picture subtitles", jt4.Status == "Done");
var (jt5, et5) = await RunJob("Lost chapters", Prof(), ("FP_OUT_CHAPTERS", "0"));
Check("chapters: lost chapters -> Failed, original untouched", jt5.Status == "Failed" && jt5.Error.Contains("chapter") && Untouched(et5));
var (jt6, _) = await RunJob("No chapters", Prof(), ("FP_CHAPTERS", "0"));
Check("chapters: a source without chapters is fine", jt6.Status == "Done");

var (h1, he1) = await RunJob("HDR default", Prof(), ("FP_HDR", "1"));
Check("hdr: default leaves HDR videos alone (Skipped, HandBrake never ran)", h1.Status == "Skipped" && h1.Error.Contains("HDR") && Untouched(he1) && Args() == "");
Check("hdr: skipped file is remembered and counted", state.IsProcessed("x", he1.Path!) && state.SkippedCount() == 1);
var (h2, _) = await RunJob("HDR keep", Prof(h => h.HdrMode = "keep"), ("FP_HDR", "1"));
Check("hdr keep: converted with the 10-bit encoder", h2.Status == "Done" && Args().Contains("-e x265_10bit"));
var (h3, h3e) = await RunJob("HDR lost", Prof(h => h.HdrMode = "keep"), ("FP_HDR", "1"), ("FP_OUT_SDR", "1"));
Check("hdr keep: result that lost HDR is discarded", h3.Status == "Failed" && h3.Error.Contains("HDR") && Untouched(h3e));
var (h4, h4e) = await RunJob("HDR 8bit", Prof(h => h.HdrMode = "keep"), ("FP_HDR", "1"), ("FP_OUT_8BIT", "1"));
Check("hdr keep: 8-bit result is discarded", h4.Status == "Failed" && Untouched(h4e));
var (h5, h5e) = await RunJob("Dolby", Prof(h => h.HdrMode = "keep"), ("FP_HDR", "1"), ("FP_DV", "1"));
Check("hdr keep: Dolby Vision is left alone", h5.Status == "Skipped" && h5.Error.Contains("Dolby Vision") && Untouched(h5e));
var (h6, _) = await RunJob("HDR h264", Prof(h => { h.HdrMode = "keep"; h.Encoder = "x264"; }), ("FP_HDR", "1"));
Check("hdr keep: an encoder without 10-bit is left alone", h6.Status == "Skipped" && h6.Error.Contains("can't keep HDR"));
var (h7, _) = await RunJob("HDR av1", Prof(h => { h.HdrMode = "keep"; h.Encoder = "svt_av1"; }), ("FP_HDR", "1"));
Check("hdr keep: missing 10-bit encoder is explained", h7.Status == "Skipped" && h7.Error.Contains("svt_av1_10bit"));
var (h8, _) = await RunJob("HDR ignore", Prof(h => h.HdrMode = "ignore"), ("FP_HDR", "1"));
Check("hdr ignore: converted like any other file", h8.Status == "Done" && Args().Contains("-e x265 ") && !Args().Contains("_10bit"));
var (h9, _) = await RunJob("SDR", Prof(), ("FP_AUDIO", "2"));
Check("hdr: normal videos are unaffected by the HDR setting", h9.Status == "Done");

// ---- 19d) encoder must exist in this HandBrake
var (en1, ene1) = await RunJob("No nvenc", Prof(h => h.Encoder = "nvenc_h265"));
Check("encoder: unavailable encoder -> job Failed with a clear reason, HandBrake never ran", en1.Status == "Failed" && en1.Error.Contains("nvenc_h265") && en1.Error.Contains("not available") && Untouched(ene1) && Args() == "");
Clear(); state.Reset(); File.Delete(argsFile);
cfg.Defaults.HandBrake.Encoder = "nvenc_h265";
var enA = Add(libA, "Auto no nvenc");
var enRan = await Tick();
cfg.Defaults.HandBrake.Encoder = "x265";
Check("encoder: auto-convert reports it once and does NOT count a failure against the file", !enRan && svc.Snapshot().State == "Error" && svc.Snapshot().Message.Contains("not available") && state.FailureCount(enA.Path!) == 0 && Args() == "");

// ---- 19e) history + retry
Check("history: records converted / skipped / failed", history.List(500).Any(x => x.Result == "Converted") && history.List(500).Any(x => x.Result == "Skipped") && history.List(500).Any(x => x.Result == "Failed"));
var okEntry = history.List(500).First(x => x.Result == "Converted");
Check("history: converted entry has sizes, time and source", okEntry.OldBytes == 600 * 1024 && okEntry.NewBytes > 0 && okEntry.NewBytes < okEntry.OldBytes && okEntry.Seconds > 0 && okEntry.Source == "queue");
var (hj, hje) = await RunJob("Retry me", Prof(), ("FP_OUT_AUDIO", "1"));
var failEntry = history.List(1).First();
Check("history: failure entry carries the reason", failEntry.Result == "Failed" && failEntry.Detail.Contains("audio track") && failEntry.JobId == hj.Id);
Check("history: retry puts the job back in the queue", pctl.RetryHistory(failEntry.Id) is OkObjectResult && jobs.List().First(j => j.Id == hj.Id).Status == "Queued");
Check("history: retry of a missing job explains itself", (jobs.Remove(new[] { hj.Id }) == 1) && pctl.RetryHistory(failEntry.Id) is NotFoundObjectResult);
Check("history: only failures can be retried", pctl.RetryHistory(okEntry.Id) is NotFoundObjectResult && pctl.RetryHistory("nope") is NotFoundObjectResult);
Clear(); state.Reset();
var haf = Add(libA, "Auto fail"); Env("HB_FAIL", "1"); await Tick(); Env("HB_FAIL", null);
var autoFail = history.List(1).First();
Check("history: auto failure recorded as auto", autoFail.Result == "Failed" && autoFail.Source == "auto" && state.FailureCount(haf.Path!) == 1);
Check("history: retry of an auto failure clears its failure count", pctl.RetryHistory(autoFail.Id) is OkObjectResult && state.FailureCount(haf.Path!) == 0);
Check("history: Clear empties it", pctl.ClearHistory() is OkObjectResult && history.List(500).Count == 0);
Check("history: persisted across restart", new HistoryStore(state, NullLogger<HistoryStore>.Instance).List(5).Count == 0);

// ---- 19f) encoder list + test button
Check("encoders: help text parsed", string.Join(",", HandBrakeCapabilities.ParseEncoders("   -e, --encoder <string>  Select video encoder:\n      x264\n      x265_10bit\n       --encoder-preset <string>\n")) == "x264,x265_10bit");
var encJson = JsonSerializer.Serialize((await pctl.Encoders(CancellationToken.None) as ObjectResult)?.Value);
Check("encoders: API lists what this HandBrake offers", encJson.Contains("x265_10bit") && encJson.Contains("svt_av1") && !encJson.Contains("nvenc"));
var okTest = JsonSerializer.Serialize((await pctl.TestEncoder("x265", CancellationToken.None) as ObjectResult)?.Value);
Check("encoders: Test makes a sample encode and reports success", okTest.Contains("\"Ok\":true"));
Env("HB_FAIL", "1");
var badTest = JsonSerializer.Serialize((await pctl.TestEncoder("x265", CancellationToken.None) as ObjectResult)?.Value);
Env("HB_FAIL", null);
Check("encoders: a failing encoder is reported", badTest.Contains("\"Ok\":false") && badTest.Contains("boom"));
Check("encoders: test refuses an encoder HandBrake doesn't list", await pctl.TestEncoder("nvenc_h265", CancellationToken.None) is BadRequestObjectResult && await pctl.TestEncoder("x265;rm", CancellationToken.None) is BadRequestObjectResult);
Check("encoders: test leaves no files behind", !Directory.Exists(Path.Combine(state.DataDir, "encoder-test")));

// ---- 19g) ffprobe JSON parsing
var mi = MediaProbe.Parse("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"mjpeg\",\"disposition\":{\"attached_pic\":1}},{\"codec_type\":\"video\",\"codec_name\":\"hevc\",\"pix_fmt\":\"yuv420p10le\",\"color_transfer\":\"smpte2084\",\"side_data_list\":[{\"side_data_type\":\"DOVI configuration record\"}]},{\"codec_type\":\"audio\"},{\"codec_type\":\"audio\"},{\"codec_type\":\"subtitle\"}],\"chapters\":[{},{}],\"format\":{\"duration\":\"61.5\"}}")!;
Check("probe: tracks, chapters, HDR, Dolby Vision and cover art are read right", mi.AudioTracks == 2 && mi.SubtitleTracks == 1 && mi.Chapters == 2 && mi.VideoCodec == "hevc" && mi.IsHdr && mi.DolbyVision && mi.BitDepth == 10 && Math.Abs(mi.Duration - 61.5) < 0.01);
Check("probe: no duration -> unreadable", MediaProbe.Parse("{\"streams\":[],\"format\":{}}") == null);

// ---- 19h) Jellyfin transcode logs -> suggestions
string MakeLog(string path, string vfmt, int sw, int sh, string audioIn, string vEnc, string aEnc, int dh, string extra = "", bool noVideo = false)
{
    var vLine = noVideo ? "" : $"  Stream #0:0: Video: {vfmt}, {sw}x{sh} [SAR 1:1 DAR 16:9], 24 fps, 24 tbr, 1k tbn\n";
    var aIdx = noVideo ? 0 : 1;
    return $"ffmpeg -analyzeduration 200M -i file:\"{path}\" -map_metadata -1 -map_chapters -1 -threads 0 {(noVideo ? "" : "-map 0:0 ")}-map 0:{aIdx} -map -0:s -codec:v:0 {vEnc} {extra} -codec:a:0 {aEnc} -f mpegts out.ts\n\n" +
        "ffmpeg version 7.0.2-Jellyfin Copyright (c) 2000-2024 the FFmpeg developers\n  libavutil      59.  8.100\n" +
        $"Input #0, matroska,webm, from 'file:{path}':\n  Metadata:\n    title : x\n  Duration: 01:30:00.00, start: 0.000000, bitrate: 20000 kb/s\n{vLine}" +
        $"  Stream #0:{aIdx}(eng): Audio: {audioIn}, 48000 Hz, 5.1(side), fltp (default)\n  Stream #0:{aIdx + 1}(eng): Subtitle: hdmv_pgs_subtitle (pgssub)\n" +
        "Stream mapping:\n  Stream #0:0 -> #0:0 (hevc (native) -> h264 (libx264))\nPress [q] to stop, [?] for help\n" +
        $"Output #0, mpegts, to 'out.ts':\n{(noVideo ? "" : $"  Stream #0:0: Video: h264, yuv420p, {sh * 0 + dh * 16 / 9}x{dh} [SAR 1:1 DAR 16:9], q=2-31, 24 fps, 90k tbn\n")}  Stream #0:{aIdx}: Audio: aac (LC), 48000 Hz, stereo, fltp, 192 kb/s\nframe=  100 fps= 30 q=28.0 size=1000kB time=00:00:04.00 bitrate=2000.0kbits/s speed=1.2x\r";
}
const string HdrIn = "hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084, progressive)";
var logDir = Path.Combine(data, "log"); Directory.CreateDirectory(logDir);
void WriteLog(string prefix, DateTime t, string text) { var n = $"FFmpeg.{prefix}-{t:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}.log"; File.WriteAllText(Path.Combine(logDir, n), text); File.SetLastWriteTime(Path.Combine(logDir, n), DateTime.Now.AddHours(-2)); }

var one = TranscodeLog.Parse(MakeLog("/media/Movies/Heat.mkv", HdrIn, 3840, 2160, "truehd", "libx264", "aac", 1080, "-vf \"tonemapx=tonemap=bt2390\""), "FFmpeg.Transcode-2026-10-29_10-00-00_abc.log", DateTime.Now)!;
Check("transcode log: source, target, HDR, tone-mapping and audio read from the real format", one.Path == "/media/Movies/Heat.mkv" && one.SrcVideo == "hevc" && one.SrcHdr && one.SrcHeight == 2160 && one.VideoTranscoded && one.DstVideo == "h264" && one.DstHeight == 1080 && one.SrcAudio == "truehd" && one.AudioTranscoded && one.DstAudio == "aac" && one.Tonemapped && !one.BurnedSubtitles && !one.Hardware && one.Time == new DateTime(2026, 10, 29, 10, 0, 0));
var burn = TranscodeLog.Parse(MakeLog("/media/overlay words/X.mkv", "h264 (High), yuv420p(progressive)", 1920, 1080, "aac", "h264_nvenc", "copy", 720, "-filter_complex \"[0:0][0:2]overlay\""), "FFmpeg.Transcode-x.log", DateTime.Now)!;
Check("transcode log: burned subtitles and hardware encoder detected, audio copy, path words ignored", burn.BurnedSubtitles && burn.Hardware && !burn.AudioTranscoded && burn.DstVideo == "h264" && !burn.Tonemapped);
var plainPath = TranscodeLog.Parse(MakeLog("/media/tonemap overlay/Y.mkv", "h264 (High), yuv420p(progressive)", 1920, 1080, "aac", "libx264", "aac", 720), "FFmpeg.Transcode-y.log", DateTime.Now)!;
Check("transcode log: words in the file's own path don't count as filters", !plainPath.Tonemapped && !plainPath.BurnedSubtitles);
var remux = TranscodeLog.Parse(MakeLog("/media/R.mkv", "h264 (High), yuv420p(progressive)", 1920, 1080, "dts (DTS-HD MA)", "copy", "aac", 0), "FFmpeg.Remux-r.log", DateTime.Now)!;
Check("remux log: video copied, only the audio re-encoded", !remux.VideoTranscoded && remux.AudioTranscoded && remux.SrcAudio == "dts" && remux.DstVideo == "");
Check("direct stream (both copied) and music are not counted", TranscodeLog.Parse(MakeLog("/media/D.mkv", "h264 (High), yuv420p", 1920, 1080, "aac", "copy", "copy", 0), "FFmpeg.DirectStream-d.log", DateTime.Now) == null
    && TranscodeLog.Parse(MakeLog("/music/a.flac", "", 0, 0, "flac", "copy", "aac", 0, noVideo: true), "FFmpeg.Transcode-m.log", DateTime.Now) == null);
Check("transcode log: garbage and half-written logs are ignored", TranscodeLog.Parse("hello", "x", DateTime.Now) == null && TranscodeLog.Parse("ffmpeg -i file:\"/a.mkv\" -codec:v:0 libx264\n\nffmpeg version 7\n", "x", DateTime.Now) == null);

var t0 = DateTime.Now.AddHours(-5);
for (var i = 0; i < 4; i++) WriteLog("Transcode", t0.AddMinutes(i * 30), MakeLog($"/media/Movies/Film{i}.mkv", HdrIn, 3840, 2160, "truehd", "libx264", "aac", 1080, i < 3 ? "-vf \"tonemapx=tonemap=bt2390\"" : ""));
WriteLog("Transcode", t0.AddMinutes(4), MakeLog("/media/Movies/Film0.mkv", HdrIn, 3840, 2160, "truehd", "libx264", "aac", 1080));   // a seek in Film0 minutes later: same playback
WriteLog("Transcode", t0.AddHours(3), MakeLog("/media/Movies/Film0.mkv", HdrIn, 3840, 2160, "truehd", "libx264", "aac", 1080));       // watched again later: counts
WriteLog("Remux", t0.AddHours(1), MakeLog("/media/Shows/S1E1.mkv", "h264 (High), yuv420p", 1920, 1080, "dts", "copy", "aac", 0));
WriteLog("DirectStream", t0, MakeLog("/media/Shows/S1E2.mkv", "h264 (High), yuv420p", 1920, 1080, "aac", "copy", "copy", 0));
File.WriteAllText(Path.Combine(logDir, "jellyfin20261029.log"), "main log, never read");
Check("scan: reads the new playback logs only", transcodes.Scan() == 7 && transcodes.Sessions(30).Count == 7);
Check("scan: nothing new the second time", transcodes.Scan() == 0);
Check("scan: summaries survive a restart (and Jellyfin deleting the logs)", ((Func<bool>)(() => { foreach (var f in Directory.GetFiles(logDir, "FFmpeg.*")) File.Delete(f); return new TranscodeStore(new Paths(data), state, NullLogger<TranscodeStore>.Instance).Sessions(30).Count == 7; }))());

var ins = TranscodeInsights.Analyze(transcodes.Sessions(30), 30);
Check("insights: seeks collapse into one playback, repeat viewings count", ins.Sessions == 6 && ins.VideoTranscodes == 5 && ins.AudioTranscodes == 6);
var sug = ins.Suggestions.ToDictionary(x => x.Key, x => x.Value);
Check("insights: suggests the format clients keep receiving (x264), the resolution they get (1080p) and AAC audio", sug.GetValueOrDefault("hb.Encoder") == "x264" && sug.GetValueOrDefault("hb.MaxHeight") == "1080" && sug.GetValueOrDefault("hb.AudioMode") == "aac");
Check("insights: reasons are explained with counts", ins.Suggestions.All(x => x.Reason.Length > 40) && ins.Suggestions.First(x => x.Key == "hb.Encoder").Reason.Contains("5 of 5"));
Check("insights: routes and most-transcoded files", ins.Routes.First().Name == "HEVC HDR 2160p → H.264 1080p" && ins.Routes.First().Count == 5 && ins.TopFiles.First().Path == "/media/Movies/Film0.mkv" && ins.TopFiles.First().Count == 2 && ins.TopFiles.First().Why.Contains("video"));
Check("insights: HDR tone-mapping is called out", ins.Notes.Any(n => n.Contains("tone-mapped") && n.Contains("2 of 5")));
Check("insights: too little data -> no suggestions", TranscodeInsights.Analyze(transcodes.Sessions(30).Take(2), 30).Suggestions.Count == 0);
Check("insights: nothing transcoded -> empty", TranscodeInsights.Analyze(new List<TranscodeSession>(), 30).Sessions == 0);
var hevcTarget = Enumerable.Range(0, 4).Select(i => new TranscodeSession { Id = "h" + i, Path = "/p" + i, Time = DateTime.Now, SrcVideo = "h264", SrcHeight = 1080, VideoTranscoded = true, DstVideo = "hevc", DstHeight = 1080 }).ToList();
var hs2 = TranscodeInsights.Analyze(hevcTarget, 30).Suggestions.ToDictionary(x => x.Key, x => x.Value);
Check("insights: HEVC clients -> x265, no resolution or audio suggestion when nothing is scaled or re-encoded", hs2.GetValueOrDefault("hb.Encoder") == "x265" && !hs2.ContainsKey("hb.MaxHeight") && !hs2.ContainsKey("hb.AudioMode"));
var ictl = JsonSerializer.Serialize((pctl.Insights(30) as ObjectResult)?.Value);
Check("insights API: returns the analysis", ictl.Contains("\"Suggestions\"") && ictl.Contains("hb.Encoder") && ictl.Contains(logDir.Replace("/", "\\/")) || ictl.Contains(logDir));

// ---- 20) "same as source" container in keep-original mode
cfg.KeepReplacedDays = 0;
Clear(); state.Reset(); File.Delete(argsFile);
var smp4 = Add(libC, "Same container mp4", ".mp4");
jobs.Add(new[] { Plain(smp4, new ConversionProfile { DeleteOriginal = false, HandBrake = new HandBrakeSettings { Container = "source" } }) });
await Tick();
Check("same as source (keep mode): .mp4 -> '- HandBrake.mp4' written as MP4", File.Exists(Path.Combine(media, "Same container mp4 - HandBrake.mp4")) && Args().Contains("-f av_mp4"));
Clear(); state.Reset(); File.Delete(argsFile);
var smkv = Add(libC, "Same container mkv", ".mkv");
jobs.Add(new[] { Plain(smkv, new ConversionProfile { DeleteOriginal = false, HandBrake = new HandBrakeSettings { Container = "source" } }) });
await Tick();
Check("same as source (keep mode): .mkv -> '- HandBrake.mkv'", File.Exists(Path.Combine(media, "Same container mkv - HandBrake.mkv")) && Args().Contains("-f av_mkv"));
Clear(); state.Reset(); File.Delete(argsFile);
var savi = Add(libC, "Same container avi", ".avi");
jobs.Add(new[] { Plain(savi, new ConversionProfile { DeleteOriginal = false, HandBrake = new HandBrakeSettings { Container = "source" } }) });
await Tick();
Check("same as source: AVI can't be kept -> MKV", File.Exists(Path.Combine(media, "Same container avi - HandBrake.mkv")));
Check("default container is 'same as source'", new HandBrakeSettings().Container == "source");

// ---- 22) not time-limited: runs any time, blackout dates and empty windows are irrelevant
Clear(); state.Reset(); File.Delete(argsFile);
cfg.ScheduleEnabled = false; cfg.Windows = Array.Empty<ScheduleWindow>();
cfg.Blackouts = new[] { new BlackoutRange { From = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"), To = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd") } };
Check("not time-limited: ScheduleEvaluator allows now", ScheduleEvaluator.IsAllowed(DateTime.Now, cfg));
var anyAuto = Add(libA, "Any time auto");
Check("not time-limited: auto-convert runs (no windows, blackout covers today)", await Tick() && new FileInfo(anyAuto.Path!).Length < 600 * 1024);
var anyJob = Add(libC, "Any time manual");
jobs.Add(new[] { new Job { ItemId = "q", Path = anyJob.Path!, Title = "x", Profile = plainDel } });   // NOT 'run now'
Check("not time-limited: normal queue job runs without 'start now'", await Tick() && jobs.List().First().Status == "Done");
Clear(); state.Reset();
var anyMid = Add(libC, "Any time midway");
jobs.Add(new[] { new Job { ItemId = "q2", Path = anyMid.Path!, Title = "x", Profile = plainDel } });
cfg.FinishCurrentAfterWindow = false; Environment.SetEnvironmentVariable("HB_SLEEP", "2");
Check("not time-limited: encode isn't cancelled mid-way by the (non-existent) window", await Tick() && jobs.List().First().Status == "Done");
Environment.SetEnvironmentVariable("HB_SLEEP", null); cfg.FinishCurrentAfterWindow = true;
cfg.Blackouts = Array.Empty<BlackoutRange>();

// ---- 23) skip small files is a global setting
Clear(); state.Reset();
Add(libA, "Small one", ".mkv", 600);
cfg.MinSizeMb = 1;
Check("min size (global): 600 KB file skipped when limit is 1 MB", backlog.FindPending(cfg, 10).Count == 0);
cfg.MinSizeMb = 0;
Check("min size (global): ...and picked up when limit is off", backlog.FindPending(cfg, 10).Count == 1);

// ---- 10) series expansion + controller (Browse / Enqueue / Queue / Cancel)
Clear(); state.Reset();
var series = new Series { Id = Guid.NewGuid(), Name = "Cowboy Bebop", ProductionYear = 1998 };
var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1 };
lib.ById[series.Id] = series; lib.ById[season.Id] = season;
lib.Parent[libB] = new List<BaseItem> { series, new Movie { Id = Guid.NewGuid(), Name = "Heat", ProductionYear = 1995, Path = "/x/heat.mkv" } };
lib.Parent[series.Id] = new List<BaseItem> { season };
var ep1 = new Episode { Id = Guid.NewGuid(), Name = "Asteroid Blues", IndexNumber = 1, Path = Path.Combine(media, "ep1.mkv") };
var ep2 = new Episode { Id = Guid.NewGuid(), Name = "Stray Dog Strut", IndexNumber = 2, Path = Path.Combine(media, "ep2.mkv") };
File.WriteAllBytes(ep1.Path, new byte[700 * 1024]); File.WriteAllBytes(ep2.Path, new byte[700 * 1024]);
lib.Parent[season.Id] = new List<BaseItem> { ep2, ep1 };
// recursive (series -> all episodes) is what ExpandTargets asks for
lib.Recursive[series.Id] = new List<BaseItem> { ep1, ep2 };
lib.ById[ep1.Id] = ep1; lib.ById[ep2.Id] = ep2;

var ctl = new HandBrakeConverterController(svc, backlog, history, caps, transcodes, loc, state, jobs, trash, lib);
string J(object? o) => JsonSerializer.Serialize((o as ObjectResult)?.Value);
var top = J(ctl.Browse(libB.ToString(), null, null));
Check("browse library lists series + movie", top.Contains("Cowboy Bebop") && top.Contains("\"Kind\":\"Series\"") && top.Contains("Heat"));
Check("browse search filters", !J(ctl.Browse(libB.ToString(), null, "bebop")).Contains("Heat"));
Check("browse series -> seasons", J(ctl.Browse(null, series.Id.ToString(), null)).Contains("\"Kind\":\"Season\""));
var eps = J(ctl.Browse(null, season.Id.ToString(), null));
Check("browse season -> episodes in order", eps.IndexOf("Asteroid") < eps.IndexOf("Stray") && eps.Contains("\"Kind\":\"Episode\""));
Check("browse needs a parent", ctl.Browse(null, null, null) is BadRequestObjectResult);

var req = new QueueRequest { ItemIds = new[] { series.Id.ToString("N") }, Profile = jobProfile, IgnoreSchedule = true };
var qr = J(ctl.Enqueue(req));
Console.WriteLine("enqueue(series): " + qr);
Check("enqueue series = 2 episode jobs", qr.Contains("\"Added\":2"));
Check("enqueue again = deduped", J(ctl.Enqueue(req)).Contains("\"AlreadyQueued\":2"));
var ql = J(ctl.GetQueue());
Check("queue lists options", ql.Contains("Asteroid Blues") && ql.Contains("\"MaxHeight\":480") && ql.Contains("\"Subtitles\":\"burn\""));
// reorder
var ids0 = jobs.List().Select(j => j.Id).ToList();
Check("move: unknown id = 404", ctl.MoveJob("nope", "up") is NotFoundResult);
ctl.MoveJob(ids0[1], "top");
Check("move top: second job runs first", jobs.List().First().Id == ids0[1]);
ctl.MoveJob(ids0[1], "down");
Check("move down: back to original order", jobs.List().Select(j => j.Id).SequenceEqual(ids0));
ctl.MoveJob(ids0[0], "up");
Check("move up at the top is a no-op", jobs.List().First().Id == ids0[0]);
ctl.MoveJob(ids0[0], "bottom");
Check("move bottom", jobs.List().Last().Id == ids0[0]);
Check("next job follows the new order", jobs.Next(_ => true)!.Id == ids0[1]);
// multi-remove
Check("remove several at once", ctl.RemoveJobs(new RemoveRequest { Ids = new[] { ids0[0], ids0[1], "nope" } }) is OkObjectResult && jobs.List().Count == 0);
jobs.Add(new[] { new Job { ItemId = "m1", Path = "/x/1.mkv", Title = "A", Profile = jobProfile }, new Job { ItemId = "m2", Path = "/x/2.mkv", Title = "B", Profile = jobProfile }, new Job { ItemId = "m3", Path = "/x/3.mkv", Title = "C", Profile = jobProfile } });
var mq = jobs.List().Select(j => j.Id).ToList();
jobs.SetStatus(mq[0], "Failed", "x");
ctl.MoveJob(mq[2], "up");
Check("finished jobs sink below the queue and don't affect moves", jobs.List().Select(j => j.Title).SequenceEqual(new[] { "C", "B", "A" }));
ctl.RemoveJobs(new RemoveRequest { Ids = new[] { mq[0] } });
Check("remove a finished job", jobs.List().Count == 2);
jobs.Remove(jobs.List().Select(j => j.Id));
Check("enqueue nothing = 400", ctl.Enqueue(new QueueRequest()) is BadRequestObjectResult);

// ---- 11) persistence: queue survives restart, Running -> Queued
var jobs2 = new JobStore(state, NullLogger<JobStore>.Instance);
Check("jobs persisted across restart", jobs2.List().Count == jobs.List().Count);

// ---- 12) finding HandBrake (no installing: configured path, then the server's PATH)
var binDir = Path.Combine(root, "bin"); Directory.CreateDirectory(binDir);
var oldPath = Environment.GetEnvironmentVariable("PATH");
Environment.SetEnvironmentVariable("PATH", binDir);   // nothing else on PATH, so "not installed" is real
HandBrakeLocator.FallbackDirs = new string[0];
Check("locator: nothing configured and nothing on PATH -> not found", loc.Resolve("") == null && loc.Resolve(Path.Combine(root, "nope")) == null);
var onPath = Path.Combine(binDir, "HandBrakeCLI");
File.Copy(fake, onPath); File.SetUnixFileMode(onPath, (UnixFileMode)0b111_101_101);
var found = loc.Resolve("");
Check("locator: HandBrakeCLI on PATH is found", found != null && found.FileName == onPath && found.Description == "installed on this server");
var viaCfg = loc.Resolve(fake);
Check("locator: a configured path wins over PATH", viaCfg != null && viaCfg.FileName == fake && viaCfg.Description == "path set in the plugin");
Check("locator: a bad configured path falls back to PATH", loc.Resolve(Path.Combine(root, "nope"))?.FileName == onPath);
Check("locator: reports the version", (await HandBrakeLocator.VersionAsync(found!, CancellationToken.None))?.StartsWith("HandBrake 1.9.0") == true);
cfg.HandBrakePath = "";
Clear(); state.Reset();
var fx = Add(libA, "ViaPath");
Check("convert using HandBrake from PATH", await Tick() && new FileInfo(fx.Path!).Length < 600 * 1024);
File.Delete(onPath);
Clear(); state.Reset();
var fy = Add(libA, "NoHandBrake");
Check("not installed: nothing runs, status says how to install", !await Tick() && svc.Snapshot().Message.Contains("apt install handbrake-cli") && new FileInfo(fy.Path!).Length == 600 * 1024);
var sctl = new HandBrakeConverterController(svc, backlog, history, caps, transcodes, loc, state, jobs, trash, lib);
Check("not installed: Status reports HandBrakeFound=false", J(await sctl.Status(CancellationToken.None)).Contains("\"HandBrakeFound\":false"));
File.Copy(fake, onPath); File.SetUnixFileMode(onPath, (UnixFileMode)0b111_101_101);
Check("installed: Status reports found with path", J(await sctl.Status(CancellationToken.None)).Contains("\"HandBrakeFound\":true") && J(await sctl.Status(CancellationToken.None)).Contains(onPath));
Environment.SetEnvironmentVariable("PATH", oldPath);
HandBrakeLocator.FallbackDirs = new[] { "/usr/bin", "/usr/local/bin", "/bin" };

Directory.Delete(root, true);
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;

class Paths : IApplicationPaths { public Paths(string d) { DataPath = d; } public string DataPath { get; } public string LogDirectoryPath => System.IO.Path.Combine(DataPath, "log"); }
static class Ext { public static List<BaseItem> GetOrAdd(this Dictionary<Guid, List<BaseItem>> d, Guid k) { if (!d.TryGetValue(k, out var l)) d[k] = l = new List<BaseItem>(); return l; } }
class FakeEncoder : MediaBrowser.Controller.MediaEncoding.IMediaEncoder { public FakeEncoder(string p) { ProbePath = p; } public string EncoderPath => ""; public string ProbePath { get; } }
class FakeLib : ILibraryManager
{
    public event EventHandler<ItemChangeEventArgs>? ItemAdded; public event EventHandler<ItemChangeEventArgs>? ItemUpdated; public event EventHandler<ItemChangeEventArgs>? ItemRemoved;
    public int Queries;
    public void Raise() { ItemAdded?.Invoke(this, new ItemChangeEventArgs()); }
    public Dictionary<Guid, List<BaseItem>> Parent = new();
    public Dictionary<Guid, List<BaseItem>> Recursive = new();   // when a recursive query for a series is made
    public Dictionary<Guid, BaseItem> ById = new();
    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery q)
    {
        Queries++;
        if (q.Recursive && Recursive.TryGetValue(q.ParentId, out var r)) return r.ToList();
        return Parent.TryGetValue(q.ParentId, out var l) ? l.ToList() : new List<BaseItem>();
    }
    public BaseItem? GetItemById(Guid id) => ById.TryGetValue(id, out var i) ? i : null;
    public void QueueLibraryScan() { }
    public string[] Roots = System.Array.Empty<string>();
    public List<MediaBrowser.Model.Entities.VirtualFolderInfo> GetVirtualFolders() => new() { new MediaBrowser.Model.Entities.VirtualFolderInfo { Locations = Roots } };
}
class FakeSessions : ISessionManager
{
    public event EventHandler<PlaybackProgressEventArgs>? PlaybackStart; public event EventHandler<PlaybackProgressEventArgs>? PlaybackProgress; public event EventHandler<PlaybackStopEventArgs>? PlaybackStopped;
    public void RaiseStart() => PlaybackStart?.Invoke(this, new PlaybackProgressEventArgs());
    public void RaiseStop() => PlaybackStopped?.Invoke(this, new PlaybackStopEventArgs());
    public bool Playing, Paused, IdleClient, Audio;
    public Guid PlayingItemId = Guid.NewGuid();
    public IEnumerable<SessionInfo> Sessions
    {
        get
        {
            var l = new List<SessionInfo>();
            if (IdleClient) l.Add(new SessionInfo { NowPlayingItem = null, PlayState = new PlayState() });
            if (Playing) l.Add(new SessionInfo { NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = PlayingItemId, MediaType = Audio ? Jellyfin.Data.Enums.MediaType.Audio : Jellyfin.Data.Enums.MediaType.Video }, PlayState = new PlayState { IsPaused = Paused } });
            return l;
        }
    }
}
