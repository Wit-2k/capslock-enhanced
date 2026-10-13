# capslock-enhanced

`capslock-enhanced` is the C#/.NET 10 NativeAOT remake of the CapsLock+ mini keyboard utility. It preserves the supported CapsLock movement, selection, deletion, Enter, and reload shortcuts without an installed AutoHotkey or .NET runtime.

This repository is derived from [Wit-2k/capslock-plus-mini](https://github.com/Wit-2k/capslock-plus-mini), a fork of [wo52616111/capslock-plus](https://github.com/wo52616111/capslock-plus).

## Scope

Implemented:

- CapsLock-modified movement, selection, and deletion shortcuts
- `CapsLock+Enter` and `CapsLock+F5` worker reload
- Safe `[Keys]` overrides with live INI reload
- A watchdog worker, crash logging, and an elevated logon-task installer

Not implemented:

- CapsLock clipboard slots, clipboard monitoring, or system clipboard interception
- Window binding, always-on-top, and transparency shortcuts
- qbar, translation GUI/API, Math Board, and script plugins
- `CapsLock+F1`, `CapsLock+F4`, and `CapsLock+F6`; these shortcuts pass through

## Requirements

- Windows 10 or newer, x64
- No AutoHotkey or .NET runtime is required to run the NativeAOT `remake.exe`.

## Run

Publish with `dotnet publish -c Release` from this directory, then run `remake.exe` from the publish output. Its manifest requests administrator rights; launching it directly can show UAC.

The tray icon is embedded in `remake.exe`, so you can copy and rename the executable for manual use without `capslock+icon.ico`. The auto-start scripts are separate: keep them beside an executable named `remake.exe`, since they locate that exact filename.

Prebuilt binaries are attached to [Releases](https://github.com/Wit-2k/capslock-enhanced/releases).

To start at login without a recurring UAC prompt, copy the published files together and run `install-autostart.ps1` once from an elevated PowerShell. It registers a highest-privilege logon task and a minute-by-minute recovery task. An intentional tray exit pauses recovery until the next logon or manual launch. Remove both tasks with `install-autostart.ps1 -Uninstall`.

Press `CapsLock+F5` to restart the worker. Right-click the tray icon and choose **退出** to exit.

Diagnostics live under `%LOCALAPPDATA%\CapsLockPlus\`: the log is `logs\remake.log` (rotated at 2 MB, two backups) and crash dumps are stored in `dumps` (up to 10 files). Dumps contain process memory and may include unsaved document content; review a dump before sharing it.

## Default Shortcuts

In the tables below, `CL` means CapsLock and `LAlt` means left Alt.

### Cursor Movement

| Shortcut | Action |
|---|---|
| `CL+E/S/D/F` | Move up/left/down/right |
| `CL+A/G` | Move one word left/right |
| `CL+P` | Move to line start |
| `CL+;` | Move to line end |
| `CL+LAlt+P` | Move to document start |
| `CL+LAlt+;` | Move to document end |

### Selection

| Shortcut | Action |
|---|---|
| `CL+I/J/K/L` | Select up/left/down/right |
| `CL+H` | Select one word left |
| `CL+.` | Select one word right |
| `CL+,` | Select current word |
| `CL+LAlt+,` | Select current line |
| `CL+U/O` | Select to line start/end |
| `CL+LAlt+U/O` | Select to document start/end |

### Deletion

| Shortcut | Action |
|---|---|
| `CL+W` | Backspace |
| `CL+R` | Delete |
| `CL+[` | Delete to line start |
| `CL+/` | Delete to line end |
| `CL+LAlt+[` | Delete to document start |
| `CL+LAlt+/` | Delete to document end |
| `CL+Backspace` | Delete current line |

## Unsupported Features

The C# remake does not read, write, or monitor the clipboard. `CapsLock+C/X/V` and `CapsLock+LAlt+C/X/V` pass through; normal `Ctrl+C/V` remain native Windows shortcuts.

Window binding, always-on-top, and transparency are outside the plan. `CapsLock+F1/F4/F6` and unsupported window-function overrides are not handled by the remake and pass through.

### Other

| Shortcut | Action |
|---|---|
| `CL+Enter` | Insert a new line below |
| `CL+F5` | Restart the worker |

## Configuration

Runtime settings are stored in `CapsLock+settings.ini` next to `remake.exe`. It is generated automatically on first launch, reloaded while the program runs, and ignored by git.

Override shortcuts in the `[Keys]` section:

```ini
[Keys]
caps_f7=keyFunc_doNothing
caps_lalt_f=keyFunc_moveRight(10)
```

Only functions whose names start with `keyFunc_` are accepted. Chord names use `caps_`, `caps_lalt_`, and `caps_win_` prefixes; `press_caps` overrides the plain CapsLock tap.

## Development Notes

- This repository is the NativeAOT C# implementation; `remake.exe` is its only executable output. The legacy AutoHotkey files stay in the parent repository.
- Default bindings live in `KeyBindings.cs` and are checked at startup against a fixed count of 121 entries: 120 CapsLock combinations plus the CapsLock tap.
- Clipboard and all window-management features remain out of scope.
