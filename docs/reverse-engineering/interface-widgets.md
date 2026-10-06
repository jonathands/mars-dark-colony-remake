# Interface widgets (`widget.c`, `button.c`, `list.c`, `gadget.c`)

The menus are text definitions in `intrface/*e` that `dc.exe` reads into
widget slots. The port reads them with `NativeScreenDefinition` and keeps the
drawing rules in `NativeWidgetRules` and `NativeListView`
(`src/DarkColony.Engine/Interface/`). The checks are in
`InterfaceChecks.cs` ("every intrface definition reads as widget.c reads it",
"buttons, text, lists and scroll bars follow button.c and list.c",
"interface colours come from dc.exe ...").

## Definitions and slots

Each widget line is `keyword id desc x y w h ...`, then options, then `-`
and the shared options (`bg`, `remap`, `intens`, `read_only`, ...,
`0x423669`). The keyword table is at `0x479470` ({name, constructor} pairs).
A slot is 0x34 bytes at `screen + 0x88 + id * 0x34`:

| Offset | Field |
| --- | --- |
| +1 | type: push button 2, check button 3, list 7, gadget 10, banim 12 |
| +2 / +3 / +4 | visible, enabled, highlighted |
| +8 | x1, y1, x2, y2 |
| +0x18 | background colour (-1: none) |
| +0x1C / +0x20 | text remap (default 7) and intensity (default 16) |
| +0x28 / +0x2C | the kind's data, the vtable |

`in_text` gives columns, rows and a font instead of a size. The old intro
screen (`buttonse`) gives a `picture` only a position.

## Colours

A screen starts with the five records at `0x479420` ({r, g, b, name}):
erase (164,164,255), ui (194,35,7), ui_highlight (192,64,64), ui_active
(255,0,0) and textfg (white). A `colour` line replaces a name it repeats and
appends a new one (`0x421790`). A widget that names no colour uses index 0
(erase) to clear and index 1 (ui) for a selection or scroll bar.

On screen a colour is the nearest entry of the screen palette: the captured
War lobby draws ui as entry 97, (203,23,23).

## Buttons (`0x426DE0`)

A push or check button has two picture numbers from the `pictures` sheet
(`knobe.spr`); a negative number is a brightness. Up, the brightness is the
highlight brightness when the pointer is over it, plus 16 or minus the
negative first number; down (pressed, or a check button checked) it is
`bright_pushed` plus 16 or minus the negative second number. "-11 2" is
picture 2 at 11, 15 when highlighted and 24 when down. The picture goes
through colour 0 of the remap (`interface-text.md`) at that brightness, and
the label uses the same brightness in the slot's remap.

Text (`0x426AF0`) runs in cells of the font's width plus one. The measured
width leaves out the last cell. Centred text is centred on that width;
right-aligned text ends one cell inside; left-aligned text (the default for
`label`) starts one cell in. The line is centred vertically.

Sounds: a push button's press plays sound 0x61 (BUTTON.WAV, `0x426F78`), as
does a check button's toggle (`0x4271D7`). When the pointer moves onto a push
or check button the poll plays 0x88 (HLIGHT.WAV, `0x42426F`).

## Lists and scroll bars

A list (`0x42A150`) clears its box to its background and draws rows of the
font height from the first row shown while a whole row fits. The selected
row is filled with `selbg` (index 1 when absent) and its text drawn at
brightness 0, which is black. Characters stop at the last whole cell.

- A press selects the row under it, or nothing past the last row
  (`0x42A38B`, event 7).
- A push button with `list n step` moves the first row shown by its step
  when pressed (`0x426F57`); it does not move the selection. The first row
  stays within 0 and the row count (`0x42A805`), so the list can scroll
  until it is empty.
- The bound scroll bar (`0x42A970`) is cleared to its background, framed in
  its `fg` colour (index 1 when absent), and the rows top to top + visible of
  the count are filled in that colour. A press takes the bar; a drag centres
  that span on the pointer and moves the list (`0x42AB2F`).

## Gadgets and `banim`

A gadget plays a FIN range with the animation clock of
`NativeAnimationTiming`: `anim_loop` 0, `anim_oneoff` 1, `anim_stopped` 2.
A one-off that runs past its last frame holds it and reports finished; a
stopped gadget still draws its frame. An `unmask` gadget clears its box to
its background first.

The event poll (`0x424144`) steps every gadget once per 33 ms elapsed (more
than 0x21 ms since the last pass, `elapsed / 0x21` steps). A `banim` runs
before the screen takes input (`0x427ADC`), redrawing at most every 17 ms
(`0x4242BC`): it plays 0xBA (ACTIVE.WAV) and starts its first gadget; when
gadget j reaches frame 2 the next one starts as a one-off, with the sound
again. When a gadget finishes it is hidden (its box cleared to erase) and the
next of its buttons that is visible is drawn; buttons the screen hid are
skipped (newgamee shows START CAMPAIGN or START TRAINING).

## The new campaign screen (`0x401E50`)

`newgamee` over `choo`, with `sound/newsnd.dat` (sound 1 is REZIN.WAV at
-1000). After its banim it shows gadget 21 (HREZIN) as a one-off and plays
REZIN.WAV, starts the decorations 13-16 and 29-35 looping, and disables the
race check buttons.

- HUMAN (check button 0, either event): if the Human loop is not already
  showing, stop and replay REZIN.WAV, hide and rewind gadgets 21-26, then
  play 21 (HREZIN) and 25 (AREZOUT) once. GRAY plays 22 (HREZOUT) and 24
  (AREZIN). The check buttons are disabled meanwhile.
- When both one-offs finish, gadget 23 (HLOOP) or 26 (ALOOP) loops and the
  check buttons are enabled again.
- Label 18 ("Type in a name for your leader") pulses: its intensity climbs
  0 to 31 and back, one step per pass of the event loop.
