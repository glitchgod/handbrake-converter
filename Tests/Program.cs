using Jellyfin.Plugin.HandBrakeConverter.Configuration;
using Jellyfin.Plugin.HandBrakeConverter.Services;

int fails = 0;
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }

// 2026-10-23 is a Friday (5). 2026-10-24 Saturday (6). 2026-10-25 Sunday (0).
var cfg = new PluginConfiguration
{
    Windows = new[] { new ScheduleWindow { Days = new[] { 5 }, Start = "23:00", End = "10:00" } }
};
Check("Fri 23:30 allowed", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,23,30,0), cfg));
Check("Sat 09:59 allowed (spill from Fri)", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,24,9,59,0), cfg));
Check("Sat 10:00 blocked", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,24,10,0,0), cfg));
Check("Sat 23:30 blocked (Sat not selected)", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,24,23,30,0), cfg));
Check("Fri 09:00 blocked (Thu not selected)", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,9,0,0), cfg));
Check("Fri 22:59 blocked", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,22,59,0), cfg));

var day = new PluginConfiguration { Windows = new[] { new ScheduleWindow { Days = new[] { 6, 0 }, Start = "00:00", End = "00:00" } } };
Check("all-day Sat allowed", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,24,15,0,0), day));
Check("all-day Fri blocked", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,15,0,0), day));

var same = new PluginConfiguration { Windows = new[] { new ScheduleWindow { Days = new[] { 5 }, Start = "13:00", End = "17:00" } } };
Check("same-day in", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,14,0,0), same));
Check("same-day out", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,17,0,0), same));

var multi = new PluginConfiguration { Windows = new[] {
    new ScheduleWindow { Days = new[] { 1,2,3,4,5 }, Start = "23:00", End = "06:00" },
    new ScheduleWindow { Days = new[] { 6,0 }, Start = "00:00", End = "00:00" } } };
Check("multi: Sun noon allowed", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,25,12,0,0), multi));
Check("multi: Mon 05:00 blocked (Sun has no night window)", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,26,5,0,0), multi));
Check("multi: Sat 03:00 allowed (Fri night spill + all-day Sat)", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,24,3,0,0), multi));

var bo = new PluginConfiguration { Blackouts = new[] { new BlackoutRange { From = "2026-10-23", To = "2026-10-24" } } };
Check("blackout blocks", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,23,30,0), bo));
Check("after blackout ok", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,25,23,30,0), bo));

var off = new PluginConfiguration { ScheduleEnabled = false };
Check("schedule off = always", ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,15,0,0), off));
var bad = new PluginConfiguration { Windows = new[] { new ScheduleWindow { Days = new[] {5}, Start = "xx", End = "10:00" } } };
Check("bad time ignored", !ScheduleEvaluator.IsAllowed(new DateTime(2026,10,23,15,0,0), bad));

var hb = HandBrakeArgs.Build(new HandBrakeSettings(), "/in.mkv", "/out.mkv");
Console.WriteLine(string.Join(" ", hb));
Check("args contain -e x265", hb.Contains("x265"));
var pa = HandBrakeArgs.Build(new HandBrakeSettings { ExtraArgs = "--foo bar" }, "/in", "/out");
Check("extra args appended last", pa[^2] == "--foo" && pa[^1] == "bar");
Check("container source defaults to mkv args", string.Join(' ', hb).Contains("-f av_mkv"));

// ---- subtitle / resolution / encoder args
string A(HandBrakeSettings x) => string.Join(' ', HandBrakeArgs.Build(x, "/in", "/out"));
var keep = A(new HandBrakeSettings());
Check("subs keep: --all-subtitles", keep.Contains("--all-subtitles") && !keep.Contains("burn"));
var none = A(new HandBrakeSettings { SubtitleMode = "none" });
Check("subs none: no subtitle flags", !none.Contains("subtitle"));
var burnLang = A(new HandBrakeSettings { SubtitleMode = "burn", BurnLanguage = "JPN" });
Check("burn by language", burnLang.Contains("--subtitle-lang-list jpn") && burnLang.Contains("--first-subtitle") && burnLang.Contains("--subtitle-burned") && !burnLang.Contains("--all-subtitles") && !burnLang.Contains("--subtitle-burned="));
var burnTrack = A(new HandBrakeSettings { SubtitleMode = "burn", BurnTrack = 2, BurnForcedOnly = true });
Check("burn by track + forced", burnTrack.Contains("--subtitle 2") && burnTrack.Contains("--subtitle-burned=1") && burnTrack.Contains("--subtitle-forced=1") && !burnTrack.Contains("lang-list"));
var burnForcedLang = A(new HandBrakeSettings { SubtitleMode = "burn", BurnForcedOnly = true });
Check("burn forced by language", burnForcedLang.Contains("--subtitle-forced") && !burnForcedLang.Contains("--subtitle-forced="));
Check("maxHeight + square pixels", A(new HandBrakeSettings { MaxHeight = 720 }).Contains("--maxHeight 720 --non-anamorphic") && !A(new HandBrakeSettings()).Contains("maxHeight") && !A(new HandBrakeSettings()).Contains("anamorphic"));
Check("nvenc: no --encoder-preset", !A(new HandBrakeSettings { Encoder = "nvenc_h265" }).Contains("--encoder-preset"));
Check("svt_av1 keeps --encoder-preset", A(new HandBrakeSettings { Encoder = "svt_av1", EncoderPreset = "6" }).Contains("--encoder-preset 6"));
Check("burn + 480p combine", A(new HandBrakeSettings { SubtitleMode = "burn", MaxHeight = 480 }).Contains("--maxHeight 480 --non-anamorphic") && A(new HandBrakeSettings { SubtitleMode = "burn", MaxHeight = 480 }).Contains("--subtitle-burned"));
Check("mp4 container", A(new HandBrakeSettings { Container = "mp4" }).Contains("-f av_mp4"));

// ---- per-library effective profile
var pc = new PluginConfiguration();
pc.Defaults.DeleteOriginal = true;
var libUse = new LibrarySettings { LibraryId = "AAA", UseCustom = false };
var libOwn = new LibrarySettings { LibraryId = "BBB", UseCustom = true, Profile = new ConversionProfile { DeleteOriginal = false } };
pc.Libraries = new[] { libUse, libOwn };
Check("effective: defaults when not custom", pc.EffectiveProfile(libUse).DeleteOriginal);
Check("effective: own profile when custom", !pc.EffectiveProfile(libOwn).DeleteOriginal);
Check("effective: unknown library -> defaults", pc.EffectiveProfile(null).DeleteOriginal);
Check("FindLibrary is case-insensitive", pc.FindLibrary("aaa") == libUse);

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
