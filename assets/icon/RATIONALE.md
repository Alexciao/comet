# Inlay icon

A white gem set into a teal tile, with a text caret right after it.

## Concepts considered

Sources are in `concepts/`. Contact sheets (16, 32 and 256 px on #F3F3F3 and #202020) are in
`screenshots/icon/concepts/`.

| Concept | Idea | Verdict |
| --- | --- | --- |
| a-socket | A text stub, a square piece in a socket, then a caret | At small sizes it reads as a media "skip to end" button. |
| b-page | A white page with text lines and a teal chip plus caret on the middle line | Easy to read, but it looks like any document icon and turns to mush at 16 px. |
| c-gem | A gem inlaid in a teal tile, followed by a caret | **Picked.** |
| d-field | A WinUI text box with a tall teal piece wedged into it | Wide shape, so it is tiny inside a square and weak at 16 px. |
| e-insert-mark | A piece above a split text line, with the proofreader's insertion caret (‸) below | Explains the idea well, but at small sizes it looks like a little figure with arms and legs. |
| f-gem-line | A gem sitting on a single text line | Looks like a keyframe or timeline marker. |
| g-colon-gems | A colon (the `:` trigger) made of inlaid dots, plus a caret | Too abstract. The two dots disappear at 16 px. |

## Why c-gem

- **Inlay, literally.** A gem set flush into a surface is what the word means. The caret after it
  says where it goes: into your text, at the cursor.
- **Unique.** No keyboard, speech bubble, letter or face. It is also clearly different from Comet's
  violet and orange comet.
- **Fits Windows 11.** A Fluent-style rounded tile with a soft diagonal gradient, gentle depth and
  no text. Teal is rare among nearby icons: Notepad, Photos and To Do are blue, Terminal is dark,
  Snipping Tool is pink-red and Sticky Notes is yellow.
- **One accent color.** Teal from #2FC8B8 to #086E69, with white and mint for the gem. The tile is
  mid-tone, so it stands out on both light and dark backgrounds.
- **Readable at 16 px.** A white diamond and a white bar on a solid tile still read at that size.

## Refinement notes

**Pass 1:** first draft of `inlay.svg` plus 16/20/24 px versions.
- The gem's four facets looked like a flat envelope, with visible seams between them. Fix: the gem
  is lit from above, with bright upper facets and mint lower facets. A base fill under the facets
  hides the seams.
- The socket groove was thick and read as a dark outline. Fix: it is about 6 px wide now, dark at
  the top and lighter at the bottom, so it looks sunk into the tile.
- The gem crowded the tile and the caret was shorter than the gem. Fix: the gem is smaller, and the
  caret is a little taller than the gem (like a real text caret) with a clear gap before it.
- 16 px: tested 4 gem placements. The one centred on x = 6.5 with a 4.5 px half-diagonal gives a
  crisp diamond and a 2 px caret on whole pixels.

**Pass 2:**
- The 24 px version had a 1 px socket ring and 16/20 px did not. In the tray this made 150 % look
  different from 100 % and 200 %. Fix: 24 px now drops the ring, so 16/20/24 px are one family with
  a larger gem.
- Corner radius 27 % looked iOS-like. Fix: 22 % (46/208), closer to Windows 11 tiles. Small sizes
  use 3.5, 4.5 and 5 px.
- Checked: the socket at 256 px reads as recessed, not outlined, so it stays.

## Export rules (`build.py`)

- App icon: 16/20/24 px come from `inlay-16/20/24.svg`. 32 px and up come from `inlay.svg`.
- Tray icon: always the simplified mark. 32/40/48 px are the 16/20/24 px drawings at 2×, so every
  display scale shows the same shape on the same pixel grid.
- Paused tray icon: the tray icon in greyscale at 70 % opacity. It still reads on light (#EEEEEE)
  and dark (#1C1C1C) taskbars. See `screenshots/icon/tray.png`.
- .ico files: sizes below 256 px are 32-bit DIBs, 256 px is a PNG. PNGs are compressed losslessly
  with oxipng.
