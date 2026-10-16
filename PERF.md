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
