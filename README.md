# WakeScope

Tray tool for finding Windows power requests that block display sleep or system sleep.

## Features

- Polls every `2s`
- Runs as administrator, required for power request APIs
- Shows tray state:
  - gray, no blockers
  - orange, display-only blocker
  - red, any sleep blocker
- Left-click or right-click opens blocker menu
- Groups blockers by `Display` and `Sleep`
- Shows process name, PID, reason, COM class name where available
- Shows matching processes when Windows reports path but not PID
- Shows compact command line and decodes PowerShell `-EncodedCommand`
- Can kill process blockers from menu
- Shows driver/kernel blockers, but cannot kill them because Windows exposes no PID
- Single instance via global mutex

## Install

1. Run `publish\WakeScope.exe`
2. Accept UAC prompt
3. Keep app in tray

## Build

```powershell
dotnet publish -p:PublishProfile=Release
```

Output:

```text
publish\WakeScope.exe
```

Current publish profile creates one self-contained `win-x64` exe.

## Usage

- Tray icon changes when blockers appear
- Open tray menu with left-click or right-click
- Use `Refresh` to force immediate check
- Use `Kill process` only when listed blocker is safe to terminate
- Use `Exit` to close WakeScope

## What It Detects

- `DISPLAY` requests, usually media playback or browser video
- `SYSTEM` requests, blocks automatic system sleep
- `EXECUTION` requests, usually process lifetime requests
- driver requests such as audio streams
- kernel requests such as `Legacy Kernel Caller`

## Limitations

- Driver and kernel requests do not expose a process PID
- `Legacy Kernel Caller` cannot be killed from WakeScope
- If multiple processes share same executable path, WakeScope lists matching PIDs instead of guessing
- Native parser uses undocumented Windows structure offsets, verified against `powercfg /requests`

## Verification

Run:

```powershell
powercfg /requests
```

WakeScope should show same active categories and blockers.

Test script:

```powershell
powershell -ExecutionPolicy Bypass -File C:\N\scripts\system\sleep_block_test.ps1 -Mode both
```

## Notes

- Based on upstream `130cmWolf/WakeScope`
- Upstream monitored display blockers only
- This fork also parses `powercfg /requests` categories and adds sleep blockers, process kill actions, and compact command-line display

## License

MIT, original project by [130cmWolf/WakeScope](https://github.com/130cmWolf/WakeScope)
