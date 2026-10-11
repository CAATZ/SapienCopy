using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SapienCopy;

static class CopyWorkerTests
{
    static int checks;
    static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        if (args.Length == 1 && args[0] == "--test-sleeper") { Thread.Sleep(60000); return 0; }
        if (CopyWorker.TryRunWorker(args, FakeEngine)) return Environment.ExitCode;
        try
        {
            if (args.Length == 2 && args[0] == "--release")
            {
                RealCopy(Path.GetFullPath(args[1]));
                Console.WriteLine("PASS: production executable worker entry point, Unicode copy and clean no-log destination.");
                return 0;
            }
            var sample = Sample();
            var restored = CopyWorker.Deserialize<CopyResult>(CopyWorker.Serialize(sample));
            Check(restored.Folders.Count == 1 && restored.Folders[0].Source == "C:\\Prueba \u00e1\\\u6f22\u5b57" && restored.Folders[0].ExitCode == 1, "Result lists and Unicode round-trip");
            Check(restored.Session != null && restored.Session.OutputRelativePaths[0] == ".settings.json", "Pause session data survives the worker protocol");
            var packet = CopyWorker.ProgressPacket.From(new CopyProgress { IsScanning = true, Percent = 42.5, Remaining = TimeSpan.FromSeconds(72), EstimatedBytesCopied = 85, TotalBytes = 200 });
            var state = CopyWorker.Deserialize<CopyWorker.ProgressPacket>(CopyWorker.Serialize(packet)).ToProgress();
            Check(state.IsScanning && state.Percent == 42.5 && state.Remaining == TimeSpan.FromSeconds(72), "Progress and ETA round-trip");

            CopyProgress receivedProgress = null;
            double? receivedSpeed = null;
            CopyResult receivedState = null;
            CopyResult completed = CopyWorker.RunForTests(Request("success"), null, CancellationToken.None, delegate(double? rate) { receivedSpeed = rate; }, delegate(CopyProgress value) { receivedProgress = value; }, TimeSpan.FromSeconds(5), null, delegate(CopyResult value) { receivedState = value; });
            Check(completed.Status == "Completed" && completed.Folders[0].Source.Contains("\u6f22\u5b57"), "Worker handshake and result preserve Unicode");
            Check(receivedSpeed == 1234 && receivedProgress.Remaining == TimeSpan.FromSeconds(72), "Worker speed and ETA events");
            Check(receivedState != null && receivedState.Session != null && !receivedState.WorkerStopped && completed.WorkerStopped, "Session state arrives before parent confirms worker stop");

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var request = Request("block"); request.Session = new CopySession();
                var cancelled = CopyWorker.Run(request, null, cancellation.Token);
                Check(cancelled.Cancelled && cancelled.WorkerStopped && Object.ReferenceEquals(cancelled.Session, request.Session), "Pre-cancel avoids worker work and preserves the paused session");
            }
            int descendant = 0;
            string scratch = null;
            string bindingTarget = null;
            using (var cancellation = new CancellationTokenSource())
            {
                var watch = Stopwatch.StartNew();
                var cancelled = CopyWorker.RunForTests(Request("spawn"), delegate(string text) {
                    if (text.StartsWith("PID: ")) { descendant = Int32.Parse(text.Substring(5)); cancellation.Cancel(); }
                    if (text.StartsWith("TEMP: ")) scratch = text.Substring(6);
                    if (text.StartsWith("TARGET: ")) bindingTarget = text.Substring(8);
                }, cancellation.Token, null, null, TimeSpan.FromSeconds(5));
                Check(cancelled.Cancelled && cancelled.Status == "Cancelled" && cancelled.WorkerStopped && cancelled.Session != null, "Blocked native work returns Cancelled with confirmed stop and retained session");
                Check(watch.Elapsed < TimeSpan.FromSeconds(10), "Blocked cancellation is bounded");
                Check(descendant != 0 && Exited(descendant), "Owned descendant is terminated before return");
                Check(scratch != null && !Directory.Exists(scratch), "Hard-stop scratch is removed by cleanup worker");
                Check(bindingTarget != null && File.ReadAllText(Path.Combine(bindingTarget, "sentinel.txt")) == "Do not traverse this junction", "Forced-stop cleanup unlinks bindings without touching their targets");
            }
            var timeoutWatch = Stopwatch.StartNew();
            bool timedOut = false;
            try { CopyWorker.RunForTests(Request("block"), null, CancellationToken.None, null, null, TimeSpan.FromMilliseconds(300)); }
            catch (IOException error) { timedOut = error.Message.Contains("metadata scan stopped responding") && error.Message.Contains("blocked metadata"); }
            Check(timedOut && timeoutWatch.Elapsed < TimeSpan.FromSeconds(10), "Metadata timeout includes last path and is bounded");

            var progressing = CopyWorker.RunForTests(Request("advancing"), null, CancellationToken.None, null, null, TimeSpan.FromMilliseconds(500));
            Check(progressing.Status == "Completed", "Actual metadata progress resets watchdog");
            bool copyingSeen = false, staleScan = false;
            var transferring = CopyWorker.RunForTests(Request("transfer"), delegate(string text) {
                if (text.StartsWith("Robocopy running:")) copyingSeen = true;
                if (copyingSeen && text.StartsWith("Scan: ")) staleScan = true;
            }, CancellationToken.None, null, null, TimeSpan.FromMilliseconds(300));
            Check(transferring.Status == "Completed", "Actual copying is not subject to metadata timeout");
            Check(copyingSeen && !staleScan, "An old scan cannot overwrite the copying phase");
            bool crashed = false;
            try { CopyWorker.RunForTests(Request("crash"), null, CancellationToken.None, null, null, TimeSpan.FromSeconds(5)); }
            catch (IOException error) { crashed = error.Message.Contains("without confirming completion"); }
            Check(crashed, "Worker crash cannot report Completed");
            bool prematureComplete = false;
            try { CopyWorker.RunForTests(Request("latecrash"), null, CancellationToken.None, null, delegate(CopyProgress value) { if (value.IsComplete) prematureComplete = true; }, TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
            Check(!prematureComplete, "Completion progress waits for confirmed result and worker exit");

            using (var cancellation = new CancellationTokenSource())
            {
                var cancelled = CopyWorker.RunForTests(Request("cooperative"), delegate(string text) { if (text == "Ready for cooperative cancellation") cancellation.Cancel(); }, cancellation.Token, null, null, TimeSpan.FromSeconds(5));
                Check(cancelled.Cancelled && cancelled.CleanupWarning == null && cancelled.Folders.Count == 1, "Cooperative cancellation preserves result without forced-stop warning");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                int stateEvents = 0;
                var paused = CopyWorker.RunForTests(Request("state-pause"), null, cancellation.Token, null, null, TimeSpan.FromSeconds(5), null, delegate(CopyResult value) {
                    Check(value.Session != null && !value.WorkerStopped, "A live worker state cannot claim that it has stopped");
                    stateEvents++; cancellation.Cancel();
                });
                Check(stateEvents > 0 && paused.Cancelled && paused.WorkerStopped && paused.Session != null, "Pause can cancel after session receipt and retain it after confirmed worker exit");
            }
            bool callbackFailureStopped = false;
            try { CopyWorker.RunForTests(Request("state-pause"), null, CancellationToken.None, null, null, TimeSpan.FromSeconds(5), null, delegate(CopyResult value) { throw new InvalidOperationException("Synthetic state callback failure"); }); }
            catch (CopyWorkerException error) { callbackFailureStopped = error.WorkerStopped && error.Message == "Synthetic state callback failure"; }
            Check(callbackFailureStopped, "State callback failure confirms worker shutdown before returning an error");
            bool partialReported = false;
            try { CopyWorker.RunForTests(Request("timeout-state"), null, CancellationToken.None, null, null, TimeSpan.FromMilliseconds(300)); }
            catch (IOException error) { partialReported = error.Message.Contains("C:\\Partial copy") && error.Message.Contains("C:\\Partial reports") && error.Message.Contains("incomplete"); }
            Check(partialReported, "Timeout reports saved partial copy and report locations");

            RealCopy(null);
            Console.WriteLine("PASS: " + checks + " worker assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void RealCopy(string executable)
    {
        string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(fixture, "Source \u00e1 \u6f22\u5b57"), destination = Path.Combine(fixture, "Destination \u00f1");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "unicode \u00e1.txt"), "Worker copy test");
        string selectedFile = Path.Combine(fixture, ".settings.json");
        File.WriteAllText(selectedFile, "Synthetic file selection");
        File.WriteAllText(Path.Combine(fixture, "unselected.txt"), "Unselected sibling");
        var copied = CopyWorker.RunForTests(new CopyRequest { Sources = new[] { source, selectedFile }, Destination = destination, DirectCopy = true, SaveLogs = false, EstimateProgress = true }, null, CancellationToken.None, null, null, TimeSpan.FromSeconds(10), executable);
        Check(copied.Status == "Completed" && File.ReadAllText(Path.Combine(destination, Path.GetFileName(source), "unicode \u00e1.txt")) == "Worker copy test", "Real Robocopy completes through isolated worker");
        Check(File.ReadAllText(Path.Combine(destination, ".settings.json")) == "Synthetic file selection" && copied.EstimatedTotalBytes > 0, "Selected file and size estimate survive the worker boundary");
        Check(Directory.GetDirectories(destination).Length == 1 && Directory.GetFiles(destination).Length == 1, "No-log direct copy leaves only selected file and folder");
        Check(copied.Session != null && copied.WorkerStopped, "Real copy returns a resumable identity session and confirmed worker exit");
    }

    static CopyRequest Request(string mode) { return new CopyRequest { Sources = new[] { "test:" + mode, "C:\\Prueba \u00e1\\\u6f22\u5b57" }, SaveLogs = false }; }
    static CopyResult Sample()
    {
        var result = new CopyResult { Status = "Completed", CopiedCount = 1, RunFolder = "C:\\Salida \u00e1", EstimatedTotalBytes = 200, SaveLogs = false, Session = new CopySession { OutputRelativePaths = new[] { ".settings.json" } } };
        result.Folders.Add(new FolderResult { Source = "C:\\Prueba \u00e1\\\u6f22\u5b57", Destination = "C:\\Salida \u00e1", Status = "Copied", ExitCode = 1 });
        return result;
    }
    static CopyResult FakeEngine(CopyRequest request, Action<string> progress, CancellationToken token, Action<double?> speed, Action<CopyProgress> details)
    {
        if (request.Sources == null || !request.Sources[0].StartsWith("test:")) return CopyEngine.Run(request, progress, token, speed, details);
        if (request.Sources[1] != "C:\\Prueba \u00e1\\\u6f22\u5b57") throw new Exception("Unicode request was corrupted.");
        string mode = request.Sources[0].Substring(5);
        if (mode == "crash") Environment.Exit(4);
        if (mode == "latecrash") { details(new CopyProgress { Percent = 100, IsComplete = true }); Thread.Sleep(350); Environment.Exit(4); }
        CopyEngine.StateChanged(Sample());
        progress("Scan: blocked metadata | 1 | C:\\Test metadata path");
        if (mode == "state-pause") { token.WaitHandle.WaitOne(5000); return Sample(); }
        if (mode == "cooperative")
        {
            progress("Ready for cooperative cancellation");
            token.WaitHandle.WaitOne(5000);
            var result = Sample(); result.Status = "Cancelled"; result.Cancelled = true;
            return result;
        }
        if (mode == "timeout-state")
        {
            CopyEngine.StateChanged(new CopyResult { Status = "Running", RunFolder = "C:\\Partial copy", ReportFolder = "C:\\Partial reports", SaveLogs = true });
            Thread.Sleep(60000);
        }
        if (mode == "spawn")
        {
            string scratch = request.WorkerTemporaryFolder;
            string helper = Path.Combine(scratch, "SapienCopy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helper);
            File.WriteAllText(Path.Combine(helper, "scratch.log"), "Owned test scratch");
            string target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "worker-binding-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "Do not traverse this junction");
            new RobocopyBindings(new[] { VolumeIdentity.Capture(target) }, scratch);
            progress("TEMP: " + scratch);
            progress("TARGET: " + target);
            using (var child = Process.Start(new ProcessStartInfo { FileName = Process.GetCurrentProcess().MainModule.FileName, Arguments = "--test-sleeper", UseShellExecute = false, CreateNoWindow = true }))
            { progress("PID: " + child.Id); Thread.Sleep(60000); }
        }
        if (mode == "block") Thread.Sleep(60000);
        if (mode == "advancing")
            for (int i = 0; i < 8; i++) { Thread.Sleep(150); progress("Scan: advancing metadata | " + i + " | C:\\Test"); }
        if (mode == "transfer") { progress("Scan complete."); progress("Robocopy running: synthetic transfer"); Thread.Sleep(1000); }
        speed(1234);
        details(new CopyProgress { Percent = 42.5, Remaining = TimeSpan.FromSeconds(72) });
        return Sample();
    }
    static bool Exited(int id) { try { using (var process = Process.GetProcessById(id)) return process.HasExited; } catch (ArgumentException) { return true; } }
    static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; }
}
