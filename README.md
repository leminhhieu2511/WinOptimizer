# WinOptimizer

A Windows 11 optimization tool built with C# / .NET 10 and WPF. It brings power modes, junk cleaning, startup apps, privacy tweaks, live system monitoring and per-monitor taskbar control together in one app.

## Features

| Page | What it does |
|---|---|
| **Home** | Overview: live CPU, memory, system drive and battery tiles, one-click power mode buttons, and shortcuts to every tool with a status line. |
| **System Monitor** | Live CPU and memory with 2-minute history graphs, battery, drive usage and the busiest processes. |
| **Power Mode** | Switches between **Battery Life Saver**, **Balanced** and **Highest Performance**, the same setting as *Settings › System › Power & battery › Power mode*. If the active power plan blocks power modes, it offers a one-click switch to the Balanced plan. |
| **Junk Cleaner** | Scans and cleans temp files, the Windows Update download cache, browser caches (Chrome, Edge, Brave, Firefox), shader and thumbnail caches, crash dumps and the Recycle Bin. Asks before deleting, skips files that are in use, and keeps temp files changed in the last 24 hours. |
| **Startup Apps** | Turns startup apps on or off the same way Task Manager does, so you can turn them back on any time. Can open each program's folder. |
| **Taskbar & Displays** | Per display: auto-hide Windows' own taskbar, and/or add WinOptimizer's **vertical taskbar** on the left or right. Also lists connected displays and controls auto-hide. |
| **Privacy & Tweaks** | 22 switches in four groups: Privacy, Ads & suggestions, Performance & gaming, and File Explorer. Each shows its current state; turning one off puts the Windows default back. |

### Vertical taskbar

On Windows builds that can't move their own taskbar to the side, WinOptimizer adds its own vertical taskbar, chosen per display (Off / Left side / Right side). It:

- shows the windows open on that display (click to switch, click again to minimize), plus a Start button
- registers as a docked side bar, so maximized windows don't cover it
- keeps buttons in a fixed order, like the real taskbar
- has a ⚡ button: click to open WinOptimizer, right-click to turn the bar off or exit

### Hide Windows' taskbar per display

Windows can only auto-hide its taskbar on every display at once. WinOptimizer can hide it on chosen displays only; the taskbar reappears when the mouse reaches its strip. Combined with the vertical taskbar, a display can show *only* the vertical taskbar.

## Requirements

- Windows 11 (developed and tested on 26H1, build 28000)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the .NET 10 Desktop Runtime to run

## Build and run

```bash
dotnet build -c Release
```

```bash
dotnet run -c Release
```

The built app is at `bin\Release\net10.0-windows\WinOptimizer.exe`.

## Command line

| Command | Effect |
|---|---|
| `WinOptimizer.exe saver` | Switch to Battery Life Saver and exit (no window) |
| `WinOptimizer.exe balanced` | Switch to Balanced and exit |
| `WinOptimizer.exe performance` | Switch to Highest Performance and exit |
| `WinOptimizer.exe --exit` | Close the running copy cleanly (taskbars are restored) |

Only one copy runs at a time; starting it again brings the existing window to the front.

## Administrator mode

The app runs without admin rights by default ("Limited mode"). Use **Restart as administrator** in the sidebar to unlock:

- cleaning Windows temporary files and the Windows Update download cache
- turning startup apps for all users on or off
- the telemetry and activity history tweaks

Everything else works without admin rights. A copy running as administrator can't be reached from a normal copy, so `--exit` from a normal prompt won't close it; close it from its window or the ⚡ menu.

## Background mode

While a vertical taskbar or per-display taskbar hiding is on, closing the window keeps WinOptimizer running so those keep working. To open it again, start WinOptimizer again or click ⚡ on a vertical taskbar. To quit, right-click ⚡ and choose **Exit**, or run `WinOptimizer.exe --exit`.

## Performance

WinOptimizer is built not to slow the PC down:

- Live updates pause when a page isn't visible or the window is minimized.
- Junk scans and cleans run in Windows' background CPU and disk priority.
- System Monitor refreshes CPU and memory every 2 s, the process list every 4 s and drives every 30 s.
- The Home page reads only cheap values and never starts a junk scan.

## Safety and recovery

- **Tweaks:** turning a switch off restores the Windows default.
- **Startup apps:** entries are never deleted, only marked on or off.
- **Junk Cleaner:** always asks before deleting; locked or protected files are skipped.
- **Hidden taskbars** are restored on exit, on a crash and when Windows signs out or shuts down. If the app is force-killed, the next start restores them.
- **Reset taskbars** (Taskbar & Displays page) shows every Windows taskbar again and turns off the vertical taskbars.
- If a taskbar is still missing, restart **Windows Explorer** from Task Manager.

## Known limitations

- **Windows 11 build 28000 (26H1):** Windows ignores the classic taskbar registry settings (alignment, combine buttons, search, Task View, multiple displays). On those builds the page hides them and links to Windows Settings. Builds from 27000 up are treated the same way, but only 28000 was tested.
- **Native taskbar position:** Windows 11 24H2/25H2 with KB5120998 (builds 26100/26200.9278+) and Insider 26H1 build 28020+ have their own *Taskbar position* option (bottom, top, left, right), rolling out gradually. WinOptimizer doesn't use it yet.
- **Hidden taskbar strip:** while Windows' taskbar is auto-hidden, its strip stays reserved, so maximized windows don't fill it.
- Removing a vertical taskbar can take Windows several seconds to give the screen space back.
- Some Microsoft Store apps may show a generic icon on the vertical taskbar.
- The vertical taskbar doesn't start with Windows; it runs while WinOptimizer runs.
- Restarting Explorer closes open File Explorer windows.

## Settings

Saved in `%LocalAppData%\WinOptimizer\settings.json` (vertical taskbars, hidden taskbars, and a safety flag used for crash recovery). Delete the file to start fresh.

## Project structure

```
App.xaml(.cs)          Startup, single instance, --exit, crash/sign-out restore, shared styles
MainWindow.xaml(.cs)   Sidebar navigation, admin restart, background mode
Pages/                 One page per feature (Home, Monitor, Power, Cleaner, Startup, Taskbar, Tweaks)
Services/              Windows logic: power, system info, junk cleaner, startup apps, tweaks,
                       taskbar settings, taskbar hider, window list, settings store, registry/Explorer helpers
SideTaskbar/           The vertical taskbar window and the manager that creates one per display
Helpers/               PageActivity (pause when hidden), LowPriority (background-priority work), formatting
```

## Credits

Per-display taskbar auto-hide uses the technique from [Per-Monitor Taskbar](https://github.com/nikoleinonen/per-monitor-taskbar) by Niko Leinonen (MIT License). WinOptimizer's implementation is an independent C# version of the same idea.
