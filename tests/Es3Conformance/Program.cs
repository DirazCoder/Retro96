// Es3Conformance — Retro96 ES3 engine conformance runner for the Mozilla
// legacy JavaScript test suite (ecma, ecma_2, ecma_3, js1_1–js1_5).
//
// Port of the reference runner semantics (js/src/tests/jstests.py as of the
// June-2014 tree the corpus was snapshotted from):
//
//   • Harness assembly (lib/tests.py Test.prefix_command): for a test at
//     <suite>/<dir>/<file>.js the shell evaluates, in ONE shared global
//     scope: <root>/shell.js, <root>/<suite>/shell.js,
//     <root>/<suite>/<dir>/shell.js, then the test itself — exactly the
//     `js -f shell.js -f suite/shell.js -f dir/shell.js -f test.js` order.
//
//   • Classification (lib/results.py TestResult.from_output): stdout lines
//     beginning " PASSED!" / " FAILED!" are counted; the process exit code
//     (0 = ran to completion, 3 = uncaught exception) must be expected —
//     *-n.js negative tests expect 3, expectExitCode() notes add others —
//     and a test passes only if it reported at least one result (or exited
//     nonzero as expected) with zero failures.
//
//   • Shell surface (SpiderMonkey shell builtins the harness relies on):
//     print/version/gc/options/quit/load.  Nothing DOM-facing is defined,
//     so tests see typeof window === "undefined" (shell mode).
//
// Modes:
//   (default)            parent: sweep every test across worker processes
//   --child              worker: run tests streamed over stdin, report blocks
//                        "@RESULT <path> <CATEGORY>", "@OUT <line>"*,
//                        "@END" on stdout
//   --single <path>      run one test in-process, verbose (triage aid)
//   --list               print every discovered test path
//   --filter <substr>    (parent) restrict the sweep to paths containing substr
//   --jobs <n>           (parent) worker process count (default 2)
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Retro96.Engine.Js;

namespace Es3Conformance;

public static class Program
{
    // Harness basenames never treated as tests (manifest.py EXCLUDED set).
    private static readonly HashSet<string> HarnessBasenames = new(StringComparer.Ordinal)
    {
        "shell.js", "browser.js", "jsref.js", "template.js", "user.js", "sta.js",
        "test262-browser.js", "test262-shell.js", "test402-browser.js",
        "test402-shell.js", "testBuiltInObject.js", "testIntl.js",
        "js-test-driver-begin.js", "js-test-driver-end.js",
    };

    private static readonly string[] Suites = { "ecma", "ecma_2", "ecma_3", "js1_1", "js1_2", "js1_3", "js1_4", "js1_5" };

    private const int WallClockWatchdogMs = 120_000;   // generous: engine budgets 5s per file
    private const int SlowLaneLimitMs = 60_000;
    private const int MaxDetailLines = 40;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            if (args.Length > 0 && args[0] == "--child") return ChildMain();
            if (args.Length > 0 && args[0] == "--list") { foreach (var p in Corpus.ListTests()) Console.WriteLine(p); return 0; }
            if (args.Length > 0 && args[0] == "--single")
            {
                if (args.Length < 2) { Console.Error.WriteLine("usage: --single <relpath>"); return 2; }
                return SingleMain(args[1]);
            }
            return ParentMain(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Corpus
    // ─────────────────────────────────────────────────────────────────────

    private static class Corpus
    {
        public static string Root { get; } = FindRoot();

        private static string FindRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "tests", "es3-conformance");
                if (Directory.Exists(Path.Combine(candidate, "ecma")))
                    return candidate;
            }
            throw new DirectoryNotFoundException(
                $"tests/es3-conformance not found above {AppContext.BaseDirectory}");
        }

        /// <summary>Every test relpath (e.g. "ecma_2/RegExp/15.10.6.2.js"), sorted.</summary>
        public static List<string> ListTests()
        {
            var tests = new List<string>();
            foreach (string suite in Suites)
            {
                string dir = Path.Combine(Root, suite);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*.js", SearchOption.AllDirectories))
                {
                    if (HarnessBasenames.Contains(Path.GetFileName(file))) continue;
                    tests.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
                }
            }
            tests.Sort(StringComparer.Ordinal);
            return tests;
        }

        /// <summary>
        /// The -f chain for a test: root shell.js, then one shell.js per
        /// directory level, then the test file itself.
        /// </summary>
        public static List<string> HarnessChain(string relpath)
        {
            var chain = new List<string> { Path.Combine(Root, "shell.js") };
            string[] parts = relpath.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string dirShell = Path.Combine(Root, Path.Combine(parts[..(i + 1)]), "shell.js");
                if (File.Exists(dirShell)) chain.Add(dirShell);
            }
            chain.Add(Path.Combine(Root, relpath));
            return chain;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Skip list (tests that are not ES3-conformance material)
    // ─────────────────────────────────────────────────────────────────────

    private sealed class SkipList
    {
        private readonly List<(string Pattern, string Reason)> _rules = new();

        public static SkipList Load()
        {
            var list = new SkipList();
            string path = Path.Combine(Corpus.Root, "skip.json");
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
                list._rules.Add((prop.Name, prop.Value.GetString() ?? ""));
            return list;
        }

        public bool Match(string relpath, out string reason)
        {
            foreach (var (pattern, why) in _rules)
            {
                if (WildcardMatch(relpath, pattern))
                {
                    reason = why;
                    return true;
                }
            }
            reason = "";
            return false;
        }
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        // '*' matches any run of characters (including '/'), '?' one char.
        int ti = 0, pi = 0, star = -1, mark = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == text[ti])) { ti++; pi++; }
            else if (pi < pattern.Length && pattern[pi] == '*') { star = pi++; mark = ti; }
            else if (star >= 0) { pi = star + 1; ti = ++mark; }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Test executor — shared by --child and --single
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Signals quit(code) from script level.</summary>
    private sealed class QuitSignal : Exception
    {
        public int Code { get; }
        public QuitSignal(int code) => Code = code;
    }

    public sealed record TestOutcome(
        string Category,        // PASS | FAIL | TIMEOUT | CRASH
        string Detail,          // first failure message / error description
        List<string> Output);   // captured print() output (whole test)

    public sealed class TestExecutor
    {
        private readonly Action<string>? _echo;   // live output mirror (--single)

        /// <summary>Per-file engine budget. The default sweep uses 10s; the
        /// slow lane retries script-timeouts once at a minute before calling
        /// them timeouts for good (heavy but correct tests must not die to a
        /// wall-clock budget — infinite loops still hit this ceiling).</summary>
        public int TimeLimitMs { get; set; } = 10_000;

        public TestExecutor(Action<string>? echo) => _echo = echo;

        public TestOutcome Run(string relpath)
        {
            var output = new StringBuilder();
            var scope = new JsScope();
            JsRuntime.PopulateGlobalScope(scope);

            var interpreter = new JsInterpreter(scope, null, _ => { }, _ => { },
                timeLimitMs: TimeLimitMs, heapLimitBytes: 67_108_864);
            interpreter.PropagateTopLevelExceptions = true;
            interpreter.RegisterRuntimeBuiltins();
            InstallHostFunctions(scope, interpreter, output);

            int rc = 0;
            string errorDetail = "";

            foreach (string file in Corpus.HarnessChain(relpath))
            {
                string source;
                try { source = File.ReadAllText(file); }
                catch (Exception ex)
                {
                    return new TestOutcome("CRASH", $"harness read failed: {ex.Message}", Lines(output));
                }

                ProgramNode program;
                try { program = JsParser.Parse(source); }
                catch (JsParserException ex)
                {
                    rc = 3;
                    errorDetail = $"SyntaxError: {ex.Message} (line {ex.Line}, column {ex.Column}) in {Path.GetRelativePath(Corpus.Root, file)}";
                    goto classify;
                }

                try { interpreter.Execute(program); }
                catch (QuitSignal quit)
                {
                    rc = quit.Code;
                    goto classify;
                }
                catch (JsTimeoutException)
                {
                    return new TestOutcome("TIMEOUT",
                        $"script timeout in {Path.GetRelativePath(Corpus.Root, file)}", Lines(output));
                }
                catch (JsOutOfMemoryException)
                {
                    rc = 3;
                    errorDetail = "InternalError: out of memory (engine heap budget)";
                    goto classify;
                }
                catch (JsThrownException ex)
                {
                    rc = 3;
                    errorDetail = "uncaught exception: " + FormatThrown(ex.Value);
                    goto classify;
                }
                catch (JsInterpreterException ex)
                {
                    rc = 3;
                    errorDetail = "uncaught " + ErrorNameFor(ex) + ": " + ex.Message;
                    goto classify;
                }
                catch (Exception ex)
                {
                    return new TestOutcome("CRASH",
                        $"{ex.GetType().Name}: {ex.Message}", Lines(output));
                }
            }

        classify:
            var (category, detail) = Classify(relpath, rc, output.ToString(), errorDetail);
            return new TestOutcome(category, detail, Lines(output));
        }

        private static List<string> Lines(StringBuilder output) =>
            output.Length == 0 ? new List<string>() : new List<string>(output.ToString().Split('\n'));

        private void InstallHostFunctions(JsScope scope, JsInterpreter interpreter, StringBuilder output)
        {
            // print(...) — SpiderMonkey shell: args joined by spaces.
            scope.Define("print", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                string text = string.Join(" ", args.Select(a => a.ToJsString()));
                output.AppendLine(text);
                _echo?.Invoke(text);
                return JsValue.Undefined;
            }, scope, "print")));

            // version(n) — ES3 has a single language level; record and ignore.
            scope.Define("version", JsValue.FromFunction(new JsFunction((self, args) =>
                JsValue.From(0), scope, "version")));

            // gc() — no-op.
            scope.Define("gc", JsValue.FromFunction(new JsFunction((self, args) =>
                JsValue.Undefined, scope, "gc")));

            // options() / options(name) — a warning-free shell exposes none.
            scope.Define("options", JsValue.FromFunction(new JsFunction((self, args) =>
                JsValue.From(""), scope, "options")));

            // quit([code]) — terminate this script immediately.
            scope.Define("quit", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                int code = args.Length > 0 && args[0].Type == JsType.Number ? (int)args[0].GetNumber() : 0;
                throw new QuitSignal(code);
            }, scope, "quit")));

            // load(path) — evaluate another file in the same global scope,
            // resolved against the corpus root (jstests runs from there).
            scope.Define("load", JsValue.FromFunction(new JsFunction((self, args) =>
            {
                string path = args.Length > 0 ? args[0].ToJsString() : "";
                string resolved = Path.GetFullPath(Path.Combine(Corpus.Root, path));
                if (!File.Exists(resolved))
                    throw new JsInterpreterException("load: cannot open file '" + path + "'");
                var program = JsParser.Parse(File.ReadAllText(resolved));
                return interpreter.Execute(program);
            }, scope, "load")));
        }

        private static string ErrorNameFor(JsInterpreterException ex) => ex switch
        {
            JsTypeErrorException => "TypeError",
            JsReferenceErrorException => "ReferenceError",
            JsRangeErrorException => "RangeError",
            JsUriErrorException => "URIError",
            _ => "InternalError",
        };

        private static string FormatThrown(JsValue value)
        {
            if (value.Type == JsType.Object)
            {
                var obj = value.GetObjectOrFunction();
                JsValue name = obj.Get("name"), message = obj.Get("message");
                if (name.Type == JsType.String && message.Type == JsType.String)
                    return name.ToJsString() + ": " + message.ToJsString();
            }
            return value.ToJsString();
        }

        /// <summary>Faithful port of lib/results.py TestResult.from_output.</summary>
        private static (string Category, string Detail) Classify(
            string relpath, int rc, string capturedOutput, string errorDetail)
        {
            int passes = 0, failures = 0;
            string firstFailure = "";
            var expectedRcs = new List<int>();
            if (relpath.EndsWith("-n.js", StringComparison.Ordinal))
                expectedRcs.Add(3);

            foreach (string rawLine in capturedOutput.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith(" FAILED!", StringComparison.Ordinal))
                {
                    failures++;
                    if (firstFailure.Length == 0)
                        firstFailure = line.Length > 9 ? line[9..] : "(empty failure line)";
                }
                else if (line.StartsWith(" PASSED!", StringComparison.Ordinal))
                {
                    passes++;
                }
                else
                {
                    Match m = Regex.Match(line,
                        @"^--- NOTE: IN THIS TESTCASE, WE EXPECT EXIT CODE (-?\d+) ---$");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int noted))
                        expectedRcs.Add(noted);
                }
            }

            if (rc != 0 && !expectedRcs.Contains(rc))
            {
                if (rc == 3)
                    return ("FAIL", FirstNonEmpty(errorDetail, firstFailure, "unexpected exit code 3"));
                return ("CRASH", $"unexpected exit code {rc}");
            }

            bool pass = (rc != 0 || passes > 0) && failures == 0;
            if (pass) return ("PASS", "");
            return ("FAIL", FirstNonEmpty(firstFailure, errorDetail,
                passes == 0 ? "no test results reported (test produced no output)" : ""));
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string v in values)
                if (v.Length > 0) return v;
            return "";
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // --child: stream tests from stdin, report blocks on stdout
    // ─────────────────────────────────────────────────────────────────────

    private static int ChildMain()
    {
        Console.InputEncoding = Encoding.UTF8;
        var stdin = Console.In;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var executor = new TestExecutor(null);

        string? relpath;
        while ((relpath = stdin.ReadLine()) != null)
        {
            if (relpath.Length == 0) continue;
            if (relpath == "@SLOW") { executor.TimeLimitMs = SlowLaneLimitMs; continue; }
            if (relpath == "@FAST") { executor.TimeLimitMs = 10_000; continue; }
            TestOutcome outcome;
            try { outcome = executor.Run(relpath); }
            catch (Exception ex) { outcome = new TestOutcome("CRASH", $"executor: {ex.GetType().Name}: {ex.Message}", new List<string>()); }

            stdout.WriteLine($"@RESULT {relpath} {outcome.Category}");
            if (outcome.Category != "PASS")
            {
                string detail = outcome.Detail.Length > 0 ? outcome.Detail : "(no detail)";
                foreach (string chunk in SplitLines(detail))
                    stdout.WriteLine("@OUT " + chunk);
                int sent = 0;
                foreach (string line in outcome.Output)
                {
                    if (line.TrimStart().StartsWith(" FAILED!", StringComparison.Ordinal) ||
                        line.TrimStart().StartsWith(" PASSED!", StringComparison.Ordinal))
                    {
                        stdout.WriteLine("@OUT   " + line);
                        if (++sent >= 8) break;
                    }
                }
            }
            stdout.WriteLine("@END");
        }
        return 0;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (string line in text.Replace("\r", "").Split('\n'))
            if (line.Length > 0)
                yield return line.Length > 400 ? line[..400] + "…" : line;
    }

    // ─────────────────────────────────────────────────────────────────────
    // --single: one test, verbose
    // ─────────────────────────────────────────────────────────────────────

    private static int SingleMain(string relpath)
    {
        relpath = relpath.Replace('\\', '/');
        Console.WriteLine($"─ {relpath} ────────────────────────────────");
        var executor = new TestExecutor(line => Console.WriteLine("  | " + line));
        var sw = Stopwatch.StartNew();
        TestOutcome outcome = executor.Run(relpath);
        sw.Stop();
        Console.WriteLine($"──────────────────────────────────────────");
        Console.WriteLine($"RESULT: {outcome.Category}  ({sw.ElapsedMilliseconds} ms)");
        if (outcome.Detail.Length > 0) Console.WriteLine("detail: " + outcome.Detail);
        Console.WriteLine($"output lines: {outcome.Output.Count}");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Parent: orchestrate workers, watchdog, aggregate
    // ─────────────────────────────────────────────────────────────────────

    private sealed record TestRecord(string Category, string RelPath, string Detail, List<string> ExtraLines);

    private sealed class Worker
    {
        public Process? Proc;
        public int KilledByWatchdog;
        public bool SlowMode;
        public bool SlowSent;
    }

    private static int ParentMain(string[] args)
    {
        string? filter = null;
        int jobs = 2;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
            else if (args[i] == "--jobs" && i + 1 < args.Length) jobs = int.Parse(args[++i]);
            else if (args[i] == "--run") { /* default mode */ }
            else { Console.Error.WriteLine($"unknown argument: {args[i]}"); return 2; }
        }

        List<string> allTests = Corpus.ListTests();
        var skips = SkipList.Load();
        var records = new ConcurrentDictionary<string, TestRecord>(StringComparer.Ordinal);

        // pre-scan skips
        var queue = new List<string>();
        int skipped = 0;
        foreach (string test in allTests)
        {
            if (filter != null && !test.Contains(filter, StringComparison.Ordinal)) continue;
            if (skips.Match(test, out string reason))
            {
                records[test] = new TestRecord("SKIP", test, reason, new List<string>());
                skipped++;
            }
            else queue.Add(test);
        }
        int total = records.Count;

        Console.WriteLine($"Es3Conformance sweep: {total} tests ({skipped} skipped, {queue.Count} to run), {jobs} workers");
        var sw = Stopwatch.StartNew();

        var workQueue = new ConcurrentQueue<string>(queue);
        var workers = new Worker[jobs];
        for (int i = 0; i < jobs; i++) workers[i] = new Worker();
        var threads = new Thread[jobs];
        for (int i = 0; i < jobs; i++)
        {
            int index = i;
            threads[i] = new Thread(() => WorkerLoop(index, workers, workQueue, records, total, sw)) { IsBackground = true };
            threads[i].Start();
        }
        foreach (Thread t in threads) t.Join();

        // ── Slow lane: give script-timeouts ONE generous retry so heavy but
        // correct tests aren't failed on wall-clock grounds. Genuinely
        // infinite loops still burn the 60s ceiling and stay TIMEOUTs.
        var scriptTimeouts = queue
            .Where(t => records.TryGetValue(t, out var r) &&
                        r.Category == "TIMEOUT" && r.Detail.StartsWith("script timeout", StringComparison.Ordinal))
            .ToList();
        if (scriptTimeouts.Count > 0)
        {
            Console.WriteLine($"slow lane: retrying {scriptTimeouts.Count} script-timeout(s) at {SlowLaneLimitMs / 1000}s budget");
            var retryQueue = new ConcurrentQueue<string>(scriptTimeouts);
            var retryWorkers = new Worker[jobs];
            for (int i = 0; i < jobs; i++) retryWorkers[i] = new Worker { SlowMode = true };
            var retryThreads = new Thread[jobs];
            for (int i = 0; i < jobs; i++)
            {
                int index = i;
                retryThreads[i] = new Thread(() => WorkerLoop(index, retryWorkers, retryQueue, records, total, sw))
                { IsBackground = true };
                retryThreads[i].Start();
            }
            foreach (Thread t in retryThreads) t.Join();
        }

        WriteResults(records, allTests, sw.Elapsed);
        return 0;
    }

    private static void WorkerLoop(
        int index, Worker[] workers, ConcurrentQueue<string> queue,
        ConcurrentDictionary<string, TestRecord> records, int total, Stopwatch sw)
    {
        var worker = workers[index];
        while (queue.TryDequeue(out string? test))
        {
        retry:
            if (worker.Proc is null || worker.Proc.HasExited)
                worker.Proc = StartWorker();
            if (worker.SlowMode && !worker.SlowSent)
            {
                worker.Proc.StandardInput.WriteLine("@SLOW");
                worker.Proc.StandardInput.Flush();
                worker.SlowSent = true;
            }

            var block = new List<string>();
            bool watchdogFired = false;
            bool workerDied = false;
            try
            {
                worker.Proc.StandardInput.WriteLine(test);
                worker.Proc.StandardInput.Flush();

                using var timer = new Timer(_ =>
                {
                    Interlocked.Exchange(ref worker.KilledByWatchdog, 1);
                    watchdogFired = true;
                    try { worker.Proc!.Kill(entireProcessTree: true); } catch { /* already gone */ }
                }, null, WallClockWatchdogMs, Timeout.Infinite);

                string? line;
                while ((line = worker.Proc.StandardOutput.ReadLine()) != null)
                {
                    if (line == "@END") break;
                    block.Add(line);
                }
                if (line is null) workerDied = true;
            }
            catch (Exception)
            {
                workerDied = true;
            }

            if (workerDied)
            {
                // A dead worker loses at most the ONE in-flight test (we send
                // one path at a time), so attribute the failure and respawn.
                string category = watchdogFired ? "TIMEOUT" : "CRASH";
                string detail = watchdogFired
                    ? $"wall-clock watchdog ({WallClockWatchdogMs / 1000}s) killed the worker"
                    : "worker process died (engine crash)";
                records[test] = new TestRecord(category, test, detail, block.FindAll(l => l.StartsWith("@OUT ")));
                worker.Proc = null;
                Progress(records, total, sw);
                continue;
            }

            // Parse the result block.
            string resultCategory = "CRASH";
            var extras = new List<string>();
            bool forThisTest = false;
            foreach (string line in block)
            {
                if (line.StartsWith("@RESULT ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', 3);
                    if (parts.Length == 3)
                    {
                        forThisTest = parts[1] == test;
                        resultCategory = parts[2];
                    }
                }
                else if (line.StartsWith("@OUT ", StringComparison.Ordinal))
                {
                    extras.Add(line[5..]);
                }
            }
            if (!forThisTest)
            {
                // Protocol desync — treat as crash and restart this test.
                worker.Proc.Kill(entireProcessTree: true);
                worker.Proc = null;
                records[test] = new TestRecord("CRASH", test, "worker protocol desync", extras);
                goto retry;
            }
            string resultDetail = extras.FirstOrDefault(e => !e.StartsWith("  ")) ?? "";
            records[test] = new TestRecord(resultCategory, test, resultDetail, extras);
            Progress(records, total, sw);
        }
        try { worker.Proc?.Kill(entireProcessTree: true); } catch { }
    }

    private static void Progress(ConcurrentDictionary<string, TestRecord> records, int total, Stopwatch sw)
    {
        int count = records.Count;
        if (count % 200 != 0) return;
        int pass = records.Values.Count(r => r.Category == "PASS");
        int fail = records.Values.Count(r => r.Category == "FAIL");
        int to = records.Values.Count(r => r.Category == "TIMEOUT");
        int crash = records.Values.Count(r => r.Category == "CRASH");
        Console.WriteLine($"  [{count}/{total}] pass={pass} fail={fail} timeout={to} crash={crash} ({sw.Elapsed:mm\\:ss})");
    }

    private static Process StartWorker()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve current executable");
        var psi = new ProcessStartInfo(exe, "--child")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,   // engine stderr noise passes through
            CreateNoWindow = true,
            WorkingDirectory = Corpus.Root,
        };
        Process proc = Process.Start(psi)!;
        return proc;
    }

    private static void WriteResults(
        ConcurrentDictionary<string, TestRecord> records, List<string> orderedTests, TimeSpan elapsed)
    {
        string outDir = Path.Combine(AppContext.BaseDirectory.Contains("bin") || !Directory.Exists(Path.Combine(Corpus.Root, "..", "Es3Conformance", "results"))
            ? FindResultsDir() : Path.Combine(Corpus.Root, "..", "Es3Conformance", "results"),
            "");
        Directory.CreateDirectory(outDir);

        var byCategory = records.Values.GroupBy(r => r.Category)
            .ToDictionary(g => g.Key, g => g.Count());
        int pass = byCategory.GetValueOrDefault("PASS");
        int fail = byCategory.GetValueOrDefault("FAIL");
        int timeout = byCategory.GetValueOrDefault("TIMEOUT");
        int crash = byCategory.GetValueOrDefault("CRASH");
        int skip = byCategory.GetValueOrDefault("SKIP");

        // results.txt — one line per test, corpus order
        using (var w = new StreamWriter(Path.Combine(outDir, "results.txt"), false, Encoding.UTF8))
        {
            foreach (string test in orderedTests)
            {
                if (!records.TryGetValue(test, out var rec)) continue;
                string detail = rec.Detail.Replace('\n', ' ');
                if (detail.Length > 300) detail = detail[..300] + "…";
                w.WriteLine(rec.Category == "PASS" ? $"PASS {test}" : $"{rec.Category} {test} :: {detail}");
            }
        }

        // results-full.txt — failure diagnostics
        using (var w = new StreamWriter(Path.Combine(outDir, "results-full.txt"), false, Encoding.UTF8))
        {
            foreach (string test in orderedTests)
            {
                if (!records.TryGetValue(test, out var rec)) continue;
                if (rec.Category is "PASS" or "SKIP") continue;
                w.WriteLine($"──── {rec.Category} {test}");
                foreach (string line in rec.ExtraLines.Take(MaxDetailLines))
                    w.WriteLine("  " + line);
                w.WriteLine();
            }
        }

        // per-suite breakdown
        var suiteStats = new SortedDictionary<string, Dictionary<string, int>>();
        foreach (var rec in records.Values)
        {
            string suite = rec.RelPath.Contains('/') ? rec.RelPath[..rec.RelPath.IndexOf('/')] : rec.RelPath;
            if (!suiteStats.TryGetValue(suite, out var cat)) suiteStats[suite] = cat = new Dictionary<string, int>();
            cat[rec.Category] = cat.GetValueOrDefault(rec.Category) + 1;
        }

        using (var w = new StreamWriter(Path.Combine(outDir, "summary.txt"), false, Encoding.UTF8))
        {
            w.WriteLine($"Retro96 ES3 conformance — Mozilla legacy suite sweep");
            w.WriteLine($"date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   elapsed: {elapsed:mm\\:ss}");
            w.WriteLine();
            w.WriteLine($"total: {records.Count}");
            w.WriteLine($"  PASS    {pass}");
            w.WriteLine($"  FAIL    {fail}");
            w.WriteLine($"  TIMEOUT {timeout}");
            w.WriteLine($"  CRASH   {crash}");
            w.WriteLine($"  SKIP    {skip}");
            w.WriteLine();
            w.WriteLine("per suite:");
            foreach ((string suite, var cats) in suiteStats)
            {
                var parts = cats.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}");
                w.WriteLine($"  {suite,-8} {string.Join(" ", parts)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"════ RESULTS ════════════════════════════════");
        Console.WriteLine($"PASS {pass}   FAIL {fail}   TIMEOUT {timeout}   CRASH {crash}   SKIP {skip}   (total {records.Count}, {elapsed:mm\\:ss})");
        foreach ((string suite, var cats) in suiteStats)
        {
            var parts = cats.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}");
            Console.WriteLine($"  {suite,-8} {string.Join(" ", parts)}");
        }
        Console.WriteLine($"files: {Path.Combine(outDir, "results.txt")}");
    }

    private static string FindResultsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "tests", "Es3Conformance", "results");
            if (d.Exists && Directory.Exists(Path.Combine(d.FullName, "tests")))
                return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "results");
    }
}
