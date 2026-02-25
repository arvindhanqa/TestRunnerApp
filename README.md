# QA Test Runner - WPF Desktop Application

A Windows desktop tool for configuring, executing, and reviewing Acumatica QA test automation results.

## Features

### Configuration Tab
- Edit site URL, credentials, DB restore settings, paths, logging — all with browse buttons
- Toggle DB restore on/off per run

### Test Selection Tab
- **Load tests from TestProject.exe** via .NET reflection (with source file scan fallback)
- Filter, Select All, click-to-toggle test selection
- Execution queue with ordering

### Generated Output Tab
- Live preview of XML config and BAT file
- Copy to clipboard or Save to disk

### Test Results Tab ★ NEW
- **Load Runs** — scans `C:\share\result` for log files, lists them in a dropdown sorted by date
- **Structured Log Tree** — parses the log into a collapsible tree:
  - `Check` → `Testcase` → `Step` → `Operation` → `Screenshot`
  - Color-coded by type (purple=testcase, blue=step, green=screenshot, red=error)
- **Screenshot Viewer** — click any log entry to preview its screenshot
  - Auto-resolves UNC paths (`\\HOSTNAME\C$\share\pics\...`) to local paths
  - Falls back to configured screenshot folder
  - "Open" button to view full-size in default viewer
- **Summary Bar** — shows PASS/FAIL status, duration, step count, screenshot count
- **Expand/Collapse** controls for the log tree

## How to Build

Open `TestRunnerApp.csproj` in Visual Studio 2019/2022 and build (Ctrl+Shift+B).
Or via command line: `msbuild TestRunnerApp.csproj /p:Configuration=Release`

## Requirements
- .NET Framework 4.8
- Windows 10/11

## Log Format Support
Parses your existing log format:
```
HH:MM:SS.mmm Type : Message
```
With tab-based indentation for hierarchy. Recognizes: Check, Testcase, Step, Operation, Screenshot, Information, Error (including stack traces).
