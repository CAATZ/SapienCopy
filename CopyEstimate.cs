using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SapienCopy
{
    public sealed class CopyProgress
    {
        public bool IsScanning, IsComplete;
        public double? Percent;
        public TimeSpan? Remaining;
        public double EstimatedBytesCopied, TotalBytes;
    }

    internal static class CopyEstimate
    {
        private static readonly Regex FileRow = new Regex(@"^\s*([0-9]+)\s+(.+)$", RegexOptions.CultureInvariant);

        internal static long Scan(CopyRequest request, FolderResult folder, Action<string> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (Process process = new Process())
            {
                CopyEngine.OutputTail output = new CopyEngine.OutputTail();
                try
                {
                    CopyEngine.CheckHelperFile(folder.ScanLogPath);
                    if (folder.JobPath != null && folder.FilterArguments.IndexOf(" /JOB:", StringComparison.OrdinalIgnoreCase) >= 0) CopyEngine.CheckHelperFile(folder.JobPath);
                    CopyEngine.ReportScan(progress, "Sizing", 0, folder.Source);
                    process.StartInfo = new ProcessStartInfo {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"),
                        Arguments = CopyEngine.BuildArguments(request, folder, true),
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { output.Add(e.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { output.Add(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    ulong? lastActivity = CopyEngine.ProcessActivity(process);
                    long lastLogLength = LogLength(folder.ScanLogPath);
                    Stopwatch activityClock = Stopwatch.StartNew();
                    if (progress != null) progress("Estimating copy size: " + folder.Source);
                    while (!process.WaitForExit(100))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (activityClock.ElapsedMilliseconds < 1000) continue;
                        ulong? activity = CopyEngine.ProcessActivity(process);
                        long logLength = LogLength(folder.ScanLogPath);
                        if ((activity.HasValue && activity != lastActivity) || (logLength >= 0 && logLength != lastLogLength))
                            CopyEngine.ReportScan(progress, "Sizing", 0, folder.Source);
                        lastActivity = activity; lastLogLength = logLength; activityClock.Restart();
                    }
                    process.WaitForExit();
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode < 0 || process.ExitCode >= 8)
                        throw new IOException("Robocopy sizing returned " + process.ExitCode + " for " + folder.Source + "." +
                            (request.SaveLogs && File.Exists(folder.ScanLogPath) ? " See " + folder.ScanLogPath + "." : "") +
                            (output.ToString().Length == 0 ? " No error details were returned." : Environment.NewLine + output));
                    CopyEngine.ReportScan(progress, "Sizing result", 0, folder.Source);
                    using (StreamReader reader = new StreamReader(folder.ScanLogPath, Encoding.Unicode, true))
                        return Parse(reader, folder.Source, folder.JobPath ?? Path.ChangeExtension(folder.LogPath ?? folder.ScanLogPath, ".rcj"), cancellation, progress, request.Bindings);
                }
                finally { CopyEngine.Stop(process); }
            }
        }

        private static long LogLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (IOException) { return -1; }
            catch (UnauthorizedAccessException) { return -1; }
        }

        internal static long Parse(TextReader reader, string source, string jobPath, CancellationToken cancellation, Action<string> progress = null, RobocopyBindings bindings = null)
        {
            long total = 0;
            CopyEngine.ScanReporter scan = new CopyEngine.ScanReporter(progress, "Sizing result");
            scan.Folder(source);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (bindings != null) line = bindings.ToVolumePath(line);
                cancellation.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(line)) continue;
                Match row = FileRow.Match(line);
                if (!row.Success)
                {
                    // /JOB prints its filename even with /NJH; identify the filename, not the translated label.
                    if (!String.IsNullOrEmpty(jobPath) && line.TrimEnd().EndsWith(jobPath, StringComparison.OrdinalIgnoreCase)) continue;
                    throw new InvalidDataException("The sizing log contains an unexpected row for " + source + ": " +
                        (line.Length > 240 ? line.Substring(0, 240) + "..." : line));
                }
                long bytes;
                if (!Int64.TryParse(row.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bytes))
                    throw new InvalidDataException("A file size in the sizing log is invalid.");
                string path = row.Groups[2].Value;
                if (!source.StartsWith(@"\\?\", StringComparison.Ordinal) && path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
                if (!CopyEngine.IsSameOrChild(path, source))
                    throw new InvalidDataException("A sizing row is outside the selected source folder.");
                total = checked(total + bytes);
                scan.Entry();
            }
            scan.Complete();
            return total;
        }
    }

    internal sealed class CopyEstimateProgress
    {
        private readonly bool enabled;
        private readonly Action<CopyProgress> report;
        private readonly Stopwatch sinceReport = Stopwatch.StartNew();
        private double? total;
        private double finishedBytes, currentBytes, folderBytes;
        private double? recentSpeed;
        private readonly Stopwatch sinceSpeed = new Stopwatch();

        internal CopyEstimateProgress(bool enabled, Action<CopyProgress> report)
        {
            this.enabled = enabled;
            this.report = report;
            if (report != null) report(new CopyProgress { IsScanning = enabled });
        }

        internal void Ready(double? bytes)
        {
            total = bytes;
            Emit(false, true);
        }

        internal void BeginFolder(double bytes)
        {
            folderBytes = bytes;
            currentBytes = 0;
        }

        internal void Sample(ulong processBytes, double? bytesPerSecond)
        {
            if (!enabled || !total.HasValue) return;
            // Process I/O includes logs/retries; confine this estimate to the current folder's planned data.
            currentBytes = Math.Max(currentBytes, Math.Min(processBytes, folderBytes * 0.995));
            recentSpeed = bytesPerSecond;
            sinceSpeed.Restart();
            Emit(false, false);
        }

        internal void EndFolder(bool completed)
        {
            finishedBytes += completed ? folderBytes : currentBytes;
            currentBytes = 0;
            folderBytes = 0;
            Emit(false, false);
        }

        internal void Finish(bool complete) { Emit(complete, true); }

        private void Emit(bool complete, bool force)
        {
            if (report == null || (!force && sinceReport.ElapsedMilliseconds < 1000)) return;
            sinceReport.Restart();
            CopyProgress state = new CopyProgress { IsComplete = complete, TotalBytes = total ?? 0 };
            if (enabled && total.HasValue)
            {
                state.EstimatedBytesCopied = Math.Min(finishedBytes + currentBytes, total.Value * 0.995);
                state.Percent = total.Value > 0 ? 100 * state.EstimatedBytesCopied / total.Value : 0;
                // Cached folders can finish between UI updates; retain a recent rate across those batch boundaries.
                if (recentSpeed.HasValue && recentSpeed.Value > 0 && sinceSpeed.ElapsedMilliseconds < 5000 && state.EstimatedBytesCopied < total.Value)
                {
                    double seconds = (total.Value - state.EstimatedBytesCopied) / recentSpeed.Value;
                    if (!Double.IsInfinity(seconds) && !Double.IsNaN(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
                        state.Remaining = TimeSpan.FromSeconds(seconds);
                }
            }
            if (complete && enabled)
            {
                state.Percent = 100;
                state.EstimatedBytesCopied = total ?? 0;
                state.Remaining = TimeSpan.Zero;
            }
            report(state);
        }
    }
}
