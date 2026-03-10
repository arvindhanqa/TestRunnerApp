using System;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

// ─────────────────────────────────────────────────────────────────────────────
// Add to TestRunnerApp alongside BreakpointTab.xaml.
//
// Wire up in MainWindow.xaml:
//   <TabItem Header="🔴 Breakpoints"><local:BreakpointTab/></TabItem>
//
// Add to AppSettings.cs:
//   public string BreakpointSlnPath    { get; set; } = "";
//   public string BreakpointLastCsFile { get; set; } = "";
//
// The Checkpoint flow (triggered by CHECKPOINT.flag):
//   1. Detect flag written by BreakpointHelper.StopAndCheckpoint()
//   2. Wait for TestProject.exe to exit
//   3. Copy .bak to PostRunBackupFolder (respects BackupMode — treated as Failed)
//   4. Restore original DB via SQL
//   5. Launch next test in the runner's queue
// ─────────────────────────────────────────────────────────────────────────────

namespace TestRunnerApp
{
    public partial class BreakpointTab : UserControl
    {
        // ── State ─────────────────────────────────────────────────────────────
        private string _currentFilePath;
        private string _slnPath;
        private TestNode _selectedNode;
        private FileSystemWatcher _flagWatcher;
        private bool _buildOutputVisible = false;
        private bool _isBuilding = false;

        // Shared flag folder — must match BreakpointHelper.cs
        private static readonly string FlagFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QATestRunner");

        private static string PausedFlagPath => Path.Combine(FlagFolder, "PAUSED.flag");
        private static string CheckpointFlagPath => Path.Combine(FlagFolder, "CHECKPOINT.flag");

        // ── Constructor ───────────────────────────────────────────────────────
        public BreakpointTab()
        {
            InitializeComponent();
            StartFlagWatcher();
            LoadSavedPaths();
        }

        // ══════════════════════════════════════════════════════════════════════
        // SETTINGS PERSISTENCE
        // ══════════════════════════════════════════════════════════════════════

        private void LoadSavedPaths()
        {
            try
            {
                var s = AppSettings.Load();
                if (!string.IsNullOrEmpty(s.BreakpointSlnPath))
                {
                    _slnPath = s.BreakpointSlnPath;
                    TxtSlnPath.Text = _slnPath;
                    BtnBuild.IsEnabled = true;
                }
                if (!string.IsNullOrEmpty(s.BreakpointLastCsFile)
                    && File.Exists(s.BreakpointLastCsFile))
                {
                    _currentFilePath = s.BreakpointLastCsFile;
                    TxtFilePath.Text = _currentFilePath;
                    LoadFile(_currentFilePath);
                }
            }
            catch { }
        }

        private void SavePaths()
        {
            try
            {
                var s = AppSettings.Load();
                s.BreakpointSlnPath = _slnPath ?? "";
                s.BreakpointLastCsFile = _currentFilePath ?? "";
                s.Save();
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        // FILE / SOLUTION PICKERS
        // ══════════════════════════════════════════════════════════════════════

        private void BtnBrowseSln_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select TestProject Solution or Project",
                Filter = "Build Files (*.sln;*.csproj)|*.sln;*.csproj|All Files|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            _slnPath = dlg.FileName;
            TxtSlnPath.Text = _slnPath;
            BtnBuild.IsEnabled = true;
            SavePaths();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select a Test C# File",
                Filter = "C# Files (*.cs)|*.cs|All Files|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            _currentFilePath = dlg.FileName;
            TxtFilePath.Text = _currentFilePath;
            LoadFile(_currentFilePath);
            SavePaths();
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentFilePath))
                LoadFile(_currentFilePath);
        }

        // ══════════════════════════════════════════════════════════════════════
        // TREE POPULATION
        // ══════════════════════════════════════════════════════════════════════

        private void LoadFile(string filePath)
        {
            try
            {
                var nodes = TestFileParser.Parse(filePath);
                TestTree.ItemsSource = nodes;

                bool hasBp = TestFileParser.HasAnyBreakpoint(filePath);
                UpdateBreakpointStatus(hasBp, hasBp ? SelectedMode : BreakpointMode.Pause);

                BtnClearAll.IsEnabled = true;
                BtnSetBreakpoint.IsEnabled = false;
                _selectedNode = null;

                TxtNodeHint.Text = nodes.Count == 0
                    ? "No TestCaseGroup / TestStepGroup blocks found."
                    : "Select a step or case, then set a breakpoint";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to parse file:\n{ex.Message}",
                    "Parse Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TestTree_SelectedItemChanged(object sender,
            RoutedPropertyChangedEventArgs<object> e)
        {
            _selectedNode = e.NewValue as TestNode;
            BtnSetBreakpoint.IsEnabled = _selectedNode != null;
            if (_selectedNode != null)
                TxtNodeHint.Text = $"Selected: \"{_selectedNode.Name}\"";
        }

        // ══════════════════════════════════════════════════════════════════════
        // BREAKPOINT INJECTION
        // ══════════════════════════════════════════════════════════════════════

        private BreakpointMode SelectedMode =>
            RboCheckpoint.IsChecked == true
                ? BreakpointMode.Checkpoint
                : BreakpointMode.Pause;

        private void BtnSetBreakpoint_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedNode == null || string.IsNullOrEmpty(_currentFilePath)) return;

            var mode = SelectedMode;
            try
            {
                TestFileParser.SetBreakpoint(_currentFilePath, _selectedNode, mode);
                LoadFile(_currentFilePath);
                UpdateBreakpointStatus(true, mode);

                if (!_buildOutputVisible) ToggleBuildOutput();

                string modeLabel = mode == BreakpointMode.Checkpoint ? "🔖 Checkpoint" : "⏸ Pause";
                AppendBuildLine($"[Breakpoint] {modeLabel} set at: \"{_selectedNode.Name}\"");

                if (mode == BreakpointMode.Checkpoint)
                {
                    var cfg = AppSettings.Load();
                    AppendBuildLine($"[Checkpoint] BackupMode : {cfg.BackupMode}");
                    AppendBuildLine($"[Checkpoint] Backup src : {cfg.BackupPath}");
                    AppendBuildLine($"[Checkpoint] Backup dst : {cfg.PostRunBackupFolder}");
                    AppendBuildLine($"[Checkpoint] DB restore : {cfg.DbServer} / {cfg.DbName}");
                }

                AppendBuildLine("[Breakpoint] Rebuild TestProject.exe — click 🔨 Build");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to set breakpoint:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;
            if (MessageBox.Show("Remove all injected breakpoints?", "Clear Breakpoints",
                    MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            try
            {
                TestFileParser.ClearAllBreakpoints(_currentFilePath);
                LoadFile(_currentFilePath);
                UpdateBreakpointStatus(false, BreakpointMode.Pause);
                AppendBuildLine("[Breakpoint] All breakpoints cleared.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear breakpoints:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateBreakpointStatus(bool hasBreakpoint, BreakpointMode mode)
        {
            if (!hasBreakpoint)
            {
                TxtBreakpointStatus.Text = "No breakpoint set";
                TxtBreakpointStatus.Foreground = Brushes.DimGray;
                BtnSetBreakpoint.Content = "Set Breakpoint";
                return;
            }

            if (mode == BreakpointMode.Checkpoint)
            {
                TxtBreakpointStatus.Text = "🔖 Checkpoint active — rebuild before running";
                TxtBreakpointStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 123, 167));
                BtnSetBreakpoint.Content = "🔖 Set Checkpoint";
            }
            else
            {
                TxtBreakpointStatus.Text = "🔴 Pause breakpoint active — rebuild before running";
                TxtBreakpointStatus.Foreground = Brushes.Crimson;
                BtnSetBreakpoint.Content = "🔴 Set Pause";
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // FLAG WATCHER — detects both PAUSED.flag and CHECKPOINT.flag
        // ══════════════════════════════════════════════════════════════════════

        private void StartFlagWatcher()
        {
            try
            {
                if (!Directory.Exists(FlagFolder))
                    Directory.CreateDirectory(FlagFolder);

                _flagWatcher = new FileSystemWatcher(FlagFolder, "*.flag")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                _flagWatcher.Created += OnFlagCreated;
                _flagWatcher.Deleted += OnFlagDeleted;

                // Handle app opened while already paused
                if (File.Exists(PausedFlagPath))
                    ShowPauseBanner(File.ReadAllText(PausedFlagPath));
            }
            catch { }
        }

        private void OnFlagCreated(object sender, FileSystemEventArgs e)
        {
            string label = string.Empty;
            try { label = File.ReadAllText(e.FullPath).Split('\n')[0].Trim(); } catch { }

            if (e.Name.Equals("PAUSED.flag", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() => ShowPauseBanner(label));
            }
            else if (e.Name.Equals("CHECKPOINT.flag", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() => _ = RunCheckpointSequenceAsync(label));
            }
        }

        private void OnFlagDeleted(object sender, FileSystemEventArgs e)
        {
            if (e.Name.Equals("PAUSED.flag", StringComparison.OrdinalIgnoreCase))
                Dispatcher.Invoke(HidePauseBanner);
        }

        // ══════════════════════════════════════════════════════════════════════
        // PAUSE / RESUME
        // ══════════════════════════════════════════════════════════════════════

        private void ShowPauseBanner(string label)
        {
            RunPausedAt.Text = string.IsNullOrWhiteSpace(label) ? "(unknown)" : label;
            PauseBanner.Visibility = Visibility.Visible;
        }

        private void HidePauseBanner() =>
            PauseBanner.Visibility = Visibility.Collapsed;

        private void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(PausedFlagPath)) File.Delete(PausedFlagPath);
                HidePauseBanner();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to resume:\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // CHECKPOINT SEQUENCE
        // ══════════════════════════════════════════════════════════════════════

        private async Task RunCheckpointSequenceAsync(string stepLabel)
        {
            var cfg = AppSettings.Load();

            ShowCheckpointBanner(stepLabel, "Initialising checkpoint…");
            if (!_buildOutputVisible) ToggleBuildOutput();

            AppendBuildLine(string.Empty);
            AppendBuildLine($"══════ CHECKPOINT at: {stepLabel} — {DateTime.Now:HH:mm:ss} ══════");

            try
            {
                // ── Step 1: Wait for TestProject.exe to fully exit ─────────────
                SetCheckpointActivity("Waiting for test process to exit…");
                await WaitForTestProcessToExitAsync();
                AppendBuildLine("  [1/4] Test process exited.");

                // ── Step 2: Copy .bak → PostRunBackupFolder (if BackupMode allows) ──
                SetCheckpointActivity("Saving database backup…");
                bool backupDone = await Task.Run(() =>
                {
                    bool ok = TryCopyBackup(cfg, stepLabel, out string backupMsg);
                    Dispatcher.Invoke(() => AppendBuildLine($"  [2/4] {backupMsg}"));
                    return ok;
                });

                // ── Step 3: Restore original DB ───────────────────────────────
                SetCheckpointActivity("Restoring database…");
                bool restored = await Task.Run(() =>
                {
                    bool ok = TryRestoreDb(cfg, out string restoreMsg);
                    Dispatcher.Invoke(() => AppendBuildLine($"  [3/4] {(ok ? "DB restored." : $"DB restore FAILED — {restoreMsg}")}"));
                    return ok;
                });
                if (!restored)
                {
                    HideCheckpointBanner();
                    MessageBox.Show(
                        "Database restore failed.\nPlease restore manually before running the next test.",
                        "Checkpoint Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // ── Step 4: Clean up flag + notify MainWindow to run next test ──
                SetCheckpointActivity("Launching next test…");
                try { File.Delete(CheckpointFlagPath); } catch { }

                AppendBuildLine("  [4/4] Signalling runner to start next test…");
                AppendBuildLine("══════ Checkpoint complete ══════");

                // Fire the event that MainWindow listens to in order to advance the queue
                CheckpointCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppendBuildLine($"  [ERROR] Checkpoint sequence failed: {ex.Message}");
                MessageBox.Show($"Checkpoint sequence error:\n{ex.Message}",
                    "Checkpoint Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideCheckpointBanner();
            }
        }

        /// <summary>
        /// Raised when the full checkpoint sequence completes successfully.
        /// MainWindow subscribes to this to advance the test queue.
        /// </summary>
        public event EventHandler CheckpointCompleted;

        // ── Checkpoint sub-steps ──────────────────────────────────────────────

        private static async Task WaitForTestProcessToExitAsync()
        {
            // Give the process up to 30 s to exit after writing the flag
            for (int i = 0; i < 60; i++)
            {
                var procs = Process.GetProcessesByName("TestProject");
                if (procs.Length == 0) return;
                await Task.Delay(500);
            }
            // If still running after 30 s, kill it
            foreach (var p in Process.GetProcessesByName("TestProject"))
                try { p.Kill(); } catch { }
        }

        private static bool TryCopyBackup(AppSettings cfg, string stepLabel, out string message)
        {
            // Checkpoint always treats the run as "failed" for BackupMode purposes
            if (cfg.BackupMode == BackupMode.Never)
            {
                message = "Backup skipped (BackupMode = Never).";
                return false;
            }

            if (string.IsNullOrEmpty(cfg.BackupPath) || !File.Exists(cfg.BackupPath))
            {
                message = $"Backup skipped — source file not found: {cfg.BackupPath}";
                return false;
            }

            try
            {
                if (!Directory.Exists(cfg.PostRunBackupFolder))
                    Directory.CreateDirectory(cfg.PostRunBackupFolder);

                // Sanitise step label for use in filename
                string safeName = SanitiseFileName(stepLabel);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string baseName = Path.GetFileNameWithoutExtension(cfg.BackupPath);
                string destFile = Path.Combine(cfg.PostRunBackupFolder,
                    $"{baseName}_CHECKPOINT_{safeName}_{ts}.bak");

                File.Copy(cfg.BackupPath, destFile, overwrite: true);
                message = $"Backup saved → {destFile}";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Backup copy failed: {ex.Message}";
                return false;
            }
        }

        private static bool TryRestoreDb(AppSettings cfg, out string message)
        {
            if (string.IsNullOrEmpty(cfg.BackupPath) || !File.Exists(cfg.BackupPath))
            {
                message = $"Restore skipped — backup file not found: {cfg.BackupPath}";
                return false;
            }

            try
            {
                // Build connection string for master DB (needed to restore)
                string connStr = $"Server={cfg.DbServer};Database=master;" +
                                 $"User Id={MasterDbUser(cfg)};Password={MasterDbPass(cfg)};" +
                                 "Connect Timeout=30;";

                string sql = $@"
                    ALTER DATABASE [{cfg.DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    RESTORE DATABASE [{cfg.DbName}]
                        FROM DISK = N'{cfg.BackupPath}'
                        WITH REPLACE, RECOVERY;
                    ALTER DATABASE [{cfg.DbName}] SET MULTI_USER;";

                using (var conn = new SqlConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(sql, conn)
                    { CommandTimeout = 300 })
                        cmd.ExecuteNonQuery();
                }

                message = $"DB [{cfg.DbName}] restored from {Path.GetFileName(cfg.BackupPath)}.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"Restore failed: {ex.Message}";
                return false;
            }
        }

        // SQL auth helpers — extend AppSettings with SqlUser/SqlPassword if needed,
        // or switch to Integrated Security by changing the connection string below.
        private static string MasterDbUser(AppSettings cfg) => "sa";
        private static string MasterDbPass(AppSettings cfg) => cfg.Password;

        private static string SanitiseFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 40 ? name.Substring(0, 40) : name;
        }

        // ── Checkpoint banner helpers ─────────────────────────────────────────

        private void ShowCheckpointBanner(string stepLabel, string activity)
        {
            TxtCheckpointStep.Text = $"Checkpoint: {stepLabel}";
            TxtCheckpointActivity.Text = activity;
            CheckpointBanner.Visibility = Visibility.Visible;
            CheckpointProgress.Visibility = Visibility.Visible;
        }

        private void SetCheckpointActivity(string activity) =>
            TxtCheckpointActivity.Text = activity;

        private void HideCheckpointBanner()
        {
            CheckpointBanner.Visibility = Visibility.Collapsed;
            CheckpointProgress.Visibility = Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════════════
        // MSBUILD
        // ══════════════════════════════════════════════════════════════════════

        private async void BtnBuild_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_slnPath) || _isBuilding) return;
            if (!_buildOutputVisible) ToggleBuildOutput();

            _isBuilding = true;
            BtnBuild.IsEnabled = false;
            BuildProgress.Visibility = Visibility.Visible;
            SetBuildStatus("⏳ Building…", "#856404");

            AppendBuildLine(string.Empty);
            AppendBuildLine($"══════ Build started {DateTime.Now:HH:mm:ss} ══════");
            AppendBuildLine($"  {Path.GetFileName(_slnPath)}  [{SelectedConfiguration}]");
            AppendBuildLine(string.Empty);

            bool success = await RunMsBuildAsync(_slnPath, SelectedConfiguration);

            BuildProgress.Visibility = Visibility.Collapsed;
            _isBuilding = false;
            BtnBuild.IsEnabled = true;

            AppendBuildLine(string.Empty);
            if (success)
            {
                AppendBuildLine("══════ Build SUCCEEDED ══════");
                SetBuildStatus("✅ Build succeeded", "#155724");
            }
            else
            {
                AppendBuildLine("══════ Build FAILED ══════");
                SetBuildStatus("❌ Build failed — see output", "#721C24");
            }
        }

        private string SelectedConfiguration =>
            (CmbConfiguration.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Debug";

        private async Task<bool> RunMsBuildAsync(string projectPath, string configuration)
        {
            string msBuildPath = FindMsBuild();
            if (msBuildPath == null)
            {
                AppendBuildLine("ERROR: Could not locate msbuild.exe.");
                AppendBuildLine("Install Visual Studio / Build Tools or add msbuild to PATH.");
                return false;
            }
            AppendBuildLine($"  msbuild: {msBuildPath}");
            AppendBuildLine(string.Empty);

            var psi = new ProcessStartInfo
            {
                FileName = msBuildPath,
                Arguments = $"\"{projectPath}\" /p:Configuration={configuration} /v:minimal /nologo",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            return await Task.Run(() =>
            {
                try
                {
                    using (var proc = new Process { StartInfo = psi })
                    {
                        proc.OutputDataReceived += (s, ev) =>
                        {
                            if (ev.Data != null)
                                Dispatcher.Invoke(() => AppendBuildLine(ev.Data));
                        };
                        proc.ErrorDataReceived += (s, ev) =>
                        {
                            if (ev.Data != null)
                                Dispatcher.Invoke(() => AppendBuildLine("ERR: " + ev.Data));
                        };
                        proc.Start();
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();
                        proc.WaitForExit();
                        return proc.ExitCode == 0;
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                        AppendBuildLine($"ERROR launching msbuild: {ex.Message}"));
                    return false;
                }
            });
        }

        private static string FindMsBuild()
        {
            var fromPath = FindInPath("msbuild.exe");
            if (fromPath != null) return fromPath;

            string[] roots = {
                @"C:\Program Files\Microsoft Visual Studio\2022",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2022",
                @"C:\Program Files\Microsoft Visual Studio\2019",
                @"C:\Program Files (x86)\Microsoft Visual Studio\2019",
            };
            string[] editions = { "Enterprise", "Professional", "Community", "BuildTools" };

            foreach (var r in roots)
                foreach (var ed in editions)
                {
                    var c = Path.Combine(r, ed, @"MSBuild\Current\Bin\MSBuild.exe");
                    if (File.Exists(c)) return c;
                }

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"MSBuild\14.0\Bin\MSBuild.exe");
            return File.Exists(legacy) ? legacy : null;
        }

        private static string FindInPath(string exe)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { var f = Path.Combine(dir.Trim(), exe); if (File.Exists(f)) return f; }
                catch { }
            return null;
        }

        private void AppendBuildLine(string text)
        {
            TxtBuildOutput.AppendText(text + Environment.NewLine);
            TxtBuildOutput.ScrollToEnd();
        }

        private void SetBuildStatus(string text, string hexColor)
        {
            TxtBuildStatus.Text = text;
            TxtBuildStatus.Foreground =
                (SolidColorBrush)new BrushConverter().ConvertFromString(hexColor);
        }

        private void BtnClearBuildOutput_Click(object sender, RoutedEventArgs e) =>
            TxtBuildOutput.Clear();

        private void BtnToggleBuildOutput_Click(object sender, RoutedEventArgs e) =>
            ToggleBuildOutput();

        private void ToggleBuildOutput()
        {
            _buildOutputVisible = !_buildOutputVisible;
            var outerGrid = ((GroupBox)((Grid)TxtBuildOutput.Parent).Parent).Parent as Grid;
            if (outerGrid != null)
                outerGrid.RowDefinitions[2].Height = _buildOutputVisible
                    ? new GridLength(200) : new GridLength(0);
            BtnToggleBuildOutput.Content = _buildOutputVisible ? "▲ Output" : "▼ Output";
        }

        // ══════════════════════════════════════════════════════════════════════
        // EXPAND / COLLAPSE
        // ══════════════════════════════════════════════════════════════════════

        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
            => SetAllExpanded(TestTree, true);
        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e)
            => SetAllExpanded(TestTree, false);

        private void SetAllExpanded(ItemsControl items, bool expanded)
        {
            foreach (var item in items.Items)
            {
                var c = items.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (c == null) continue;
                c.IsExpanded = expanded;
                SetAllExpanded(c, expanded);
            }
        }
    }
}