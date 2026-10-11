using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace SapienCopy
{
    public sealed class CopyWorkerException : IOException
    {
        public bool WorkerStopped { get; private set; }
        internal CopyWorkerException(Exception error, bool stopped) : base(error.Message, error) { WorkerStopped = stopped; }
    }

    // The GUI owns this process boundary, so a blocked filesystem driver cannot strand its copy task.
    public static class CopyWorker
    {
        const string Argument = "--copy-worker", TemporaryPrefix = "SapienCopy-worker-";
        internal delegate CopyResult Execute(CopyRequest request, Action<string> progress, CancellationToken token, Action<double?> speed, Action<CopyProgress> details);

        public static CopyResult Run(CopyRequest request, Action<string> progress, CancellationToken cancellation, Action<double?> speed = null, Action<CopyProgress> details = null, Action<CopyResult> stateChanged = null)
        {
            return RunForTests(request, progress, cancellation, speed, details, TimeSpan.FromMinutes(2), null, stateChanged);
        }

        internal static CopyResult RunForTests(CopyRequest request, Action<string> progress, CancellationToken cancellation, Action<double?> speed, Action<CopyProgress> details, TimeSpan idleTimeout, string executable = null, Action<CopyResult> stateChanged = null)
        {
            if (cancellation.IsCancellationRequested)
            {
                CopyResult cancelled = Cancelled(request, null);
                cancelled.WorkerStopped = true;
                return cancelled;
            }
            string temporary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SapienCopy", "Temp", TemporaryPrefix + Guid.NewGuid().ToString("N"));
            CopyResult result = null;
            bool stopped = false;
            Exception failure = null;
            try
            {
                result = RunProcess(new WorkerStart { Request = request, Temporary = temporary }, progress, cancellation, speed, details, stateChanged, idleTimeout, false, executable, out stopped);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (result != null) result.WorkerStopped = stopped;
                // Cleanup also runs out of process; even disconnected storage cannot block the GUI task here.
                string warning = stopped ? Cleanup(temporary, executable) : "Worker exit could not be confirmed; temporary files were left untouched at " + temporary + ".";
                if (warning != null)
                {
                    if (result != null) result.CleanupWarning = JoinWarning(result.CleanupWarning, warning);
                    if (progress != null) progress("Cleanup warning: " + warning);
                }
            }
            if (failure != null) throw new CopyWorkerException(failure, stopped);
            return result;
        }

        public static bool TryRunWorker(string[] args) { return TryRunWorker(args, CopyEngine.Run); }

        internal static bool TryRunWorker(string[] args, Execute execute)
        {
            if (args.Length != 1 || args[0] != Argument) return false;
            // A WinExe has redirected pipes but no console whose code page can be changed.
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            object outputLock = new object();
            Action<WorkerEvent> emit = delegate(WorkerEvent item) {
                lock (outputLock) { output.WriteLine(Serialize(item)); }
            };
            try
            {
                // No folder access or Robocopy start is allowed until the parent assigns our job and sends this line.
                WorkerStart start = Deserialize<WorkerStart>(input.ReadLine());
                if (start == null || !IsPrivateTemporaryName(start.Temporary)) throw new InvalidDataException("Invalid copy worker handshake.");
                if (start.CleanupOnly)
                {
                    DeleteTemporary(start.Temporary);
                    emit(new WorkerEvent { Kind = "result", Result = new CopyResult { Status = "Completed" } });
                    return true;
                }
                using (var cancellation = new CancellationTokenSource())
                {
                    var reader = new Thread(delegate() {
                        try { if (input.ReadLine() == "cancel") cancellation.Cancel(); }
                        catch (IOException) { }
                        catch (ObjectDisposedException) { }
                    });
                    reader.IsBackground = true;
                    reader.Start();
                    start.Temporary = VolumeIdentity.NormalizeScratchLocation(start.Temporary);
                    Directory.CreateDirectory(start.Temporary);
                    Environment.SetEnvironmentVariable("TEMP", start.Temporary);
                    Environment.SetEnvironmentVariable("TMP", start.Temporary);
                    start.Request.WorkerTemporaryFolder = start.Temporary;
                    CopyEngine.StateChanged = delegate(CopyResult state) { emit(new WorkerEvent { Kind = "state", Result = state }); };
                    CopyResult result = execute(start.Request,
                        delegate(string text) { emit(new WorkerEvent { Kind = "message", Text = text }); }, cancellation.Token,
                        delegate(double? rate) { emit(new WorkerEvent { Kind = "speed", Speed = rate }); },
                        delegate(CopyProgress state) { emit(new WorkerEvent { Kind = "progress", Progress = ProgressPacket.From(state) }); });
                    emit(new WorkerEvent { Kind = "result", Result = result });
                }
            }
            catch (Exception error)
            {
                emit(new WorkerEvent { Kind = "error", Text = error.Message });
                Environment.ExitCode = 1;
            }
            return true;
        }

        static CopyResult RunProcess(WorkerStart start, Action<string> progress, CancellationToken cancellation, Action<double?> speed, Action<CopyProgress> details, Action<CopyResult> stateChanged, TimeSpan idleTimeout, bool cleanup, string executable, out bool stopped)
        {
            stopped = true;
            using (var job = new OwnedJob())
            using (var process = new Process())
            {
                var received = new WorkerOutput();
                process.StartInfo = new ProcessStartInfo {
                    FileName = executable ?? Process.GetCurrentProcess().MainModule.FileName, Arguments = Argument,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { received.Accept(args.Data); };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) received.ErrorText(args.Data); };
                process.Start();
                stopped = false;
                bool stopAttempted = false;
                try
                {
                    // The child's first operation is reading stdin; assignment failure cannot start a copy.
                    job.Assign(process);
                    if (cancellation.IsCancellationRequested) return Cancelled(start.Request, null);
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    // StandardInput otherwise uses the host console code page, which can corrupt Unicode paths.
                    var input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
                    Task sending = input.WriteLineAsync(Serialize(start));
                    Stopwatch stopping = null;
                    string timeout = null;
                    bool forced = false;
                    var runtime = Stopwatch.StartNew();
                    while (true)
                    {
                        received.Deliver(progress, speed, details == null ? (Action<CopyProgress>)null : delegate(CopyProgress state) { if (!cancellation.IsCancellationRequested) details(state); }, stateChanged);
                        if (process.HasExited && received.Closed) break;
                        if (sending.IsFaulted) throw new IOException("Cannot send the copy request to its worker.", sending.Exception);
                        if (stopping == null)
                        {
                            if (cleanup && runtime.Elapsed > idleTimeout) timeout = "Temporary cleanup did not finish promptly.";
                            else if (!cleanup && received.MetadataIdle > idleTimeout)
                                timeout = "The metadata scan stopped responding for " + Math.Round(idleTimeout.TotalSeconds) + " seconds. Last activity: " + received.LastScan + ". Check the source or drive and try again.";
                            if (cancellation.IsCancellationRequested || timeout != null)
                            {
                                stopping = Stopwatch.StartNew();
                                try { if (sending.IsCompleted) sending = input.WriteLineAsync("cancel"); }
                                catch (IOException) { }
                                if (progress != null) progress(timeout ?? "Cancelling the copy worker...");
                            }
                        }
                        if (stopping != null && stopping.ElapsedMilliseconds >= (cleanup ? 0 : 1500))
                        {
                            stopAttempted = true; job.Stop();
                            forced = true;
                            break;
                        }
                        Thread.Sleep(50);
                    }
                    if (job.ActiveProcesses != 0) { stopAttempted = true; job.Stop(); }
                    if (!process.WaitForExit(1000)) throw new IOException("The copy worker has not exited. Check Task Manager before disconnecting the drive.");
                    stopped = true;
                    received.Deliver(progress, speed, details == null ? (Action<CopyProgress>)null : delegate(CopyProgress state) { if (!cancellation.IsCancellationRequested) details(state); }, stateChanged, true);
                    if (timeout != null)
                    {
                        CopyResult partial = received.Result ?? received.State;
                        if (partial != null && !String.IsNullOrEmpty(partial.RunFolder))
                            timeout += " Partial files were kept in " + partial.RunFolder + ".";
                        if (partial != null && !String.IsNullOrEmpty(partial.ReportFolder))
                            timeout += " Reports at " + partial.ReportFolder + " may still show Running; this copy is incomplete.";
                        throw new IOException(timeout);
                    }
                    if (stopping != null || cancellation.IsCancellationRequested)
                    {
                        CopyResult cancelled = Cancelled(start.Request, received.Result ?? received.State);
                        if (forced && cancelled.SaveLogs)
                            cancelled.CleanupWarning = JoinWarning(cancelled.CleanupWarning, "The worker was forcibly stopped. Saved reports may still show Running; this copy is incomplete.");
                        return cancelled;
                    }
                    if (received.Error != null) throw new IOException(received.Error);
                    if (process.ExitCode != 0 || received.Result == null)
                        throw new IOException("The copy worker exited without confirming completion." + received.StandardError);
                    if (received.Result.Status == "Completed" && !cancellation.IsCancellationRequested) received.DeliverCompletion(details);
                    return received.Result;
                }
                finally
                {
                    stopped = false;
                    if (job.Assigned)
                    {
                        if (!stopAttempted && job.ActiveProcesses != 0) job.Stop();
                    }
                    else if (!process.HasExited) process.Kill();
                    stopped = process.WaitForExit(1000) && job.ActiveProcesses == 0;
                }
            }
        }

        static CopyResult Cancelled(CopyRequest request, CopyResult result)
        {
            result = result ?? new CopyResult { SaveLogs = request != null && request.SaveLogs };
            if (result.Session == null && request != null) result.Session = request.Session;
            result.Status = "Cancelled";
            result.Cancelled = true;
            foreach (FolderResult folder in result.Folders)
                if (folder.Status == "Running") folder.Status = "Cancelled";
            return result;
        }

        static string Cleanup(string temporary, string executable)
        {
            try
            {
                bool stopped;
                RunProcess(new WorkerStart { Temporary = temporary, CleanupOnly = true }, null, CancellationToken.None, null, null, null, TimeSpan.FromSeconds(2), true, executable, out stopped);
                return null;
            }
            catch (Exception error) { return "Temporary worker files may remain at " + temporary + ". " + error.Message; }
        }

        static bool IsPrivateTemporaryName(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            Guid value;
            return name.StartsWith(TemporaryPrefix, StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(TemporaryPrefix.Length), "N", out value) && Path.IsPathRooted(path);
        }

        static void DeleteTemporary(string path)
        {
            if (!IsPrivateTemporaryName(path)) throw new IOException("Unrecognized worker temporary folder.");
            path = VolumeIdentity.NormalizeScratchLocation(path);
            if (!Directory.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Worker temporary folder became a link; it was left untouched.");
            foreach (string directory in Directory.GetDirectories(path))
            {
                string name = Path.GetFileName(directory);
                Guid value;
                if (name.StartsWith("SapienCopyBindings-", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring("SapienCopyBindings-".Length), "N", out value))
                {
                    DeleteBindings(directory);
                    continue;
                }
                if (!name.StartsWith("SapienCopy-", StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring("SapienCopy-".Length), "N", out value) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Unexpected temporary directory was left untouched.");
                foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory, false);
            }
            Directory.Delete(path, false);
        }

        static void DeleteBindings(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Worker binding folder became a link; it was left untouched.");
            foreach (string alias in Directory.GetFileSystemEntries(path))
            {
                string name = Path.GetFileName(alias);
                const FileAttributes link = FileAttributes.Directory | FileAttributes.ReparsePoint;
                if (name.Length != 10 || !name.StartsWith("volume-", StringComparison.Ordinal) || name[7] < '0' || name[7] > '9' || name[8] < '0' || name[8] > '9' || name[9] < '0' || name[9] > '9' || (File.GetAttributes(alias) & link) != link)
                    throw new IOException("Unexpected worker binding was left untouched.");
                // Unlink the junction itself; never enumerate or delete through its target.
                Directory.Delete(alias, false);
            }
            Directory.Delete(path, false);
        }

        static string JoinWarning(string first, string second) { return String.IsNullOrEmpty(first) ? second : first + " " + second; }
        internal static string Serialize(object value) { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(value); }
        internal static T Deserialize<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<T>(value); }

        public sealed class WorkerStart { public CopyRequest Request; public string Temporary; public bool CleanupOnly; }
        public sealed class WorkerEvent { public string Kind, Text; public CopyResult Result; public double? Speed; public ProgressPacket Progress; }
        public sealed class ProgressPacket
        {
            public bool IsScanning, IsComplete;
            public double? Percent, RemainingSeconds;
            public double EstimatedBytesCopied, TotalBytes;
            internal static ProgressPacket From(CopyProgress value)
            {
                return new ProgressPacket { IsScanning = value.IsScanning, IsComplete = value.IsComplete, Percent = value.Percent, RemainingSeconds = value.Remaining.HasValue ? (double?)value.Remaining.Value.TotalSeconds : null, EstimatedBytesCopied = value.EstimatedBytesCopied, TotalBytes = value.TotalBytes };
            }
            internal CopyProgress ToProgress() { return new CopyProgress { IsScanning = IsScanning, IsComplete = IsComplete, Percent = Percent, Remaining = RemainingSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(RemainingSeconds.Value) : null, EstimatedBytesCopied = EstimatedBytesCopied, TotalBytes = TotalBytes }; }
        }

        sealed class WorkerOutput
        {
            readonly object sync = new object();
            readonly Queue<string> messages = new Queue<string>();
            readonly Stopwatch clock = Stopwatch.StartNew();
            double lastActivity, lastScanDelivery, lastDelivery;
            string pendingScan;
            string lastScan = "Preparing copy worker";
            bool scanning = true, speedChanged, statePending, omitted;
            double? pendingSpeed;
            ProgressPacket pendingProgress, completion;
            public CopyResult Result, State;
            public string Error, StandardError = "";
            public bool Closed;
            public string LastScan { get { lock (sync) return lastScan; } }
            public TimeSpan MetadataIdle { get { lock (sync) return scanning ? TimeSpan.FromSeconds(clock.Elapsed.TotalSeconds - lastActivity) : TimeSpan.Zero; } }
            public void Accept(string line)
            {
                lock (sync)
                {
                    if (line == null) { Closed = true; return; }
                    try
                    {
                        WorkerEvent item = Deserialize<WorkerEvent>(line);
                        switch (item.Kind)
                        {
                            case "message":
                                if (item.Text.StartsWith("Scan: ", StringComparison.Ordinal) || item.Text.StartsWith("Estimating copy size:", StringComparison.Ordinal))
                                { scanning = true; lastActivity = clock.Elapsed.TotalSeconds; lastScan = pendingScan = item.Text; }
                                else
                                {
                                    if (item.Text == "Scan complete." || item.Text.StartsWith("Robocopy running:", StringComparison.Ordinal)) { scanning = false; pendingScan = null; }
                                    if (messages.Count >= 256) { messages.Dequeue(); omitted = true; }
                                    messages.Enqueue(item.Text);
                                }
                                break;
                            case "speed": pendingSpeed = item.Speed; speedChanged = true; break;
                            case "progress": if (item.Progress != null && item.Progress.IsComplete) completion = item.Progress; else pendingProgress = item.Progress; break;
                            case "state": State = item.Result; statePending = true; break;
                            case "result": Result = item.Result; break;
                            case "error": Error = item.Text; break;
                            default: Error = "The copy worker sent an unrecognized event."; break;
                        }
                    }
                    catch (Exception error) { Error = "Cannot read copy worker output: " + error.Message; }
                }
            }
            public void ErrorText(string text) { lock (sync) StandardError = (StandardError + " " + text).Substring(Math.Max(0, StandardError.Length + text.Length + 1 - 4000)); }
            public void DeliverCompletion(Action<CopyProgress> details) { if (details != null && completion != null) details(completion.ToProgress()); }
            public void Deliver(Action<string> progress, Action<double?> speed, Action<CopyProgress> details, Action<CopyResult> stateChanged, bool force = false)
            {
                var text = new List<string>();
                bool reportSpeed;
                double? rate;
                ProgressPacket state;
                CopyResult copyState;
                lock (sync)
                {
                    if (!force && clock.Elapsed.TotalSeconds - lastDelivery < 0.25) return;
                    lastDelivery = clock.Elapsed.TotalSeconds;
                    if (omitted) { text.Add("Additional activity messages were omitted from the live view."); omitted = false; }
                    while (messages.Count > 0 && (force || text.Count < 16)) text.Add(messages.Dequeue());
                    if (pendingScan != null && clock.Elapsed.TotalSeconds - lastScanDelivery >= 0.25)
                    { text.Add(pendingScan); pendingScan = null; lastScanDelivery = clock.Elapsed.TotalSeconds; }
                    reportSpeed = speedChanged; rate = pendingSpeed; speedChanged = false;
                    state = pendingProgress; pendingProgress = null;
                    copyState = statePending ? State : null; statePending = false;
                }
                if (progress != null) foreach (string message in text) progress(message);
                if (speed != null && reportSpeed) speed(rate);
                if (details != null && state != null) details(state.ToProgress());
                if (stateChanged != null && copyState != null) stateChanged(copyState);
            }
        }

        sealed class OwnedJob : IDisposable
        {
            readonly JobHandle handle;
            public bool Assigned { get; private set; }
            public OwnedJob()
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // Kill only this worker and descendants if the GUI closes.
                if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits)))
                { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            }
            public void Assign(Process process)
            {
                if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot contain the copy worker; no copy was started.");
                Assigned = true;
            }
            public uint ActiveProcesses
            {
                get
                {
                    Accounting accounting;
                    if (!QueryInformationJobObject(handle, 1, out accounting, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return accounting.ActiveProcesses;
                }
            }
            public void Stop()
            {
                if (!TerminateJobObject(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop the owned copy worker. Check Task Manager before disconnecting the drive.");
                var waiting = Stopwatch.StartNew();
                while (ActiveProcesses != 0)
                {
                    if (waiting.ElapsedMilliseconds >= 5000) throw new IOException("The copy worker has not stopped. Check Task Manager before disconnecting the drive.");
                    Thread.Sleep(25);
                }
            }
            public void Dispose() { handle.Dispose(); }
        }

        sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public JobHandle() : base(true) { }
            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential)]
        struct Accounting { public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern JobHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(JobHandle job, int type, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(JobHandle job, int type, out Accounting information, uint length, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(JobHandle job, uint exitCode);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }
}
