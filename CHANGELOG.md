# Changelog

All notable changes to Inlay (called Comet before 1.1) are listed in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-10-20

Comet is now Inlay. Installing 1.1 over Comet 1.0 upgrades it in place and keeps your settings, snippets, shorthands and excluded apps.

### Added

- **Images**: your own images and GIFs under names you choose. `:lgtm` pastes your LGTM GIF, `:screenshot` pastes your newest screenshot.
- Add an image, a folder (newest, random, or pick from a strip of the latest few), or a collection, where every image in a folder gets its own name and stays in sync.
- Images paste as a picture (GIFs stay animated), attach as a file, or type their path for terminals, editors and Markdown. The Latest screenshot preset sets up `:screenshot` in one click.
- **Symbols**: about 300 of them, from arrows and math to currency, fractions, keyboard symbols and Greek letters. `:arrow_right` gives →, `:deg` gives °, `:euro` gives €, `:half` gives ½. You can turn them off on the Emoji page.
- **Recent items**: type `:` and pause, and Quick Insert opens with the 8 items you use most.
- **Keyboard shortcut**: **Win+Alt+I** opens Quick Insert without typing `:`, in every app except password fields and windows running as administrator. You can change it or clear it in Settings.
- **Create a snippet from Quick Insert**: when nothing of yours matches, the last row offers "Create snippet :name". Save it and Inlay inserts it where you were typing.
- **Per-app modes**: each app can show everything, hide emoji, or keep Quick Insert off.
- **See what's new**: when a new version is out, it shows that version's notes. Settings › General › About shows the notes for the version you have.

### Changed

- The emoji picker is now **Quick Insert**: one menu for your snippets, images, emoji and symbols. Each row shows the code on one line and a caption under it.
- Built-in codes now all use snake_case (`:thumbs_up`, `:arrow_right`), and other spellings such as `:thumbsup` or `:arrow-right` still find them.
- Auto-replace is now called **Shorthands**. Your entries carry over unchanged.
- Excluded apps are now per-app modes. Apps you excluded are set to Off.
- Slack, Discord, Teams, Telegram and WhatsApp now hide emoji instead of being excluded, since they have their own emoji pickers. Your snippets and images show up there.
- Export settings now saves one `.inlay` file that includes your images. Import still reads Comet 1.0 backups.
- The update checks now run at launch and once a day instead of once a week. Inlay still only tells you about a new version and never installs anything.
- Settings has new Quick Insert and Images pages, and General comes first.
- Pause in the tray now lets you choose how long: for 15 minutes, for 1 hour, or until you resume.
- The tray icon is now in full color, and dims while Inlay is paused.

### Fixed

- On the US-International keyboard, a dead key (`'`, `"`, `~`) right before the end of a shorthand no longer loses the next character.
- The Import confirmation dialog now has the standard Windows look.
- Turning Emoji off now frees the memory its data was using.
- The tray icon is sharp at every display scale.

## [1.0.0] - 2026-10-19

The first release, under the name Comet.

### Added

- **Emoji picker**: type `:` and two letters in any text field and a list of emoji appears at the caret. Search knows GitHub and Slack shortcodes, Unicode names and everyday words, forgives typos and abbreviations, and puts the emoji you use most first.
- Your default skin tone is applied to every emoji that has one.
- **Snippets**: saved text with a short name, inserted from the same list. Placeholders fill in the date or time in any format, an offset date, the weekday or the clipboard, and `{cursor}` sets where the caret ends up.
- **Auto-replace**: abbreviations that expand when you finish the word. The replacement follows how you typed it, and Backspace right after an expansion undoes it.
- **Per-app exclusions**: turn the picker or auto-replace off in specific apps. Slack, Discord, Teams and Telegram, which have their own `:` picker, are excluded by default.
- **Pause** from the tray stops everything until you resume.
- **Start with Windows**, on by default.
- **Silent emoji data updates**: newer emoji data is downloaded in the background, with a switch to turn it off.
- **Update notifications**: Comet tells you when a new version is out, and never installs anything by itself.
- Installer (per user, no admin rights) and portable zip, for x64 and Arm64.

[1.1.0]: https://github.com/Alexciao/inlay/releases/tag/v1.1.0
[1.0.0]: https://github.com/Alexciao/inlay/releases/tag/v1.0.0
