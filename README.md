# Snap

macOS-style screenshots on Windows.

Tap **Ctrl+Shift** (press both, let go) and drag a box. The shot is copied to the clipboard, saved to
`Pictures\Screenshots`, and a thumbnail sits in the bottom-right corner for five seconds. Drag the
thumbnail straight into Slack, an email, a chat or a folder. Click it to open the file.

| Keys | What it does |
|---|---|
| Tap Ctrl+Shift | Drag to select an area |
| Ctrl+Shift+4 | Same thing |
| Ctrl+Shift+3 | The whole screen under the mouse |
| Click without dragging | The whole screen you clicked on |
| Esc or right-click | Cancel |

Right-click the thumbnail to copy, open, show in folder or delete. It stays put while the mouse is
over it.

## Install

Windows 10 or 11. No SDK needed: it compiles with the C# compiler that already ships with Windows.

```powershell
git clone https://github.com/rhymepoets/snap
cd snap
powershell -ExecutionPolicy Bypass -File install.ps1
```

That builds `Snap.exe` into `%LOCALAPPDATA%\Snap`, adds it to Startup and runs it. Left-click the tray
icon to capture, right-click it to quit. To remove it:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```

## How the tap works

Ctrl+Shift on its own does nothing in most apps, so it's a free shortcut. A tap only counts when
nothing else happened while both keys were down: no other key, no click, no scroll, and you let go
within 1.5 seconds. Ctrl+Shift+T, Ctrl+Shift+click and the rest keep working as before.

## Notes

- Files are named the way macOS names them: `Screenshot 2026-11-17 at 3.42.10 PM.png`.
- DPI-aware, and the selection overlay covers every monitor.
- The keyboard hook runs on its own thread, so a slow clipboard can't make Windows drop it.
- One C# file, no dependencies.

## License

MIT
