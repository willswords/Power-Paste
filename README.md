# PowerPaste

A small stand-alone Windows app that copies ClipMate's **PowerPaste** feature: copy a series of items, pick the first one, turn on PowerPaste, and each **Ctrl+V** (or **Shift+Insert**) in any other app pastes the next item.

## Running it

Double-click `PowerPaste.exe`. It runs from the system tray. Closing the window hides it there; use **File → Exit** or the tray menu to quit.

To rebuild after changing `PowerPaste.cs`, run `build.cmd`. It uses the C# compiler that comes with Windows, so nothing needs to be installed.

## Using PowerPaste

1. Copy your items as usual. Each copy becomes a clip, and the newest is at the top of the list.
2. Click the clip to start from. Usually that's the oldest clip in the series, near the bottom.
3. Click the **PowerPaste** button (the gear, far left).
   - First click: PowerPaste turns on in your last-used direction (▲ Up or ▼ Down).
   - Click again **before pasting anything**: the direction flips. The app remembers this direction for next time.
   - Click again after pasting, or after a flip: PowerPaste turns off.
4. Switch to the target app and paste repeatedly. The current clip is highlighted yellow, and the status bar shows what the next paste will be.
5. After the last item, PowerPaste beeps and turns off.

**Extended selection:** Ctrl+click or Shift+click several clips and PowerPaste uses only those. Otherwise it runs from the selected clip to the end of the list in the direction of travel.

**Changing the starting clip:** while PowerPaste is on, click a different clip in the list and PowerPaste restarts from it.

**Explode Into Fragments** (Edit menu or toolbar): this splits each clip into pieces. With the default settings, the clip `January, 31` / `February, 28` pastes as *January*, *31*, *February*, *28*, and then PowerPaste moves to the next clip. Choose the delimiters (lines, commas, tabs, semicolons, spaces, custom characters), quote handling and trimming in **File → Options**, which includes a live preview.

**Choosing the starting fragment:** while Explode is on, a pane below the clip list shows the fragments of the starting clip.
- Click a fragment to start there, so earlier fragments are skipped.
- Double-click a fragment to start PowerPaste right away.
- While PowerPaste is running, the pane shows the current clip. Click any fragment to make it the next paste, whether that's going back or skipping ahead.

## Editing clips

- **Edit a clip:** double-click it, press **F2**, or use the **Edit** button or right-click menu. Press **Ctrl+Enter** or click OK to save. While Explode is on, the editor shows how many fragments the text will produce. If you edit the clip PowerPaste is on, the next paste uses your changes.
- **Add a clip by typing it:** **Edit → New Clip** (Ctrl+N).
- **Copy a clip back to the clipboard:** select it and press **Enter**.

**PowerPaste Loop** (Edit menu or toolbar): after the last item, PowerPaste beeps twice and starts over. Click the button to stop.

The tray menu has the same options: PowerPaste Up/Down, Stop, Explode, Loop.

## Notes and limitations

- It handles **text clips only**. Images and files are ignored.
- A paste is detected from the keystroke (Ctrl+V / Shift+Insert). Right-click → Paste and toolbar Paste buttons in other apps paste the current item but **don't advance** to the next one.
- Pastes into apps running as Administrator aren't detected unless PowerPaste also runs as Administrator. This is a Windows security restriction.
- Clips are saved in `%APPDATA%\PowerPaste\clips.xml` so they are still there after a restart. Copies that password managers mark as private are skipped. To stop saving clips, turn off **Save clips between sessions** in Options.
