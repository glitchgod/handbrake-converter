"""Loads the real configPage.html in headless Chromium with a fake Jellyfin ApiClient/Dashboard and drives the UI."""
import json, sys, pathlib
from playwright.sync_api import sync_playwright

PAGE = pathlib.Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.HandBrakeConverter/Configuration/configPage.html"

INIT = r"""
window.__calls = [];
window.__cfg = {
  Enabled: false, HandBrakePath: '', MaxFailures: 2, SkipIfNotSmaller: true, VerifyDuration: true, MinFreeSpaceGb: 2, MinSavingsPercent: 0, TrashFolder: '', MinSizeMb: 0, KeepReplacedDays: 7, ScheduleEnabled: true, PauseWhileStreaming: true, PausedCountsAsStreaming: false,
  FinishCurrentAfterWindow: true, LowPriority: true,
  Defaults: { HandBrake: { Encoder:'x265', Quality:22, EncoderPreset:'medium', Container:'source', AudioMode:'copy', AudioBitrate:160, SubtitleMode:'keep', BurnLanguage:'eng', BurnTrack:0, BurnForcedOnly:false, MaxHeight:0, PresetJson:'', PresetName:'', ExtraArgs:'' }, DeleteOriginal:false, OutputSuffix:'- HandBrake' },
  Libraries: [], Windows: [{Days:[0,1,2,3,4,5,6], Start:'23:00', End:'10:00'}], Blackouts: []
};
window.__jobs = [];
window.__ins = {Days:60, Sessions:12, VideoTranscodes:9, AudioTranscodes:7, LogDirectory:'/var/log/jellyfin',
  Routes:[{Name:'HEVC HDR 2160p → H.264 1080p', Count:6},{Name:'HEVC 1080p → H.264 720p', Count:3}],
  TopFiles:[{Path:'/media/Movies/Heat.mkv', Count:4, Why:'video, audio'}],
  Suggestions:[{Key:'hb.Encoder', Value:'x264', Reason:'9 of 9 video transcodes produced H.264 (100%).'},{Key:'hb.MaxHeight', Value:'1080', Reason:'6 of 9 video transcodes were scaled down.'},{Key:'hb.AudioMode', Value:'ac3', Reason:'The audio was re-encoded in 7 of 12 playbacks.'}],
  Notes:['2 of 9 video transcodes were HDR being tone-mapped.']};
window.__hist = [
  {Id:'h1', Time:'2026-10-28T14:03:00', Title:'Heat', Path:'/m/Heat.mkv', Result:'Converted', Detail:'', OldBytes:4294967296, NewBytes:1073741824, Seconds:5400, Source:'auto', JobId:''},
  {Id:'h2', Time:'2026-10-28T12:00:00', Title:'Planet Earth', Path:'/m/pe.mkv', Result:'Skipped', Detail:'Left alone: this is a HDR video and HDR videos are set to be skipped.', OldBytes:0, NewBytes:0, Seconds:0, Source:'auto', JobId:''},
  {Id:'h3', Time:'2026-10-28T11:00:00', Title:'Bad file', Path:'/m/bad.mkv', Result:'Failed', Detail:'Converted file has 1 audio track(s) but the source has 2', OldBytes:0, NewBytes:0, Seconds:60, Source:'queue', JobId:'j9'}];
window.__trash = [{Id:'t1', Name:'Heat.mkv', OriginalPath:'/m/Heat.mkv', Size:2147483648, Trashed:'2026-10-20T01:00:00', Expires:'2026-10-27T01:00:00'}];
window.Dashboard = { showLoadingMsg(){}, hideLoadingMsg(){}, processPluginConfigurationUpdateResult(){} };
window.ApiClient = {
  getUrl: p => '/' + p,
  getPluginConfiguration: async () => JSON.parse(JSON.stringify(window.__cfg)),
  updatePluginConfiguration: async (id, c) => { window.__calls.push(['SAVE', c]); window.__cfg = JSON.parse(JSON.stringify(c)); return {}; },
  getVirtualFolders: async () => ([
    {ItemId:'aaaa1111aaaa1111aaaa1111aaaa1111', Name:'Movies', CollectionType:'movies'},
    {ItemId:'bbbb2222bbbb2222bbbb2222bbbb2222', Name:'Anime', CollectionType:'tvshows'},
    {ItemId:'cccc3333cccc3333cccc3333cccc3333', Name:'Music', CollectionType:'music'}]),
  ajax: async (o) => {
    const body = o.data ? JSON.parse(o.data) : null;
    window.__calls.push([o.type, o.url, body]);
    const u = o.url;
    const ok = (j) => j;
    if (u.endsWith('/Status')) return ok({State:'Idle', Message:'Nothing to convert.', CurrentFile:'', CurrentJobId:'', Progress:0, Eta:'', Suspended:false, Paused:!!window.__paused, HandBrakeFound:true, HandBrakeSource:'installed on this server', HandBrakePath:'/usr/bin/HandBrakeCLI', HandBrakeVersion:'HandBrake 1.9.2', Converted:3, BytesSaved:5368709120, Skipped:2, TrashCount:1, TrashBytes:2147483648, Failed:0, Queued: window.__jobs.length});
    if (u.includes('/Browse?')) {
      if (u.includes('libraryId=bbbb')) return ok([{Id:'11111111111111111111111111111111', Name:'Cowboy Bebop', Kind:'Series', Year:1998, Index:null}]);
      if (u.includes('parentId=1111')) return ok([{Id:'22222222222222222222222222222222', Name:'Season 1', Kind:'Season', Year:null, Index:1}]);
      if (u.includes('parentId=2222')) return ok([{Id:'33333333333333333333333333333333', Name:'Asteroid Blues', Kind:'Episode', Year:null, Index:1}, {Id:'44444444444444444444444444444444', Name:'Stray Dog Strut', Kind:'Episode', Year:null, Index:2}]);
      return ok([{Id:'55555555555555555555555555555555', Name:'Heat', Kind:'Movie', Year:1995, Index:null}]);
    }
    if (u.endsWith('/Queue') && o.type === 'POST') { window.__jobs.push({Id:'j'+window.__jobs.length, Title:'Asteroid Blues', Path:'/m/x.mkv', Status:'Queued', Error:'', IgnoreSchedule:body.IgnoreSchedule, DeleteOriginal:body.Profile.DeleteOriginal, Encoder:body.Profile.HandBrake.Encoder, Subtitles:body.Profile.HandBrake.SubtitleMode, MaxHeight:body.Profile.HandBrake.MaxHeight}); return ok({Added:body.ItemIds.length, AlreadyQueued:0, NothingFound:0}); }
    if (u.endsWith('/Queue')) return ok(window.__jobs);
    if (u.includes('/Insights')) return ok(window.__ins);
    if (u.endsWith('/Encoders')) return ok({Encoders:['x264','x265','x265_10bit','svt_av1']});
    if (u.includes('/Encoders/Test')) return ok({Ok: !u.includes('x264'), Message: u.includes('x264') ? 'Failed (exit 3): boom' : 'Works (0.4 s for a 2-second sample).'});
    if (u.endsWith('/History') && o.type === 'GET') return ok(window.__hist);
    if (u.endsWith('/History/Clear')) { window.__hist = []; return ok({Ok:true}); }
    if (u.includes('/History/') && u.endsWith('/Retry')) return ok({Ok:true});
    if (u.endsWith('/Pause')) { window.__paused = true; return ok({}); }
    if (u.endsWith('/Resume')) { window.__paused = false; return ok({}); }
    if (u.includes('/Move?')) { const id = u.split('/Queue/')[1].split('/')[0], to = u.split('to=')[1]; const i = window.__jobs.findIndex(j => j.Id === id); const [j] = window.__jobs.splice(i, 1); window.__jobs.splice(to === 'top' ? 0 : to === 'up' ? Math.max(0, i-1) : i+1, 0, j); return ok({Ok:true}); }
    if (u.endsWith('/Queue/Remove')) { window.__jobs = window.__jobs.filter(j => !body.Ids.includes(j.Id)); return ok({Removed:body.Ids.length}); }
    if (u.endsWith('/Trash') && o.type === 'GET') return ok(window.__trash);
    if (u.includes('/Trash/') && u.endsWith('/Restore')) { window.__trash = []; return ok({Path:'/m/x - Original.mkv'}); }
    return ok({Ok:true});
  }
};
"""

fails = 0
def check(name, cond):
    global fails
    print(("PASS " if cond else "FAIL ") + name)
    if not cond: fails += 1

with sync_playwright() as p:
    b = p.chromium.launch(executable_path="/opt/pw-browsers/chromium", args=["--no-sandbox"])
    pg = b.new_page(viewport={"width": 1100, "height": 1400})
    errors = []
    pg.on("pageerror", lambda e: errors.append(str(e)))
    pg.on("console", lambda m: errors.append(m.text) if m.type == "error" else None)
    html = PAGE.read_text()
    pg.set_content(html)
    pg.evaluate(INIT)
    pg.evaluate("document.querySelector('#HBConvPage').dispatchEvent(new Event('pageshow'))")
    pg.wait_for_selector(".hbLib", state="attached")

    check("overview shows version + stats", "HandBrake 1.9.2" in pg.inner_text("#hbStatus") and "Converted: 3" in pg.inner_text("#hbStats"))
    check("music library hidden, 2 library cards", pg.locator(".hbLib").count() == 2)
    check("convert dropdown: '- Pick library -' first, then Movies+Anime", pg.locator("#hbConvLib option").count() == 3 and pg.locator("#hbConvLib").input_value() == "" and "Pick library" in pg.locator("#hbConvLib option").first.inner_text())
    check("overview only has the status box and master switch", pg.locator(".hbTab[data-tab=overview] .verticalSection").count() == 0 and pg.locator("#Enabled").is_visible() and not pg.locator("#hbCheckBtn").is_visible() and not pg.locator("#MaxFailures").is_visible())
    check("no 'Clear finished' button", pg.locator("#hbClearFinished").count() == 0)
    pg.click(".hbTabBtn[data-tab=defaults]")
    q0 = pg.locator("#hbDefaults [data-k='hb.Quality']").input_value()
    pg.locator("#hbDefaults [data-k='hb.Quality']").fill("31")
    pg.locator("#hbDefaults [data-k='hb.MaxHeight']").select_option("1080")

    # --- Libraries tab: Anime = auto + custom + burn jpn + 720p
    pg.click(".hbTabBtn[data-tab=libraries]")
    anime = pg.locator(".hbLib[data-name=Anime]")
    check("custom profile hidden until ticked", not anime.locator(".hbLibProfile").is_visible())
    anime.locator(".hbAuto").check()
    anime.locator(".hbCustom").check()
    check("custom profile appears", anime.locator(".hbLibProfile").is_visible())
    check("custom profile starts from the Default settings", anime.locator("[data-k='hb.Quality']").input_value() == "31" and anime.locator("[data-k='hb.MaxHeight']").input_value() == "1080")
    anime.locator("[data-k='hb.Quality']").fill("27")
    anime.locator(".hbCustom").uncheck(); anime.locator(".hbCustom").check()
    check("re-ticking keeps your edits", anime.locator("[data-k='hb.Quality']").input_value() == "27")
    check("burn group hidden by default", not anime.locator(".hbBurn").is_visible())
    anime.locator("[data-k='hb.SubtitleMode']").select_option("burn")
    check("burn group shows on 'burn'", anime.locator(".hbBurn").is_visible())
    anime.locator("[data-k='hb.BurnLanguage']").fill("jpn")
    anime.locator("[data-k='hb.MaxHeight']").select_option("720")
    anime.locator("[data-k='hb.Quality']").fill("24")
    pg.click(".hbTabBtn[data-tab=defaults]")
    pg.locator("#hbDefaults [data-k='hb.Quality']").fill(q0)
    pg.locator("#hbDefaults [data-k='hb.MaxHeight']").select_option("0")
    pg.click(".hbTabBtn[data-tab=overview]")
    pg.check("#Enabled")
    pg.click("#hbSave")
    pg.wait_for_function("window.__calls.some(c => c[0]==='SAVE')")
    saved = [c for c in pg.evaluate("window.__calls") if c[0] == "SAVE"][-1][1]
    libs = {l["Name"]: l for l in saved["Libraries"]}
    check("saved: master enabled", saved["Enabled"] is True)
    check("saved: Anime auto+custom", libs["Anime"]["AutoProcess"] and libs["Anime"]["UseCustom"])
    hb = libs["Anime"]["Profile"]["HandBrake"]
    check("saved: Anime burn jpn 720p q24", hb["SubtitleMode"] == "burn" and hb["BurnLanguage"] == "jpn" and hb["MaxHeight"] == 720 and hb["Quality"] == 24)
    check("saved: Movies not auto", libs["Movies"]["AutoProcess"] is False and libs["Movies"]["UseCustom"] is False)
    check("saved: defaults untouched", saved["Defaults"]["HandBrake"]["SubtitleMode"] == "keep")
    check("saved: numbers are numbers", isinstance(hb["Quality"], int) and isinstance(saved["MaxFailures"], int) and isinstance(hb["MaxHeight"], int) and isinstance(saved["Defaults"]["HandBrake"]["MaxHeight"], int))

    # --- Convert tab: drill Anime > Cowboy Bebop > Season 1 > episode, options prefilled from Anime's custom profile
    pg.click(".hbTabBtn[data-tab=convert]")
    pg.select_option("#hbConvLib", "bbbb2222bbbb2222bbbb2222bbbb2222")
    pg.click("#hbConvGo")
    pg.wait_for_selector(".hbOpen")
    check("series listed", "Cowboy Bebop" in pg.inner_text("#hbConvList"))
    pg.click(".hbOpen")
    pg.wait_for_selector("text=Season 1")
    pg.click(".hbOpen")
    pg.wait_for_selector("text=Asteroid Blues")
    check("breadcrumb has 3 levels", pg.locator(".hbCrumb").count() == 3)
    pg.locator(".hbOne").first.click()
    check("options open", pg.locator("#hbConvOptions").is_visible())
    check("options prefilled with Anime custom (burn + 720)", pg.input_value("#hbConvProfile [data-k='hb.SubtitleMode']") == "burn" and pg.input_value("#hbConvProfile [data-k='hb.MaxHeight']") == "720")
    check("burn group visible in options", pg.locator("#hbConvProfile .hbBurn").is_visible())
    pg.select_option("#hbConvProfile [data-k='hb.MaxHeight']", "480")
    pg.fill("#hbConvProfile [data-k='hb.BurnTrack']", "3")
    pg.select_option("#hbConvProfile [data-k='p.DeleteOriginal']", "replace")
    pg.check("#hbRunNow")
    pg.click("#hbQueueBtn")
    pg.wait_for_function("window.__calls.some(c => c[0]==='POST' && c[1].endsWith('/Queue'))")
    q = [c for c in pg.evaluate("window.__calls") if c[0] == "POST" and c[1].endswith("/Queue")][-1][2]
    check("queue body: one episode id", q["ItemIds"] == ["33333333333333333333333333333333"])
    check("queue body: 480p, track 3, burn, delete original, run now", q["Profile"]["HandBrake"]["MaxHeight"] == 480 and q["Profile"]["HandBrake"]["BurnTrack"] == 3 and q["Profile"]["HandBrake"]["SubtitleMode"] == "burn" and q["Profile"]["DeleteOriginal"] is True and q["IgnoreSchedule"] is True)
    check("queued message shown", "Queued 1 file" in pg.inner_text("#hbQueueMsg"))

    # multi-select (checkboxes) -> converts both episodes
    pg.locator(".hbPick").nth(0).check(); pg.locator(".hbPick").nth(1).check()
    check("selection count", "2 selected" in pg.inner_text("#hbSelCount"))
    pg.click("#hbConvSel")
    check("multi options title", "Asteroid Blues" in pg.inner_text("#hbConvWhat") and "Stray Dog" in pg.inner_text("#hbConvWhat"))

    # --- Queue tab
    pg.click(".hbTabBtn[data-tab=queue]")
    pg.wait_for_selector(".hbJobPick")
    check("queue row shows job + options", "Asteroid Blues" in pg.inner_text("#hbQueue") and "480p" in pg.inner_text("#hbQueue") and "replaces original" in pg.inner_text("#hbQueue"))
    check("save bar hidden on queue/convert tabs", not pg.locator("#hbSaveBar").is_visible())
    pg.evaluate("window.__jobs.push({Id:'jB', Title:'Bravo', Path:'/m/b.mkv', Status:'Queued', Error:'', IgnoreSchedule:false, DeleteOriginal:false, Encoder:'x265', Subtitles:'keep', MaxHeight:0}, {Id:'jC', Title:'Charlie', Path:'/m/c.mkv', Status:'Queued', Error:'', IgnoreSchedule:false, DeleteOriginal:false, Encoder:'x265', Subtitles:'keep', MaxHeight:0}, {Id:'jD', Title:'Delta', Path:'/m/d.mkv', Status:'Done', Error:'', IgnoreSchedule:false, DeleteOriginal:false, Encoder:'x265', Subtitles:'keep', MaxHeight:0})")
    pg.click(".hbTabBtn[data-tab=overview]"); pg.click(".hbTabBtn[data-tab=queue]")
    pg.wait_for_function("document.querySelectorAll('#hbQueue .hbRow').length === 4")
    titles = lambda: pg.evaluate("[...document.querySelectorAll('#hbQueue .hbRow b')].map(b => b.textContent)")
    check("queue: first row can't go up/top, last queued can't go down", pg.locator("#hbQueue .hbRow").nth(0).locator("[data-to=up]").is_disabled() and pg.locator("#hbQueue .hbRow").nth(2).locator("[data-to=down]").is_disabled())
    check("queue: finished jobs have no move buttons", pg.locator("#hbQueue .hbRow").nth(3).locator(".hbMove").count() == 0)
    pg.locator("#hbQueue .hbRow").nth(2).locator("[data-to=top]").click()
    pg.wait_for_function("document.querySelector('#hbQueue .hbRow b').textContent === 'Charlie'")
    check("queue: Top moves a job to the front", titles()[:3] == ["Charlie", "Asteroid Blues", "Bravo"])
    pg.locator("#hbQueue .hbRow").nth(0).locator("[data-to=down]").click()
    pg.wait_for_function("document.querySelector('#hbQueue .hbRow b').textContent === 'Asteroid Blues'")
    check("queue: down moves one place", titles()[:3] == ["Asteroid Blues", "Charlie", "Bravo"])
    check("queue: move calls hit the API", any("/Move?to=down" in c[1] for c in pg.evaluate("window.__calls") if c[0] == "POST"))
    # selection survives the 4 s refresh, then multi-remove
    pg.locator("#hbQueue .hbJobPick").nth(1).check(); pg.locator("#hbQueue .hbJobPick").nth(3).check()
    pg.wait_for_timeout(4500)
    check("queue: selection survives a refresh", pg.locator("#hbQueue .hbJobPick:checked").count() == 2)
    pg.once("dialog", lambda d: d.accept())
    pg.click("#hbRemoveSel")
    pg.wait_for_function("document.querySelectorAll('#hbQueue .hbRow').length === 2")
    rm = [c for c in pg.evaluate("window.__calls") if c[0] == "POST" and c[1].endswith("/Queue/Remove")][-1][2]
    check("queue: remove selected sends all ids at once", sorted(rm["Ids"]) == ["jC", "jD"] and titles() == ["Asteroid Blues", "Bravo"])
    pg.check("#hbSelAll")
    check("queue: select all ticks every row", pg.locator("#hbQueue .hbJobPick:checked").count() == 2)
    pg.uncheck("#hbSelAll")

    # --- Pause / Continue on the Overview box
    pg.click(".hbTabBtn[data-tab=overview]")
    check("overview: shows Pause", pg.inner_text("#hbPauseBtn").strip() == "Pause")
    pg.click("#hbPauseBtn")
    pg.wait_for_function("document.querySelector('#hbPauseBtn').textContent.trim() === 'Continue'")
    check("overview: after pausing the button says Continue and explains", "Continue" in pg.inner_text("#hbPauseNote") and any(c[1].endswith("/Pause") for c in pg.evaluate("window.__calls") if c[0] == "POST"))
    pg.click("#hbPauseBtn")
    pg.wait_for_function("document.querySelector('#hbPauseBtn').textContent.trim() === 'Pause'")
    check("overview: Continue posts Resume", any(c[1].endswith("/Resume") for c in pg.evaluate("window.__calls") if c[0] == "POST"))


    # --- History tab
    pg.click(".hbTabBtn[data-tab=history]")
    pg.wait_for_function("document.querySelectorAll('#hbHistory .hbRow').length === 3")
    ht = pg.inner_text("#hbHistory")
    check("history: converted row shows sizes, saving and time", "4096 MB" in ht or "4.0 GB" in ht)
    check("history: saving percent and duration shown", "75%" in ht and "1 h 30 min" in ht)
    check("history: skipped and failed rows show their reason", "HDR videos are set to be skipped" in ht and "1 audio track" in ht)
    check("history: only failures have Retry", pg.locator("#hbHistory .hbRetry").count() == 1)
    pg.select_option("#hbHistFilter", "Skipped")
    check("history: filter works", pg.locator("#hbHistory .hbRow").count() == 1 and "Planet Earth" in pg.inner_text("#hbHistory"))
    pg.select_option("#hbHistFilter", "")
    pg.click("#hbHistory .hbRetry")
    pg.wait_for_function("document.querySelector('#hbHistory .hbRetry').disabled")
    check("history: retry posts to the API", any(c[1].endswith("/History/h3/Retry") for c in pg.evaluate("window.__calls") if c[0] == "POST"))
    check("history: save bar hidden", not pg.locator("#hbSaveBar").is_visible())
    pg.once("dialog", lambda d: d.accept())
    pg.click("#hbHistClear")
    pg.wait_for_function("document.querySelector('#hbHistory').textContent.includes('Nothing here yet')")
    check("history: clear empties the list", True)


    # --- Default settings: the suggested mark follows Jellyfin's transcode logs when they have an answer
    pg.click(".hbTabBtn[data-tab=defaults]")
    check("no separate suggestions panel (kept to the dropdowns)", pg.locator("#hbInsights").count() == 0)
    sample = pg.evaluate("window.__ins")
    pg.evaluate("window.__ins = %s" % __import__("json").dumps(dict(sample, Suggestions=[
        {"Key": "hb.Encoder", "Value": "x264", "Reason": "r"}, {"Key": "hb.MaxHeight", "Value": "720", "Reason": "r"}, {"Key": "hb.AudioMode", "Value": "ac3", "Reason": "r"}])))
    pg.evaluate("document.querySelector('#HBConvPage').dispatchEvent(new Event('pageshow'))")
    pg.wait_for_function("document.querySelector('#hbDefaults [data-k=\"hb.Encoder\"] option:checked') !== null && [...document.querySelectorAll('#hbDefaults option')].some(o => o.textContent.includes('from your playback'))")
    fl = pg.evaluate("[...document.querySelectorAll('#hbDefaults option')].filter(o => o.textContent.includes('(suggested')).map(o => o.parentElement.getAttribute('data-k') + '=' + o.value + ':' + (o.textContent.includes('from your playback') ? 'logs' : 'default'))")
    check("log-based suggestions replace the default mark on encoder, resolution and audio", sorted(fl) == sorted(["hb.Encoder=x264:logs", "hb.EncoderPreset=fast:default", "hb.MaxHeight=720:logs", "hb.HdrMode=skip:default", "hb.AudioMode=ac3:logs", "hb.SubtitleMode=keep:default", "hb.Container=source:default", "p.DeleteOriginal=replace:default"]))
    pg.evaluate("window.__ins = %s" % __import__("json").dumps(dict(sample, Suggestions=[{"Key": "hb.Encoder", "Value": "nvenc_h265", "Reason": "r"}])))
    pg.evaluate("document.querySelector('#HBConvPage').dispatchEvent(new Event('pageshow'))")
    pg.wait_for_function("![...document.querySelectorAll('#hbDefaults option')].some(o => o.textContent.includes('from your playback'))")
    check("a log suggestion that isn't installed here falls back to the default mark", pg.locator("#hbDefaults [data-k='hb.Encoder'] option", has_text="x265 (CPU, HEVC) (suggested)").count() == 1)
    pg.evaluate("window.__ins = %s" % __import__("json").dumps(dict(sample, Suggestions=[])))
    pg.evaluate("document.querySelector('#HBConvPage').dispatchEvent(new Event('pageshow'))")
    pg.wait_for_function("[...document.querySelectorAll('#hbDefaults option')].some(o => o.textContent.includes('(suggested)'))")

    # --- Default settings: installed encoders only, HDR option, suggested marks
    pg.click(".hbTabBtn[data-tab=defaults]")
    encs = pg.evaluate("[...document.querySelectorAll('#hbDefaults [data-k=\"hb.Encoder\"] option')].map(o => o.value)")
    check("encoder dropdown lists only what HandBrake has here", sorted(encs) == sorted(["x264", "x265", "x265_10bit", "svt_av1"]))
    sug = pg.evaluate("[...document.querySelectorAll('#hbDefaults option')].filter(o => o.textContent.includes('(suggested)')).map(o => o.parentElement.getAttribute('data-k') + '=' + o.value)")
    check("every dropdown marks a suggested choice", sorted(sug) == sorted(["hb.Encoder=x265", "hb.EncoderPreset=fast", "hb.MaxHeight=1080", "hb.HdrMode=skip", "hb.AudioMode=copy", "hb.SubtitleMode=keep", "hb.Container=source", "p.DeleteOriginal=replace"]))
    check("HDR dropdown has its three choices", pg.evaluate("[...document.querySelectorAll('#hbDefaults [data-k=\"hb.HdrMode\"] option')].map(o => o.value).join()") == "skip,keep,ignore")
    pg.locator("#hbDefaults [data-k='hb.HdrMode']").select_option("keep")
    pg.click("#hbSave")
    pg.wait_for_function("window.__calls.filter(c => c[0]==='SAVE').length >= 2")
    hsaved = [c for c in pg.evaluate("window.__calls") if c[0] == "SAVE"][-1][1]
    check("saved: HDR mode", hsaved["Defaults"]["HandBrake"]["HdrMode"] == "keep")
    pg.locator("#hbDefaults [data-k='hb.HdrMode']").select_option("skip")

    # --- HandBrake & tools: encoder list + test buttons
    pg.click(".hbTabBtn[data-tab=tools]")
    check("tools: lists the installed encoders", pg.locator("#hbEncoders .hbEncTest").count() == 4 and "x265 10-bit" in pg.inner_text("#hbEncoders"))
    pg.locator("#hbEncoders .hbEncTest[data-name=x265]").click()
    pg.wait_for_function("document.querySelector('.hbEncMsg[data-for=x265]').textContent.includes('Works')")
    check("tools: Test reports success", "✔" in pg.inner_text(".hbEncMsg[data-for=x265]"))
    pg.locator("#hbEncoders .hbEncTest[data-name=x264]").click()
    pg.wait_for_function("document.querySelector('.hbEncMsg[data-for=x264]').textContent.includes('boom')")
    check("tools: Test reports failure", "✖" in pg.inner_text(".hbEncMsg[data-for=x264]"))

    # --- Original files tab (held originals + safety checks)
    pg.click(".hbTabBtn[data-tab=originals]")
    pg.wait_for_selector(".hbRestore")
    check("held original listed with size + expiry", "Heat.mkv" in pg.inner_text("#hbTrash") and "2.0 GB" in pg.inner_text("#hbTrash") and "2026-10-27" in pg.inner_text("#hbTrash"))
    check("original-files tab is ordered before / after / replaced", pg.inner_text(".hbTab[data-tab=originals]").index("1. Before converting") < pg.inner_text(".hbTab[data-tab=originals]").index("2. After converting") < pg.inner_text(".hbTab[data-tab=originals]").index("3. Replaced originals"))
    pg.click(".hbRestore")
    pg.wait_for_function("document.querySelector('#hbTrash').textContent.includes('Nothing on hold')")
    check("restore posted", any(c[0] == "POST" and c[1].endswith("/Trash/t1/Restore") for c in pg.evaluate("window.__calls")))
    check("safety defaults loaded (verify duration + only-if-smaller on, 2 GB spare, 7 days)", pg.is_checked("#VerifyDuration") and pg.is_checked("#SkipIfNotSmaller") and pg.input_value("#MinFreeSpaceGb") == "2" and pg.input_value("#KeepReplacedDays") == "7")
    pg.fill("#MinFreeSpaceGb", "10"); pg.fill("#MinSavingsPercent", "15"); pg.fill("#TrashFolder", "/mnt/hold"); pg.fill("#KeepReplacedDays", "14"); pg.fill("#MinSizeMb", "50")
    pg.uncheck("#VerifyDuration")
    pg.screenshot(path="/tmp/page_originals.png", full_page=True)

    # --- Schedule tab + install
    pg.click(".hbTabBtn[data-tab=schedule]")
    check("schedule limited by default: windows visible", pg.locator("#hbScheduleBody").is_visible() and pg.is_checked("#ScheduleEnabled"))
    pg.uncheck("#ScheduleEnabled")
    check("time limits off: window editor hidden", not pg.locator("#hbScheduleBody").is_visible())
    pg.check("#ScheduleEnabled")
    check("window-end behaviour is a dropdown with plain wording", pg.locator("#WindowEndMode option").all_inner_texts() == ["Let that file finish, then stop (suggested)", "Stop it now and start over in the next window"])
    check("schedule window rendered", pg.locator(".hbWin").count() == 1)
    pg.click("#hbAddWin")
    check("add window", pg.locator(".hbWin").count() == 2)
    pg.click(".hbTabBtn[data-tab=defaults]")
    pg.screenshot(path="/tmp/page_defaults.png", full_page=True)
    d = pg.locator("#hbDefaults")
    check("default settings: no preset JSON box anywhere", pg.locator("textarea").count() == 0 and "preset" not in pg.inner_text("#hbDefaults").lower())
    sub_opts = d.locator("[data-k='hb.SubtitleMode'] option").all_inner_texts()
    check("subtitle dropdown = same as source / remove / burn one (no burn+keep)", sub_opts == ["Same as source (keep all tracks) (suggested)", "Remove all subtitles", "Burn one track into the video"])
    check("resolution dropdown says 'Same as source'", d.locator("[data-k='hb.MaxHeight'] option").first.inner_text() == "Same as source")
    check("audio dropdown says 'Same as source'", d.locator("[data-k='hb.AudioMode'] option").first.inner_text().startswith("Same as source"))
    check("container dropdown: same as source / MKV / MP4", d.locator("[data-k='hb.Container'] option").all_inner_texts() == ["Same as source (suggested)", "MKV", "MP4"])
    check("original-file dropdown = keep / replace", d.locator("[data-k='p.DeleteOriginal'] option").all_inner_texts() == ["Keep it, and save the converted file next to it", "Replace it with the converted file (suggested)"])
    check("label field visible only while keeping the original", d.locator(".hbSuffix").is_visible())
    d.locator("[data-k='p.DeleteOriginal']").select_option("replace")
    check("label field hidden when replacing", not d.locator(".hbSuffix").is_visible())
    d.locator("[data-k='hb.SubtitleMode']").select_option("burn")
    check("burn options appear", d.locator(".hbBurn").is_visible())
    pg.click("#hbSave")
    pg.wait_for_function("window.__calls.some(c => c[0]==='SAVE')")
    sv = [c for c in pg.evaluate("window.__calls") if c[0] == "SAVE"][-1][1]
    check("save: original-file dropdown becomes DeleteOriginal=true", sv["Defaults"]["DeleteOriginal"] is True and sv["Defaults"]["HandBrake"]["SubtitleMode"] == "burn" and sv["Defaults"]["HandBrake"]["Container"] == "source")
    check("save carries original-file settings as real numbers/bools", sv["MinFreeSpaceGb"] == 10 and sv["MinSavingsPercent"] == 15 and sv["TrashFolder"] == "/mnt/hold" and sv["VerifyDuration"] is False and sv["SkipIfNotSmaller"] is True and sv["KeepReplacedDays"] == 14 and sv["MinSizeMb"] == 50)
    check("save: numeric profile values stay numbers", isinstance(sv["Defaults"]["HandBrake"]["MaxHeight"], int) and isinstance(sv["Defaults"]["HandBrake"]["Quality"], int))
    pg.click(".hbTabBtn[data-tab=overview]")
    check("overview stats show skipped + held originals", "Skipped (not smaller): 2" in pg.inner_text("#hbStats") and "Originals on hold: 1" in pg.inner_text("#hbStats"))
    pg.click(".hbTabBtn[data-tab=tools]")
    check("HandBrake section shows it is installed, with version + path", "HandBrake is installed" in pg.inner_text("#hbHbStatus") and "1.9.2" in pg.inner_text("#hbHbStatus") and "/usr/bin/HandBrakeCLI" in pg.inner_text("#hbHbStatus"))
    check("no install/update button any more", pg.locator("#hbInstallBtn").count() == 0 and "flatpak" not in pg.inner_text("#HBConvPage").lower())
    pg.evaluate("(() => { window.__hbMissing = true; const orig = ApiClient.ajax; ApiClient.ajax = async (x) => { const r = await orig(x); if (x.url.endsWith('/Status') && window.__hbMissing) { r.HandBrakeFound = false; r.HandBrakeVersion = null; r.HandBrakePath = null; } return r; }; })()")
    pg.click("#hbCheckBtn")
    pg.wait_for_function("document.querySelector('#hbHbStatus').textContent.includes('not found')")
    check("missing HandBrake: tells how to install it", "sudo apt install handbrake-cli" in pg.inner_text("#hbHbStatus"))
    pg.evaluate("window.__hbMissing = false")
    pg.click("#hbCheckBtn")
    pg.wait_for_function("document.querySelector('#hbHbStatus').textContent.includes('is installed')")
    check("Check again picks it up", True)

    # every option explains itself; every tab (except Overview) opens with a short explanation
    missing = pg.evaluate("""() => {
      const out = [];
      document.querySelectorAll('#HBConvPage input[is=emby-input], #HBConvPage select[is=emby-select], #HBConvPage input[is=emby-checkbox]').forEach(el => {
        if (el.classList.contains('hbDay') || el.classList.contains('hbPick') || el.classList.contains('hbJobPick') || el.id === 'hbSelAll') return;
        const box = el.closest('.inputContainer, .selectContainer, .checkboxContainer');
        if (!box || !box.querySelector('.fieldDescription')) out.push(el.id || el.getAttribute('data-k') || el.className);
      });
      return out;
    }""")
    check("every option has a description sentence", not missing)
    if missing: print("  missing:", missing)
    pg.click(".hbTabBtn[data-tab=convert]"); pg.select_option("#hbConvLib", "bbbb2222bbbb2222bbbb2222bbbb2222"); pg.click("#hbConvGo"); pg.wait_for_selector(".hbPick"); pg.locator(".hbPick").nth(0).check(); pg.click("#hbConvSel")
    missing2 = pg.evaluate("""() => { const out=[]; document.querySelectorAll('#hbConvProfile input[is=emby-input], #hbConvProfile select[is=emby-select], #hbConvProfile input[is=emby-checkbox]').forEach(el => { const b = el.closest('.inputContainer, .selectContainer, .checkboxContainer'); if (!b || !b.querySelector('.fieldDescription')) out.push(el.getAttribute('data-k')); }); return out; }""")
    check("manual-convert dialog options all described", not missing2)
    for t in ["libraries", "convert", "queue", "history", "defaults", "originals", "schedule", "tools"]:
        first = pg.evaluate("(t) => { const e = document.querySelector('.hbTab[data-tab=' + t + '] > .fieldDescription'); return e ? e.textContent.length : 0; }", t)
        check("tab intro present: " + t, first > 60)

    pg.click(".hbTabBtn[data-tab=libraries]")
    pg.screenshot(path="/tmp/page_libraries.png", full_page=True)
    pg.click(".hbTabBtn[data-tab=convert]")
    pg.screenshot(path="/tmp/page_convert.png", full_page=True)
    check("no JS errors", not errors)
    if errors: print(errors)
    b.close()

print("ALL PASS" if fails == 0 else f"{fails} FAILED")
sys.exit(fails)
