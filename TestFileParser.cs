using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// ─────────────────────────────────────────────────────────────────────────────
// Add this file to TestRunnerApp.
// Parses a C# test file into a tree of TestCaseGroup / TestStepGroup nodes,
// then injects or removes BreakpointHelper calls in-place.
//
// Two breakpoint modes:
//   BreakpointMode.Pause      → injects WaitForResume()
//   BreakpointMode.Checkpoint → injects StopAndCheckpoint()
// ─────────────────────────────────────────────────────────────────────────────

namespace TestRunnerApp
{
    public enum TestNodeType { TestCase, TestStep }
    public enum BreakpointMode { Pause, Checkpoint }

    public class TestNode
    {
        public string Name { get; set; }
        public TestNodeType Type { get; set; }
        public int BraceLineIndex { get; set; }  // 0-based
        public bool HasBreakpoint { get; set; }
        public BreakpointMode BreakpointMode { get; set; }
        public List<TestNode> Children { get; set; } = new List<TestNode>();

        public string Icon => Type == TestNodeType.TestCase ? "📋" : "🔹";

        public string BpIndicator => HasBreakpoint
            ? (BreakpointMode == BreakpointMode.Checkpoint ? " 🔖" : " 🔴")
            : string.Empty;

        public string DisplayName => $"{Icon} {Name}{BpIndicator}";
    }

    public static class TestFileParser
    {
        // ── Regex patterns ────────────────────────────────────────────────────
        private static readonly Regex CaseRegex =
            new Regex(@"CreateTestCaseGroup\s*\(\s*""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex StepRegex =
            new Regex(@"CreateTestStepGroup\s*\(\s*""([^""]+)""", RegexOptions.Compiled);

        // Marker appended to every injected line — used to find + remove them
        public const string BreakpointMarker = "// 🔴 BREAKPOINT - TestRunner";

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Parses the file and returns a tree of TestCase/TestStep nodes.</summary>
        public static List<TestNode> Parse(string filePath)
        {
            var lines = File.ReadAllLines(filePath);
            var roots = new List<TestNode>();
            var stack = new Stack<(TestNode node, int openDepth)>();
            int depth = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                int net = CountNetBraces(raw);

                if (net < 0)
                {
                    depth += net;
                    while (stack.Count > 0 && depth < stack.Peek().openDepth)
                        stack.Pop();
                    continue;
                }

                TestNode node = null;
                var cm = CaseRegex.Match(raw);
                var sm = StepRegex.Match(raw);

                if (cm.Success)
                    node = new TestNode { Name = cm.Groups[1].Value, Type = TestNodeType.TestCase };
                else if (sm.Success)
                    node = new TestNode { Name = sm.Groups[1].Value, Type = TestNodeType.TestStep };

                depth += net;

                if (node != null)
                {
                    int braceIdx = FindOpeningBrace(lines, i);
                    if (braceIdx < 0) braceIdx = i; // fallback to node line itself
                    node.BraceLineIndex = braceIdx;
                    node.HasBreakpoint = CheckHasBreakpoint(lines, braceIdx, out var mode);
                    node.BreakpointMode = mode;

                    if (stack.Count == 0) roots.Add(node);
                    else stack.Peek().node.Children.Add(node);

                    stack.Push((node, depth - 1));
                }
            }

            return roots;
        }

        /// <summary>
        /// Injects the appropriate BreakpointHelper call immediately after
        /// the opening '{' of the target node.
        /// Any existing breakpoint in the file is removed first
        /// (only one active breakpoint at a time).
        /// </summary>
        public static void SetBreakpoint(string filePath, TestNode node, BreakpointMode mode)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node.Name))
                throw new InvalidOperationException("Selected node has no name.");

            var lines = ReadLines(filePath, out var encoding, out var lineEnding, out var trailingNl);

            // Remove any existing injected breakpoint lines
            lines.RemoveAll(l => l != null && l.Contains(BreakpointMarker));

            // Locate the line containing the using(...CreateTestXxxGroup("name")...)
            int nodeLine = FindNodeLine(lines, node);
            if (nodeLine < 0)
                throw new InvalidOperationException(
                    $"Could not find \"{node.Name}\" in the file.\n" +
                    $"Make sure the file is saved and the correct node is selected.");

            // Find the opening brace of that using block
            int braceIdx = FindOpeningBrace(lines.ToArray(), nodeLine);
            if (braceIdx < 0 || braceIdx >= lines.Count)
                throw new InvalidOperationException(
                    $"Could not find opening '{{' after \"{node.Name}\".");

            string braceLine = lines[braceIdx];
            if (braceLine == null)
                throw new InvalidOperationException(
                    $"Unexpected null line at index {braceIdx}.");

            string i1 = GetIndent(braceLine) + "    ";  // one level in from the brace
            string i2 = i1 + "    ";                    // two levels in (loop body)
            string m = BreakpointMarker;               // shorthand
            string label = node.Name.Replace("\"", "'").Replace("\\", "/"); // safe for string literal

            // Each injected line carries the marker comment so RemoveAll cleans them all up.
            // Multi-line format satisfies StyleCop SA1107 (one statement per line)
            // and SA1312 (no double-underscore variable prefixes).
            List<string> injection;
            if (mode == BreakpointMode.Pause)
            {
                // Writes PAUSED.flag, spins until runner deletes it, then continues.
                injection = new List<string>
                {
                    $"{i1}{{ {m} [{label}]",
                    $"{i2}string bpFlagPath = System.IO.Path.Combine( {m}",
                    $"{i2}    System.Environment.GetFolderPath( {m}",
                    $"{i2}        System.Environment.SpecialFolder.LocalApplicationData), {m}",
                    $"{i2}    \"QATestRunner\", {m}",
                    $"{i2}    \"PAUSED.flag\"); {m}",
                    $"{i2}System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bpFlagPath)); {m}",
                    $"{i2}System.IO.File.WriteAllText(bpFlagPath, \"{label}\"); {m}",
                    $"{i2}while (System.IO.File.Exists(bpFlagPath)) {m}",
                    $"{i2}{{ {m}",
                    $"{i2}    System.Threading.Thread.Sleep(300); {m}",
                    $"{i2}}} {m}",
                    $"{i1}}} {m}",
                };
            }
            else
            {
                // Writes CHECKPOINT.flag then throws to abort the test cleanly.
                // Pragma suppresses CS0162 (unreachable code) for the rest of the
                // using block. Clear All removes both pragma and block together.
                injection = new List<string>
                {
                    $"#pragma warning disable CS0162, SA1117 {m} [{label}]",
                    $"{i1}{{ {m}",
                    $"{i2}string cpFlagPath = System.IO.Path.Combine( {m}",
                    $"{i2}    System.Environment.GetFolderPath( {m}",
                    $"{i2}        System.Environment.SpecialFolder.LocalApplicationData), {m}",
                    $"{i2}    \"QATestRunner\", {m}",
                    $"{i2}    \"CHECKPOINT.flag\"); {m}",
                    $"{i2}System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cpFlagPath)); {m}",
                    $"{i2}System.IO.File.WriteAllText(cpFlagPath, \"{label}\"); {m}",
                    $"{i2}throw new System.Exception(\"[CHECKPOINT] Stopped at: {label}\"); {m}",
                    $"{i1}}} {m}",
                };
            }

            // Insert all lines after the opening brace (in reverse so indices stay correct)
            for (int idx = injection.Count - 1; idx >= 0; idx--)
                lines.Insert(braceIdx + 1, injection[idx]);

            WriteLines(filePath, lines, encoding, lineEnding, trailingNl);
        }

        /// <summary>Removes all injected breakpoint lines from the file.</summary>
        public static void ClearAllBreakpoints(string filePath)
        {
            var lines = ReadLines(filePath, out var encoding, out var lineEnding, out var trailingNl);
            int removed = lines.RemoveAll(l => l.Contains(BreakpointMarker));
            if (removed > 0)
                WriteLines(filePath, lines, encoding, lineEnding, trailingNl);
        }

        /// <summary>Returns true if the file currently contains any injected breakpoints.</summary>
        public static bool HasAnyBreakpoint(string filePath)
        {
            foreach (var line in File.ReadLines(filePath))
                if (line.Contains(BreakpointMarker))
                    return true;
            return false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int CountNetBraces(string line)
        {
            bool inString = false;
            int net = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (c == '{') net++;
                else if (c == '}') net--;
            }
            return net;
        }

        private static int FindOpeningBrace(string[] lines, int fromLine)
        {
            if (fromLine < 0) return -1;
            for (int i = fromLine; i < Math.Min(fromLine + 6, lines.Length); i++)
                if (lines[i] != null && lines[i].Contains("{")) return i;
            return -1;
        }

        private static bool CheckHasBreakpoint(string[] lines, int braceIdx,
            out BreakpointMode mode)
        {
            mode = BreakpointMode.Pause;
            if (braceIdx < 0) return false;
            for (int i = braceIdx + 1; i < Math.Min(braceIdx + 5, lines.Length); i++)
            {
                if (lines[i] == null || !lines[i].Contains(BreakpointMarker)) continue;
                mode = lines[i].Contains("StopAndCheckpoint")
                    ? BreakpointMode.Checkpoint
                    : BreakpointMode.Pause;
                return true;
            }
            return false;
        }

        private static int FindNodeLine(List<string> lines, TestNode node)
        {
            string key = node.Type == TestNodeType.TestCase
                ? $"CreateTestCaseGroup(\"{node.Name}\""
                : $"CreateTestStepGroup(\"{node.Name}\"";

            for (int i = 0; i < lines.Count; i++)
                if (lines[i] != null && lines[i].Contains(key)) return i;
            return -1;  // not found — caller must check
        }

        private static string GetIndent(string line) =>
            line.Substring(0, line.Length - line.TrimStart().Length);

        // ── Encoding-preserving I/O ───────────────────────────────────────────
        // File.WriteAllLines always emits UTF-8 BOM + CRLF + trailing newline.
        // If the source file uses UTF-8 no-BOM or LF endings, git sees a diff
        // even when the content is logically identical.  These helpers detect
        // the original encoding/line-ending style and write back exactly.

        private static Encoding DetectEncoding(string filePath)
        {
            byte[] bom = new byte[4];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                fs.Read(bom, 0, Math.Min(4, (int)fs.Length));

            if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new UTF8Encoding(true);   // UTF-8 with BOM
            if (bom[0] == 0xFF && bom[1] == 0xFE)
                return Encoding.Unicode;          // UTF-16 LE
            if (bom[0] == 0xFE && bom[1] == 0xFF)
                return Encoding.BigEndianUnicode; // UTF-16 BE

            return new UTF8Encoding(false);       // UTF-8 without BOM (most .cs files)
        }

        private static string DetectLineEnding(string filePath)
        {
            using (var sr = new StreamReader(filePath))
            {
                int ch;
                while ((ch = sr.Read()) >= 0)
                {
                    if (ch == '\r') return "\r\n"; // CRLF (or bare CR, but CRLF is overwhelmingly common)
                    if (ch == '\n') return "\n";    // LF
                }
            }
            return "\r\n"; // default
        }

        private static bool HasTrailingNewline(string filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                if (fs.Length == 0) return false;
                fs.Seek(-1, SeekOrigin.End);
                return fs.ReadByte() == '\n';
            }
        }

        /// <summary>Reads lines and captures the file's encoding metadata.</summary>
        private static List<string> ReadLines(string filePath,
            out Encoding encoding, out string lineEnding, out bool trailingNewline)
        {
            encoding = DetectEncoding(filePath);
            lineEnding = DetectLineEnding(filePath);
            trailingNewline = HasTrailingNewline(filePath);
            return new List<string>(File.ReadAllLines(filePath));
        }

        /// <summary>Writes lines back preserving the original encoding, line endings, and trailing newline.</summary>
        private static void WriteLines(string filePath, List<string> lines,
            Encoding encoding, string lineEnding, bool trailingNewline)
        {
            string content = string.Join(lineEnding, lines);
            if (trailingNewline) content += lineEnding;
            File.WriteAllText(filePath, content, encoding);
        }
    }
}