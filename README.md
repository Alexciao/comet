<div align="center">

<img src="docs/images/logo.png" width="96" alt="">

# Inlay

**Everything you type often, a few keys away.**

[![Latest release](https://img.shields.io/github/v/release/Alexciao/inlay?label=release)](https://github.com/Alexciao/inlay/releases/latest)
[![CI](https://github.com/Alexciao/inlay/actions/workflows/ci.yml/badge.svg)](https://github.com/Alexciao/inlay/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Windows 11](https://img.shields.io/badge/Windows-11-0078D4)

</div>

Inlay sits in your tray and works in every text field on Windows. Type `:email` and your address is in. Type
`:screenshot` and your latest screenshot is pasted. `:fire` gets you 🔥, `:deg` gets you °. And `brb` turns into
*be right back* without opening anything.

**Comet is now Inlay.** Same app, new name. Installing 1.1 over Comet 1.0 keeps your settings, snippets and
shorthands. See [what's new](CHANGELOG.md).

> [!NOTE]
> **Notice on AI-generated code**<br>
> Most of Inlay's code was written with the help of AI coding agents.<br>
> It is covered by extensive unit and end-to-end tests, and every feature was also tested by hand to keep the
> quality bar high.<br>
> Inlay works fully offline. It never logs what you type and never sends it anywhere.<br>
> The only network requests are two optional update checks to GitHub (emoji data and new Inlay versions), made quietly at launch and once a day, which you can turn off in Settings.

## Features

### Quick Insert

One menu for everything you insert. Type `:` and a couple of letters in any text field, and Quick Insert opens at
the caret with your snippets, your images, emoji and symbols. Keep typing to filter. Search forgives typos
(`:thnmbs`) and abbreviations (`:hrt`), and learns what you use, so your usual picks rise to the top. Each row shows
the code on one line and a caption under it.

| Key | Does |
|---|---|
| `:` + letters | opens Quick Insert (two letters by default) |
| `:` and a pause | opens it with your recent items |
| **Win+Alt+I** | opens it without typing `:` |
| ↑ / ↓, Tab / Shift+Tab | move the selection |
| ← / → | choose an image in a folder's strip |
| Enter or Tab | insert the selected item |
| Esc | close the menu |
| `:code:` or `:code` + space | insert that exact match directly |

**Recent items.** Type `:` and pause for half a second, and Quick Insert opens with the 8 items you use most, each
marked with a history icon. The pause can be anywhere from 300 to 1500 ms, and you can turn this off.

**Keyboard shortcut.** **Win+Alt+I** opens Quick Insert without typing the trigger, so nothing lands in the app
until you pick something. What you type shows in a search line at the top of the menu. It works in every app
except password fields and windows running as administrator. You can change the shortcut in Settings, or clear it.

**Create a snippet from Quick Insert.** When nothing of yours matches, the last row reads *Create snippet :name*.
It opens Settings with the new snippet's name filled in. Write the text, click Save, and Inlay inserts it where you
were typing.

**Per-app modes.** Each app can be set to:

- **Show everything**: snippets, images, emoji and symbols.
- **Hide emoji**: Quick Insert only appears when one of your own snippets or images matches. This is the default
  for Slack, Discord, Teams, Telegram and WhatsApp, which have their own emoji pickers.
- **Off**: typing `:` never opens it there. The keyboard shortcut still works.

The trigger character can be changed. Settings always shows the one you use; this page uses `:`.

#### Snippets

Text you type often, saved under a name: `:email` becomes your address, `:sig` your signature. Placeholders fill in
when the snippet is inserted.

| Placeholder | Inserts |
|---|---|
| `{date}`, `{time}` | today's date, the current time |
| `{date:dddd d MMMM yyyy}` | a date or time in any .NET format |
| `{date+7d}`, `{date-1d}`, `{date+2w}`, `{date+1m}` | an offset date (`{date+1d:dd/MM}` with a format) |
| `{weekday}` | today's weekday |
| `{clipboard}` | the text on the clipboard |
| `{cursor}` | where the caret ends up |
| `{{`, `}}` | literal braces |

#### Images

Your own images and GIFs, under names you choose. `:lgtm` pastes your LGTM GIF. `:screenshot` pastes your newest
screenshot. Rows in the menu show a thumbnail, and a larger preview appears beside it.

There are three kinds:

- **Image**: one file. Inlay copies it into its own library, so it keeps working if you move or delete the
  original.
- **Folder**: one name for a folder. Pick **Newest**, **Random**, or **Let me pick**, which shows a strip of the
  newest few thumbnails to choose from with ← and →.
- **Collection**: every image in a folder becomes its own entry, named after the file, with an optional prefix. It
  stays in sync as files come and go.

Add keywords to find an image by other words. **Insert as** decides what the app receives:

| Insert as | What happens |
|---|---|
| **Image** (default) | pastes the picture; GIFs stay animated |
| **File** | attaches it as a file |
| **File path** | types the path, for terminals, editors and Markdown |

To add images, drag files onto the Images page in Settings, or press Ctrl+V there to add the image on the
clipboard. The **Latest screenshot** preset creates `:screenshot` in one click. Images cost nothing while you don't
use them: with an empty list or the feature off, Inlay watches no folders and makes no thumbnails.

#### Emoji

GitHub and Slack shortcodes, Unicode names and everyday words: `:fire` gets you 🔥, and `:love` and `:lol` work
too. Your default skin tone is applied for you.

**Symbols** are part of Emoji: about 300 of them, covering arrows, math, degrees and units, currency, typography
(dashes, quotes, ©, ™), fractions, superscripts and subscripts, check marks, keyboard symbols (⌘ ⇧ ⏎) and Greek
letters. You can turn them off on the Emoji page.

| Type | Get |
|---|---|
| `:arrow_right` | → |
| `:deg` | ° |
| `:euro` | € |
| `:half` | ½ |
| `:check` | ✓ |
| `:alpha` | α |
| `:em_dash` | — |

Every built-in code is lowercase `snake_case`: `:thumbs_up`, `:heart_eyes`, `:arrow_right`, `:em_dash`. Typing
`:arrow-right` or `:arrowright` finds the same symbol. Your own snippets and images can have any name you like.

### Shorthands

Abbreviations that expand as you type, without opening anything. Finish the word and it is replaced: `brb` + space
becomes `be right back `. The expansion follows how you typed it (`Btw` → `By the way`, `BTW` → `BY THE WAY`)
unless *Match case exactly* is on, and the space, Enter or punctuation you typed is kept. **Backspace** right after
an expansion undoes it. Each app can be excluded.

### Everything else

Snippets, Images, Emoji, Symbols and Shorthands can each be switched off. **Pause** from the tray stops everything
for 15 minutes, for 1 hour, or until you resume; the tray icon dims while Inlay is paused. Inlay never takes focus,
and stays out of password fields, apps running as administrator and IME input.

**Export settings** saves one `.inlay` file with your settings, snippets, shorthands, images and the image
library. **Import** reads it back, and also reads backups made with Comet 1.0.

When a new version of Inlay is out, Settings and the tray menu tell you, and **See what's new** shows its notes.
Inlay never installs anything by itself. Settings › General › About shows the notes for the version you have.

## Install

| | x64 | Arm64 |
|---|---|---|
| **Installer** (per user, no admin rights) | [Inlay-Setup-x64.exe](https://github.com/Alexciao/inlay/releases/latest/download/Inlay-Setup-x64.exe) | [Inlay-Setup-arm64.exe](https://github.com/Alexciao/inlay/releases/latest/download/Inlay-Setup-arm64.exe) |
| **Portable** (unzip and run; data stays in the folder) | [Inlay-Portable-x64.zip](https://github.com/Alexciao/inlay/releases/latest/download/Inlay-Portable-x64.zip) | [Inlay-Portable-arm64.zip](https://github.com/Alexciao/inlay/releases/latest/download/Inlay-Portable-arm64.zip) |

Checksums: [SHA256SUMS.txt](https://github.com/Alexciao/inlay/releases/latest/download/SHA256SUMS.txt).

**Requirements:** Windows 11 (build 22000 or later). Windows 10 is not supported: the installer and the app
require Windows 11, and it hasn't been tested there.

**SmartScreen:** the builds aren't code-signed yet, so Windows may say it protected your PC. Click **More info** →
**Run anyway**.

**Coming from Comet 1.0:** run the installer. It upgrades Comet in place, and your settings, snippets, shorthands
(auto-replacements) and excluded apps move over by themselves. Excluded apps become per-app modes.

## Settings

Left-click the tray icon (or start Inlay again) to open Settings. Changes apply and save at once.

| Page | What's there |
|---|---|
| **General** | start with Windows, update checks, export and import, and About with this version's notes |
| **Quick Insert** | the trigger character, minimum letters, keyboard shortcut, recent items (and clearing them), creating snippets from the menu, per-app modes |
| **Snippets** | add, search, edit, duplicate, import and export snippets, with a live preview |
| **Images** | your images, folders and collections, with drag and drop, Ctrl+V and the Latest screenshot preset |
| **Emoji** | emoji and symbols on or off, the default skin tone |
| **Shorthands** | your abbreviations, each with its own switch, and the apps where they don't expand |

## Performance

Release build (Native AOT), measured; details in [docs/performance.md](docs/performance.md).

| | |
|---|---:|
| Keystroke → Quick Insert on screen (p95) | 12.5 ms |
| Search, worst case per keystroke | 0.32 ms |
| Start → ready | 113–148 ms |
| Tray click → Settings drawn (warm / first) | 154–168 ms / 217–243 ms |
| CPU while idle | ≈ 0 (Inlay's threads: 0 cycles) |
| Download (installer) | 18 MB |

## Privacy

Inlay contacts exactly two addresses, at launch and once a day, with only a `User-Agent: Inlay/<version>` header:

| Address | Why | Turn off |
|---|---|---|
| `https://github.com/Alexciao/inlay/releases/download/emoji-data/manifest.json` | newer emoji data | Settings › General › Update emoji automatically |
| `https://api.github.com/repos/Alexciao/inlay/releases/latest` | a newer Inlay version (it only tells you, it never installs anything) | Settings › General › Notify me about Inlay updates |

Neither runs while Inlay is paused or on a metered connection. A test fails the build if any other network code
appears. The local log in `%LocalAppData%\Inlay\logs` records events and timings, never typed text.

## Build from source

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download), Visual Studio 2022 Build Tools with the
**Desktop development with C++** workload (for Native AOT), and the Windows 11 SDK (10.0.26100).

```pwsh
dotnet build Inlay.sln                      # Debug build
dotnet test tests/Inlay.Core.Tests          # unit tests incl. the search golden set
dotnet test tests/Inlay.Platform.Tests
./installer/build.ps1 -Version 1.1.0        # Native AOT publish, installer and portable zip (needs Inno Setup 6)
```

The end-to-end tests (`tests/Inlay.E2E`) drive a real desktop with real keystrokes; run them when you're away from
the keyboard. The solution opens in Rider or Visual Studio. Changes between versions are in
[CHANGELOG.md](CHANGELOG.md).

## License and acknowledgements

[MIT](LICENSE). Inspired by [Rocket](https://matthewpalmer.net/rocket/) for macOS. Emoji data from
[emojibase](https://github.com/milesj/emojibase), [gemoji](https://github.com/github/gemoji),
[emojilib](https://github.com/muan/emojilib) and [Unicode CLDR](https://cldr.unicode.org/); see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
