# Clipwise

A Windows clipboard manager that merges live clipboard captures with the Windows clipboard history (Win+V) into one searchable, pinnable list.

Built with WPF on .NET 9. Runs in the system tray; opens as a borderless, topmost overlay.

## Features

- **Live capture** — text and images are captured as they hit the clipboard.
- **Win+V integration** — items from the Windows clipboard history appear alongside live captures; restore and delete operate on the history API when supported.
- **Win+V override** — optionally redirects the Win+V shortcut to open Clipwise instead of the Windows history (on by default; toggle in `%APPDATA%\Clipwise\settings.json`).
- **Search** — type to filter as you type.
- **Pin** — pinned items stick to the top and persist across restarts (including image bytes).
- **Global hotkey** — `Ctrl+`` opens the overlay; falls back to `Ctrl+Shift+`` if the first binding is taken.

## Usage

| Key | Action |
|-----|--------|
| Enter | Paste selected item |
| Del  | Remove selected item |
| P    | Pin / unpin selected item |
| Esc  | Close overlay |

Open the overlay with `Ctrl+`` (or Win+V when the override is on). The window keeps its history fresh while idle.

## Requirements

- Windows 10 19041 (20H1) or later
- .NET 9 Desktop Runtime (for framework-dependent builds)
- Windows clipboard history enabled for Win+V items to appear

## Build

```powershell
dotnet build -c Release
# or single-file publish
dotnet publish -c Release
```

Run `Clipwise.exe` from the output directory. State lives in `%APPDATA%\Clipwise` (`store.json` + `settings.json`).

## Tests

PowerShell smoke/regression scripts simulate input and inspect the overlay window:

- `smoke.ps1` — launch and UI smoke test
- `winvtest.ps1` — Win+V hook tests
- `winvregress.ps1` — Win+V override regression suite
