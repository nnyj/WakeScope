# WakeScope

Who's keeping your display awake? WakeScope finds out — every second.  
A system tray app that detects processes blocking Windows display sleep in real time.

[日本語版はこちら](README.md)

## Overview

Before Windows dims the display, it checks for active DISPLAY power requests.  
Browsers playing video and certain apps hold these requests indefinitely, preventing the screen from sleeping.  
WakeScope polls `powercfg /requests` every second and signals any culprits via tray icon color.

## Features

- Runs as a system tray icon with no visible window
- Monitors DISPLAY power requests every second
- Tray icon turns red when a blocker is detected
- Right-click menu shows process name, PID, and icon for each blocker
- Single-instance enforced via global Mutex

## Requirements

| Item | Requirement |
|------|-------------|
| OS | Windows 11 (x64) |
| Runtime | None (self-contained) |
| Privileges | **Administrator required** (needed to run `powercfg /requests`) |

## Installation

1. Download and extract the latest zip from the [Releases](https://github.com/130cmWolf/WakeScope/releases) page, or build from source
2. Right-click `WakeScope.exe` → **Run as administrator**

### Build from source

.NET 8 SDK is required.

```bash
git clone https://github.com/130cmWolf/WakeScope.git
cd WakeScope
dotnet publish -p:PublishProfile=Release
```

The output is placed at `publish\WakeScope.exe`.

## Usage

1. Run `WakeScope.exe` as administrator — a tray icon appears
2. Icon is green when no blockers are found; turns red when one or more are active
3. Right-click the tray icon to see the blocker list and the **Exit** option

## How it works

WakeScope parses `powercfg /requests` output every second and extracts `[PROCESS]` entries from the `DISPLAY:` section.  
Process icons are loaded from the executable path, and PIDs are resolved by matching the image path against running processes filtered by name.

```mermaid
flowchart TD
    A([Start]) --> B[Load tray icons and fallback icon]
    B --> C[Show tray icon]
    C --> D[Wait 1 second]
    D --> E[Run powercfg /requests]
    E --> F[Parse DISPLAY section]
    F --> G{Any PROCESS entries?}
    G -- No --> H[Icon: green]
    G -- Yes --> I[Icon: red]
    H --> D
    I --> D
    C --> J{{Right-click → Exit}}
    J --> K([Exit])
```

## Verification

While playing YouTube in Chrome, run the following and confirm it matches WakeScope's display:

```
powercfg /requests
DISPLAY:
[PROCESS] \Device\HarddiskVolume3\...\chrome.exe
Video Wake Lock
```

## License

MIT — [130cmWolf](https://github.com/130cmWolf/WakeScope)
