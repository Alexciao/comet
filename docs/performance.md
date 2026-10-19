# Performance

Machine: 12 logical cores, Windows 11 (build 26200), .NET 10.0. All numbers are measured, not estimated.

## Search (BenchmarkDotNet, `tests/Inlay.Benchmarks`, full set of 1,923 emoji + 2 snippets, 24 results)

| Query | What it exercises | Mean | Allocated |
|---|---|---:|---:|
| `sm` | wide prefix range | 2.4 µs | 0 |
| `smi` | prefix | 1.2 µs | 0 |
| `smile` | exact + prefixes | 0.9 µs | 0 |
| `hrt` | abbreviation tier over every emoji | 42 µs | 0 |
| `thumbsup` | few strong matches → abbreviation + typo tiers run | 161 µs | 0 |
| `smiel` | 1-edit typo scan | 164 µs | 0 |
| `thnumbs` | 1-edit typo scan | 128 µs | 0 |
| `laughign` | 2-edit typo scan (worst case) | 321 µs | 0 |
| `zzzzqx` | no match at all | 62 µs | 0 |
| load data + build engine | startup / data swap | 30 µs | 36 KB |

Target < 1 ms per keystroke: met with ≥ 3× headroom in the worst case. The fuzzy tiers only run when the
strong tiers did not already fill the list, so typical prefix keystrokes cost 1–2 µs.

## Search quality (`tests/search-golden.tsv`)

383 cases (273 tuned against + 110 holdout). Top-1 100% (232 cases marked "1"), top-3 100%.
Holdout on first run, before any fix: top-1 100%, top-3 99.1%.

## Keystroke → popup (E2E, `PerformanceTests`, Notepad, 30 sessions × 3 query keystrokes)

Measured inside Inlay from the hook callback for the key to the composition frame that shows the updated popup,
after the startup warm-up (top 500 emoji pre-rendered at idle priority, done 12.5 s after start).

| Build | Warm p50 | Warm p95 | Warm p99 | First-time emoji p95 | Hook, idle key p50 / p95 | Hook, popup open p50 / p95 |
|---|---:|---:|---:|---:|---:|---:|
| Release, Native AOT | 6.3 ms | **12.5 ms** | 22.7 ms | 59 ms | 21 µs / 119 µs | 141 µs / 346 µs |
| Debug (busy desktop) | 6.4–6.8 ms | 17–21 ms | 30–40 ms | 90–130 ms | 100–130 µs / 1.1–2.7 ms | 0.6–1.4 ms / 4.8–7.4 ms |

Target p95 < 16 ms for the shipped build: met (12.5 ms, two runs). "First-time emoji" are frames that draw an emoji
the popup has never drawn (color glyph rasterization costs 10–25 ms each); the warm-up makes these rare. The idle
hook cost (the path for every keystroke typed anywhere) is translation + a foreground check; the p95 includes
scheduler noise.

## Startup, memory, idle CPU, size (Release, Native AOT, `artifacts\publish`)

| Metric | Value | How |
|---|---:|---|
| Process created → hook installed, emoji loaded | 113–148 ms (3 runs) | process start time vs. the "started" log line |
| Emoji warm-up (top 500 pre-rendered, idle priority) | 12.5–14.5 s after start | "emoji bitmaps ready" log line; input is served from the first millisecond |
| Working set / private bytes, 1 s after start | 99 MB / 112 MB | WinUI 3 + DirectComposition + the acrylic popup window |
| Working set / private bytes, after warm-up | 124–132 MB / 197–212 MB | each pre-rendered emoji is its own `RenderTargetBitmap` surface (~170 KB commit each) |
| Handles / threads (idle) | 874 / 36–41 | |
| Idle CPU (60 s, popup closed, no typing) | 47–63 ms per minute (≈0.08% of one core; Task Manager shows 0%) | per-thread cycle counts: Inlay's own threads (hook, engine, context worker, UI) **0 cycles**; the rest is the GPU driver's worker threads (`nvwgf2umx.dll`, created with the composition device) and the .NET finalizer |
| `Inlay.exe` | 9.3 MB | Native AOT, single native image |
| Publish folder | 105 MB, of which 38.5 MB is `Inlay.pdb` | the shipped package leaves the PDB out (symbols are a separate artifact): **66 MB** installed, Windows App SDK runtime included |

Memory note: the emoji bitmap cache is the one sizeable cost under Inlay's control. Packing pre-rendered emoji
into a few atlas bitmaps would cut ~80 MB of commit; it is listed as a future idea because there is no memory
target and the latency target is met.

## Silent emoji data updates

| Metric | Value | How |
|---|---:|---|
| Update check answered 304 Not Modified | 67 ms | E2E `EmojiUpdateTests`, local HTTP server (first request of the process, so it includes connection setup); a GitHub round trip adds network latency on top |
| Rebuild of the search data after a swap | 0.3–0.4 ms | "search data rebuilt in the background" log line (low-priority thread; the index is read in place from the pack) |
| Installed-font check (DirectWrite), whole dataset | 21–40 ms for 1,923 emoji | "font check" log line / `EmojiFontCheckTests`; runs in the background after each dataset load |
| Idle cost | none | one one-shot `DispatcherQueueTimer`, armed for the next due check (days away); no polling, no thread |

## Soak (10,000 triggers, Release AOT build, Edge textarea, 17.4 min)

Mix per 10 triggers: 1 shorthandsment, 2 emoji inserted with Enter, 7 popups opened, browsed and cancelled.

| After | Private | Working set | Handles | GDI | USER |
|---:|---:|---:|---:|---:|---:|
| start (warm) | 216.2 MB | 137.1 MB | 906 | 9 | 34 |
| 1,000 | 231.3 MB | 146.5 MB | 1,121 | 9 | 38 |
| 5,000 | 233.8 MB | 150.0 MB | 1,082 | 9 | 36 |
| 10,000 | 234.8 MB | 151.0 MB | 1,084 | 9 | 37 |

Flat after the first 1,000 (caches and pools filling): +3.5 MB private over the next 9,000 triggers and levelling
off, handles −37, GDI/USER objects unchanged; no errors logged. Latency over the run: keystroke → popup p50 6.1 ms,
p95 9.6 ms, p99 10.8 ms (n = 4,096 most recent); hook cost per keystroke p50 67 µs idle / 161 µs with the popup
open.

## "Let me pick" preview (Debug build, E2E `Let_me_pick_shows_thumbnails_for_new_files_and_the_preview_never_flashes`)

The preview window is placed and laid out cloaked and uncloaked only once the image has opened, two frames have
rendered and DWM has composed once more, so it never appears empty. Checked with real keystrokes: a sampler
grabbed the preview's screen rectangle in a loop while the picker opened five times; no sample showed the window
without its picture (6 runs in a row, 30 opens; with a single frame wait it failed in about 1 of 5 opens).

| Metric | Value | How |
|---|---:|---|
| Preview shown → first visible frame, with the image in it | p50 33–47 ms (3 runs, 5 opens each; first open of a new file is the slowest, 160–175 ms) | "preview shown to first frame" log line. Counted from the moment the preview is requested, which is 100 ms after the row is selected on purpose (no flicker while arrowing). |
| Quick Insert popup, keystroke → rendered | unchanged | the popup's own path was not touched |

Measured on a Debug build; the shipped AOT build was not re-measured for this change.

## Settings window (Release, Native AOT, 500 snippets + 500 replacements)

Measured inside Inlay with `INLAY_PERF=1` (time from the action to the composed frame that shows its result,
split into the frame's own layout + render work), on a 165 Hz display; 60 Hz has a 16.7 ms frame.

| Action | Target | Measured |
|---|---|---:|
| Tray click → Settings fully drawn, first open after start | < 500 ms | 217–243 ms |
| Tray click → Settings fully drawn, warm (window destroyed and rebuilt) | < 250 ms | 154–168 ms |
| Switching pages, first visit in a window | < 50 ms | 27–44 ms (frame work 9–17 ms) |
| Switching pages, page already visited | < 50 ms | 15–32 ms (frame work 9–17 ms) |
| Filtering 500 entries, per typed keystroke | one frame | 3.8–19 ms to the frame (list update ≤ 3.1 ms, frame work ≤ 3.4 ms) |
| Filtering: select all + delete (25 → 500 cards at once) | one frame | 33 ms (frame work 6 ms) — the one case over a frame |
| Scrolling 500 cards with the wheel (flick and normal speed) | smooth | UI-thread frames p95 ≤ 2.9 ms, max 5.4 ms, none over 16.7 ms |

Scrolling itself runs on the compositor thread; what can make it stutter is the UI thread realizing cards, which is
what the scroll row measures. First visits stay under 50 ms because each page builds what is on screen in its first
frame and the rest (list cards, the sections below the fold) in the next one, behind the entrance transition.

**Memory.** The window is destroyed on close and its objects are collected (checked with heap dumps and weak
references; an E2E test fails if closed windows stay in memory). Then Inlay trims its working set and re-renders one
cloaked popup frame, so the popup's own pages are back before the next keystroke.

| | Working set | Private bytes |
|---|---:|---:|
| Idle, before Settings was opened | 136 MB | 212 MB |
| After opening and closing Settings | 31 MB | 262 MB |
| After 6 more opens and closes | 43–46 MB | 270 MB |

Private bytes don't come back: WinUI keeps ~50 MB of XAML/DirectX state after the first window and ~1.3 MB of
native memory per window created (measured with an empty `Window` too, so it is the framework's). Keystroke → popup
right after Settings was closed: p95 13.2 ms (12.2 ms without opening Settings; target 16 ms). Without the re-render
it was 16.3 ms, which is why it is there.
