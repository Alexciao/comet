# Performance

Machine: 12 logical cores, Windows 11 (build 26200), .NET 10.0. All numbers are measured, not estimated.

## Search (BenchmarkDotNet, `tests/Comet.Benchmarks`, full set of 1,923 emoji + 2 snippets, 24 results)

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

Measured inside Comet from the hook callback for the key to the composition frame that shows the updated popup,
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
| Idle CPU (60 s, popup closed, no typing) | 47–63 ms per minute (≈0.08% of one core; Task Manager shows 0%) | per-thread cycle counts: Comet's own threads (hook, engine, context worker, UI) **0 cycles**; the rest is the GPU driver's worker threads (`nvwgf2umx.dll`, created with the composition device) and the .NET finalizer |
| `Comet.exe` | 9.3 MB | Native AOT, single native image |
| Publish folder | 105 MB, of which 38.5 MB is `Comet.pdb` | the shipped package leaves the PDB out (symbols are a separate artifact): **66 MB** installed, Windows App SDK runtime included |

Memory note: the emoji bitmap cache is the one sizeable cost under Comet's control. Packing pre-rendered emoji
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

Mix per 10 triggers: 1 auto-replacement, 2 emoji inserted with Enter, 7 popups opened, browsed and cancelled.

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
