# WakeScope

<div align="center">

[![Stars](https://img.shields.io/github/stars/nnyj/WakeScope?style=for-the-badge&labelColor=555&color=e3b341)](https://github.com/nnyj/WakeScope/stargazers)
[![Downloads](https://img.shields.io/github/downloads/nnyj/WakeScope/total?style=for-the-badge&labelColor=555&color=2ea44f)](https://github.com/nnyj/WakeScope/releases)
[![Latest Release](https://img.shields.io/github/v/release/nnyj/WakeScope?style=for-the-badge&label=Latest%20Release&labelColor=555&color=3572d6)](https://github.com/nnyj/WakeScope/releases/latest)
[![Build](https://img.shields.io/github/actions/workflow/status/nnyj/WakeScope/release.yml?style=for-the-badge&labelColor=555)](https://github.com/nnyj/WakeScope/actions)

</div>

Windows system tray tool that identifies active power requests blocking display sleep or system sleep, with process details and a kill action.

## Features

- Polls every 2 s via native `PowerInformationWithPrivileges` API, no process spawn per tick
- Color-coded tray icon: gray (no blockers), orange (display-only blocker), red (any sleep blocker)
- Groups blockers by `Display` and `Sleep` category
- Shows process name, PID, reason string, COM class name where available
- Decodes PowerShell `-EncodedCommand` for readable command-line display
- Lists all matching processes when Windows reports a path without a PID
- Kill option for process-backed blockers from the tray menu
- Shows the active power plan sleep timeout and sets it from the tray menu via native `powrprof` API
- Detects legacy `SetThreadExecutionState` callers such as video players, not only `PowerCreateRequest` clients
- Reports driver and kernel blockers with device names and localized reason strings (cannot kill, Windows exposes no PID)
- Resolves the owning service name for blockers hosted in a shared `svchost.exe`
- Single instance enforced via global mutex
- Requires administrator, UAC prompt on launch

## Usage

Run `publish\WakeScope.exe` and accept the UAC prompt.

- Tray icon reflects current blocker state
- Left-click or right-click opens the blocker menu
- `Refresh` forces an immediate check
- `Kill process` terminates the selected process blocker
- `Exit` stops WakeScope

## Build

```powershell
dotnet publish -p:PublishProfile=Release
```

Output: `publish\WakeScope.exe`, self-contained single-file `win-x64` binary.

## Verification

Compare against the Windows built-in tool:

```powershell
powercfg /requests
```

Test blocking behavior with the included helper:

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\sleep_block_test.ps1
```

## How it works

WakeScope calls `PowerInformationWithPrivileges` at level 45, the same undocumented level `powercfg.exe` uses internally, to read the raw power request list from the kernel. Each entry carries per-category active counts and a diagnostic buffer naming the caller (process image path and PID, shared service tag, or device description and path) plus a reason that is either a plain string or a module and resource id resolved through `LoadStringW`. Native path strings are resolved via `NtQueryObject`; COM class names are looked up in the registry. The monitor runs on a background thread and posts state changes to the UI thread via `SynchronizationContext`.

> [!NOTE]
> Level 45 uses undocumented structure offsets, verified on build 26100 against controlled dumps of both `PowerCreateRequest` and `SetThreadExecutionState` blockers and cross-checked with `powercfg /requests`. A future Windows update could break parsing.

## Changes from upstream

- Native API (`PowerInformationWithPrivileges`) replaces `powercfg /requests` CLI parsing, removing process-spawn overhead on every poll
- `SYSTEM`, `AWAYMODE` and `EXECUTION` category tracking added (upstream tracked `DISPLAY` only)
- Kill action for process-backed blockers
- Compact command-line display with PowerShell `-EncodedCommand` decode
- Process candidate list when Windows reports a path without a PID
- COM class name resolution for COM-hosted blockers
- Sleep timeout submenu reading and writing the active power plan without spawning `powercfg`
- Sleep block test helper (`tests/sleep_block_test.ps1`)

## Credits

- [130cmWolf/WakeScope](https://github.com/130cmWolf/WakeScope): original display-sleep tray monitor this fork extends

## License

[MIT](LICENSE)
