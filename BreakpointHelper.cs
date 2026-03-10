using System;
using System.IO;
using System.Threading;

// ─────────────────────────────────────────────────────────────────────────────
// Place this file in your TestProject (same namespace as your test classes).
// Provides two breakpoint modes for the QA Test Runner WPF app:
//
//   WaitForResume()     — pauses execution, waits for Resume click in runner
//   StopAndCheckpoint() — aborts the test so runner can save DB + run next test
// ─────────────────────────────────────────────────────────────────────────────

namespace TestProject
{
    /// <summary>
    /// Thrown by StopAndCheckpoint() to abort the test cleanly.
    /// The test framework will catch this as an unhandled exception and exit.
    /// </summary>
    public class BreakpointCheckpointException : Exception
    {
        public BreakpointCheckpointException(string label)
            : base($"[CHECKPOINT] Test stopped at breakpoint: {label}") { }
    }

    public static class BreakpointHelper
    {
        // Must match FlagFolder in BreakpointTab.xaml.cs
        private static readonly string FlagFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QATestRunner");

        private static string PausedFlagPath     => Path.Combine(FlagFolder, "PAUSED.flag");
        private static string CheckpointFlagPath => Path.Combine(FlagFolder, "CHECKPOINT.flag");

        // ── MODE 1: Pause and wait for manual Resume ──────────────────────────

        /// <summary>
        /// Pauses test execution. The WPF runner shows a Resume button.
        /// Execution continues when the runner deletes PAUSED.flag.
        /// </summary>
        public static void WaitForResume(string breakpointLabel = "")
        {
            try
            {
                EnsureFolder();
                File.WriteAllText(PausedFlagPath, breakpointLabel);

                Console.WriteLine($"[BREAKPOINT] Paused at: {breakpointLabel}");
                Console.WriteLine("[BREAKPOINT] Waiting for Resume in QA Test Runner...");

                while (File.Exists(PausedFlagPath))
                    Thread.Sleep(300);

                Console.WriteLine("[BREAKPOINT] Resumed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BREAKPOINT] Error in WaitForResume: {ex.Message}");
            }
        }

        // ── MODE 2: Stop test, signal runner to save DB and run next test ─────

        /// <summary>
        /// Writes CHECKPOINT.flag so the runner saves the DB backup and starts
        /// the next test, then throws BreakpointCheckpointException to abort
        /// the current test immediately.
        ///
        /// The exception will propagate up through the test framework and
        /// terminate TestProject.exe — this is intentional.
        /// </summary>
        public static void StopAndCheckpoint(string breakpointLabel = "")
        {
            EnsureFolder();

            // Write flag BEFORE throwing — runner is watching for it
            File.WriteAllText(CheckpointFlagPath,
                $"{breakpointLabel}{Environment.NewLine}{DateTime.Now:O}");

            Console.WriteLine($"[CHECKPOINT] Stopping at: {breakpointLabel}");
            Console.WriteLine("[CHECKPOINT] Runner will save DB and start next test.");

            // Abort the test by throwing — intentionally not caught here
            throw new BreakpointCheckpointException(breakpointLabel);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void EnsureFolder()
        {
            if (!Directory.Exists(FlagFolder))
                Directory.CreateDirectory(FlagFolder);
        }
    }
}
