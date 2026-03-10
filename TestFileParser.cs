using System;
using System.Collections.Generic;
using System.IO;
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
    public enum TestNodeType    { TestCase, TestStep }
    public enum BreakpointMode  { Pause, Checkpoint }

    public class TestNode
    {
        public string         Name           { get; set; }
        public TestNodeType   Type           { get; set; }
        public int            BraceLineIndex { get; set; }  // 0-based
        public bool           HasBreakpoint  { get; set; }
        public BreakpointMode BreakpointMode { get; set; }
        public List<TestNode> Children       { get; set; } = new List<TestNode>();

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
                var raw  = lines[i];
                int net  = CountNetBraces(raw);

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
                    int braceIdx        = FindOpeningBrace(lines, i);
                    node.BraceLineIndex = braceIdx;
                    node.HasBreakpoint  = CheckHasBreakpoint(lines, braceIdx, out var mode);
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
            var lines = new List<string>(File.ReadAllLines(filePath));

            // Remove any existing injected breakpoint
            lines.RemoveAll(l => l.Contains(BreakpointMarker));

            // Re-locate the brace line after removal
            int braceIdx = FindOpeningBrace(lines.ToArray(), FindNodeLine(lines, node));
            if (braceIdx < 0)
                throw new InvalidOperationException(
                    $"Could not find opening '{{' for \"{node.Name}\".");

            string indent    = GetIndent(lines[braceIdx]) + "    ";
            string call      = mode == BreakpointMode.Checkpoint
                ? $"BreakpointHelper.StopAndCheckpoint(\"{node.Name}\")"
                : $"BreakpointHelper.WaitForResume(\"{node.Name}\")";
            string injection = $"{indent}{call}; {BreakpointMarker}";

            lines.Insert(braceIdx + 1, injection);
            File.WriteAllLines(filePath, lines);
        }

        /// <summary>Removes all injected breakpoint lines from the file.</summary>
        public static void ClearAllBreakpoints(string filePath)
        {
            var lines   = new List<string>(File.ReadAllLines(filePath));
            int removed = lines.RemoveAll(l => l.Contains(BreakpointMarker));
            if (removed > 0)
                File.WriteAllLines(filePath, lines);
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
            int  net      = 0;
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
            for (int i = fromLine; i < Math.Min(fromLine + 4, lines.Length); i++)
                if (lines[i].Contains("{")) return i;
            return fromLine;
        }

        private static bool CheckHasBreakpoint(string[] lines, int braceIdx,
            out BreakpointMode mode)
        {
            mode = BreakpointMode.Pause;
            for (int i = braceIdx + 1; i < Math.Min(braceIdx + 5, lines.Length); i++)
            {
                if (!lines[i].Contains(BreakpointMarker)) continue;
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
                if (lines[i].Contains(key)) return i;
            return 0;
        }

        private static string GetIndent(string line) =>
            line.Substring(0, line.Length - line.TrimStart().Length);
    }
}
