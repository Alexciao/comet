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
