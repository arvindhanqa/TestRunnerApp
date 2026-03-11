# QA Test Runner — WPF Desktop Application

A Windows desktop tool for configuring, executing, and reviewing Acumatica QA test automation results.

## Features

### Configuration Tab
- Edit site URL, credentials, DB restore settings, paths, and logging — all with browse buttons
- Set backup mode: Always / Failed Only / Never
- Configure screenshot and archive folders

### Tests Tab
- **Load tests from DLL** via .NET reflection — scans for `Tests*.dll` / `Test*.dll` assemblies
- Filter by module or search by name
- Click-to-toggle test selection, Select All / None
- Auto-reloads when DLLs change on disk
- Inline status per test row (Not Run / Running / PASSED / FAILED / CHECKPOINT)
- Click 📄 to jump directly to that test's log in the Results tab
- Click 🔴 to open the test source file directly in the Breakpoints tab

### Results Tab
- **Load Runs** — scans configured log folder and archive folder, lists runs in a dropdown sorted by date
- **Structured Log Tree** — parses logs into a collapsible tree:
  - `Check` → `Testcase` → `Step` → `Operation` → `Screenshot`
  - Color-coded by type (purple=testcase, blue=step, green=screenshot, red=error)
- **Screenshot Viewer** — click any log entry to preview its screenshot
  - Auto-resolves UNC paths (`\\HOSTNAME\C$\share\pics\...`) to local paths
  - Falls back to configured screenshot folder
  - "Open" button to view full-size in default viewer
- **Summary Bar** — shows PASS/FAIL status and duration
- **Expand/Collapse** controls for the log tree

### Breakpoints Tab ★ NEW
Set pause or checkpoint breakpoints inside any test `.cs` file — no changes to the TestsManufacturing project required.

#### Two Modes
| Mode | Behaviour |
|------|-----------|
| ⏸ **Pause** | Injects a flag-file spin-wait. Test freezes at that step. Click **Resume** in the runner to continue. |
| 🔖 **Checkpoint** | Injects a flag + throw. Test stops, runner saves a DB backup, restores the original DB, and auto-starts the next test in the queue. |

#### Breakpoint Workflow
1. Browse to a test `.cs` file (or click 🔴 on any test row in the Tests tab)
2. The Test Structure tree shows all `TestCaseGroup` and `TestStepGroup` blocks
3. Select a node → choose Pause or Checkpoint mode → click **Set Breakpoint**
4. Click **🔨 Build** to rebuild TestProject with the injected code
5. Run the test — the runner detects the flag file and reacts accordingly
6. Click **✖ Clear All** when done — the file is restored exactly as it was

#### Checkpoint Sequence (automatic)
1. Waits for TestProject.exe to exit
2. Copies `.bak` → `PostRunBackupFolder\DbName_CHECKPOINT_StepName_timestamp.bak` (respects BackupMode)
3. Restores original DB via `sqlcmd`
4. Auto-starts the next test in the queue

#### Implementation Notes
- Breakpoints are injected as fully self-contained inline BCL code — no new files or references needed in TestsManufacturing
- Every injected line carries `// 🔴 BREAKPOINT - TestRunner` so **Clear All** removes them precisely
- The `.sln` path and last opened `.cs` file are persisted in AppSettings between sessions
- Nothing is ever committed to git — you control when to build and when to clear

## How to Build

Open `TestRunnerApp.sln` in Visual Studio 2019/2022 and build (Ctrl+Shift+B).  
Or via command line:
```
msbuild TestRunnerApp.sln /p:Configuration=Release
```

## Requirements
- .NET Framework 4.8
- Windows 10/11
- Visual Studio 2019/2022 (or MSBuild standalone) for the built-in Build button
- SQL Server with `sqlcmd` on PATH (for DB restore/backup)

## Log Format Support
Parses the existing Acumatica test log format:
```
HH:MM:SS.mmm Type : Message
```
With tab-based indentation for hierarchy.  
Recognises: `Check`, `Testcase`, `Step`, `Operation`, `Screenshot`, `Information`, `Error` (including stack traces).
