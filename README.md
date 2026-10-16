# Comet

Emoji, snippets and auto-replace for any text field on Windows 11.

- **Emoji picker.** Type `:` and two letters anywhere (`:fir`) and a small list appears at the caret. Keep typing
  to filter, use ↑/↓ or Tab/Shift+Tab to move, Enter or Tab to insert, Esc to cancel, or click a row. Typing a
  whole code such as `:fire:` replaces it directly. Search knows GitHub and Slack shortcodes, CLDR names and
  keywords and everyday words (`:love`, `:lol`, `:ok`), forgives typos and abbreviations (`:thnmbs`, `:hrt`),
  and learns which emoji you use. Your default skin tone is applied automatically.
- **Snippets.** Saved text with a name, offered in the same `:` list (`:addr` → your address). Snippets can
  contain `{date}`, `{time}`, `{date:dddd d MMMM yyyy}`, `{date+7d}` / `{date-1d}` / `{date+2w}` / `{date+1m}`
  (optionally with a format: `{date+1d:dd/MM}`), `{weekday}`, `{clipboard}`, and `{cursor}` (where the caret ends
  up). `{{` and `}}` insert literal braces; anything malformed is inserted as typed and flagged in the editor.
- **Auto-replace.** Abbreviations that expand when you finish the word (`cmq` + space → `comunque `). The
  replacement follows the case you typed (`Cmq` → `Comunque`) unless you tick *Match case*; the space, Enter or
  punctuation you typed is kept; Backspace right after undoes the replacement.

Each feature can be switched off on its own. The snippet and auto-replace lists start empty — they are yours.

Comet stays out of the way: it never takes focus, ignores password fields and windows running as
administrator, and has its own exclusion lists (apps with their own `:` picker — Slack, Discord, Teams,
Telegram — are excluded from the picker by default). Pause everything from the tray icon.

## Install

Download from the releases page (or build, below):

- `Comet-<version>-setup.exe` — installs for your user only (no administrator rights) into
  `%LocalAppData%\Programs\Comet`, adds a Start menu entry and starts Comet. Running a newer setup updates in
  place: the running Comet is asked to quit, replaced, and started again. Uninstall from *Settings → Apps*;
  your settings and lists are kept in `%LocalAppData%\Comet`.
- `Comet-<version>-portable.zip` — unzip anywhere and run `Comet.exe`. Before deleting the folder, turn off
  *Start at login* in Settings (or remove the `Comet` value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).

The installer isn't code-signed, so SmartScreen may ask for confirmation the first time.

## Settings and files

Left-click the tray icon (or launch Comet again) to open Settings. Right-click for Settings, Pause/Resume and
Quit.

Everything lives in `%LocalAppData%\Comet`:

| File | Contents |
|---|---|
| `settings.json` | features, start at login, trigger character, minimum letters, skin tone, excluded apps |
| `snippets.json` | your snippets |
| `autoreplace.json` | your replacements |
| `frecency.json` | which emoji and snippets you use (for ranking); *Reset* in Settings clears it |
| `emoji-update.json`, `emoji-data.bin` | state and data of the silent emoji updates |
| `logs\comet.log` | events and timings only — never what you type |

The files are plain JSON with a schema version, written atomically with a `.bak` copy of the previous version.
Edits made by hand while Comet runs are picked up immediately. A file that can't be read is set aside as
`*.corrupt-<time>.json`, the backup is used if it's good, and Settings shows what happened. Snippets and
replacements can be exported and imported as JSON from their pages.

## Emoji updates

New emoji arrive without an app update: at most once a week (and not in the first two minutes after start),
while Emoji is on and Comet isn't paused, Comet makes one small HTTPS request for a manifest. Only when it
announces newer data does it download the data pack (≈650 KB), verify its size, SHA-256 and contents, and swap
it in while the list is closed. Checks are skipped on metered connections and fail silently offline. Turn it off
with *Update emoji automatically* in Settings → General. Nothing else is ever sent anywhere.

The packs are built every week by `.github/workflows/emoji-data.yml` from the upstream datasets, with the same
builder and the same golden search tests as the built-in data, and published to the `emoji-data` release.

At load time Comet checks every emoji against the installed Segoe UI Emoji and hides the ones it can't draw as
a single picture, so a Windows font update reveals new emoji by itself.

## Build

Requirements: Windows 11, the .NET 10 SDK, and for Native AOT publishing the "Desktop development with C++"
workload of Visual Studio Build Tools (the linker; the IDE isn't needed). Rider or VS Code work as editors.

```powershell
dotnet build Comet.sln                     # everything, warnings are errors
dotnet run --project src/Comet.App         # run (Debug, JIT)
dotnet test tests/Comet.Core.Tests         # search (incl. the golden set), picker, auto-replace, snippets, settings
dotnet test tests/Comet.Platform.Tests     # key translation (Italian/US layouts), SendInput encoding, font check, updater
pwsh installer/build.ps1                   # Native AOT publish + installer + portable zip -> artifacts/installer
```

The version comes from `Directory.Build.props` only.

`tools/Comet.DataBuilder` compiles the emoji datasets in `data/sources` into `src/Comet.Core/Data/emoji.bin`;
`tools/Comet.IconBuilder` draws the app and tray icons.

### End-to-end tests

`tests/Comet.E2E` launches Comet and real apps (Notepad, Edge, Windows Terminal, and VS Code and Word when
installed) and types with real, layout-aware keystrokes. It takes over keyboard and focus while it runs, only
ever types into windows it opened itself, and keeps the display awake. Run it when you're not using the PC:

```powershell
dotnet build tests/Comet.E2E; tests/Comet.E2E/bin/Debug/net10.0-windows10.0.26100.0/Comet.E2E.exe
```

| Variable | Effect |
|---|---|
| `COMET_EXE` | test a published build (the latency target is asserted only then) |
| `COMET_SCREENSHOTS=<folder>` | take the review screenshots and flicker frame captures |
| `COMET_SOAK=10000` | run the soak test (about 20 minutes) |
| `COMET_E2E_PACE_MS` | delay between keystrokes (default 60) |

Performance numbers are in [PERF.md](PERF.md), design decisions in [DECISIONS.md](DECISIONS.md).

## Known limitations

- **Windows 11 Notepad** applies keys typed in the same instant as an insertion against the live keyboard
  state; Comet edits Notepad directly to avoid this, but keys typed faster than ~200 words per minute right
  after an insertion can still come out wrong in Notepad (not in other apps).
- **Administrator windows** get no picker or replacements unless Comet itself runs as administrator (Windows
  doesn't let a normal app type into them).
- **Password fields** are detected through UI Automation and the classic Edit style; a custom control that
  exposes neither isn't recognized as a password field.
- **Country flags** aren't offered: Segoe UI Emoji draws them as two letters. Emoji newer than the installed
  font are hidden until Windows updates the font.
- **Remote desktop and virtual machine windows** receive text through the clipboard (they mistranslate typed
  Unicode); Comet restores the clipboard afterwards. Apps where typed emoji arrive broken can be switched to the
  clipboard method in `settings.json` (`insertionMethods`).
- **IME composition** (Chinese, Japanese, Korean input) cancels the picker; Comet doesn't interfere with it.
- The first time an emoji outside the ~500 most popular is shown costs one glyph rasterization (~20 ms); after
  that it's cached.
- Memory: about 130 MB working set once the emoji pictures are pre-rendered (most of it is the WinUI 3 runtime).
- Display scaling other than 100% is supported (per-monitor DPI aware) but was not visually checked.
- The emoji update URL points at `github.com/Alexciao/comet`; see [BLOCKERS.md](BLOCKERS.md).

## Credits

Emoji data: [emojibase](https://emojibase.dev) (MIT), [gemoji](https://github.com/github/gemoji) (MIT),
[emojilib](https://github.com/muan/emojilib) (MIT), and the Unicode Consortium's emoji frequency data. Licenses
are in `data/sources`.
