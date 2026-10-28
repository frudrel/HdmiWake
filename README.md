# HdmiWake

Small Windows tray app that brings external monitors back after logon, unlock, resume from sleep/hibernate, and the display turning back on.

On some laptops (notably hybrid AMD/NVIDIA machines where HDMI is wired to the dGPU) Windows drops a still-connected external monitor from the active display layout after resume, leaving laptop-only mode. HdmiWake records which external outputs were active before sleep, lock, or display-off, then re-activates them afterwards and restores each monitor's saved resolution, refresh rate and position (Windows would otherwise pick a default ~60 Hz mode). With no saved mode it uses the highest refresh rate at the current resolution. Saved modes live in `%LOCALAPPDATA%\HdmiWake\modes.txt`. If the layout is already correct, it re-applies it to force a mode set that re-drives the link.

## Build

Uses the .NET Framework C# compiler that ships with Windows; nothing to install.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## Install

Copies the exe to `%LOCALAPPDATA%\HdmiWake`, adds a Startup shortcut (runs at every logon) and starts it.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Remove with `uninstall.ps1`.

## Use

- Tray icon: **Wake monitor now** (or double-click), **Open log**, **Exit**.
- `HdmiWake.exe --now` runs one wake sequence and exits.
- Settings in `%LOCALAPPDATA%\HdmiWake\settings.ini`:
  - `DelayMs` (default 2000): wait after an event before acting.
  - `Retries` (default 5): passes, 3 s apart, waiting for a slow monitor to reconnect.
- Log: `%LOCALAPPDATA%\HdmiWake\HdmiWake.log`.
