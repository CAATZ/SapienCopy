using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SapienCopy
{
    public sealed class CopyRequest
    {
        public string[] Sources;
        public string Destination;
        public int Threads = 32;
        public bool BackupMode;
        public bool UpdateExisting;
        public bool DirectCopy;
        public bool SaveLogs = true;
        public string DestinationFileSystem { get; internal set; }
        public OneDriveMode OneDriveMode = OneDriveMode.All;
        public bool EstimateProgress = true;
        public string[] ExcludedFolders = new string[0];
        public CopySession Session;
        internal string WorkerTemporaryFolder;
        internal RobocopyBindings Bindings;
    }

    public sealed class CopySession
    {
        public VolumePath[] Sources, ExcludedFolders, OneDriveRoots;
        public VolumePath Destination, RunFolder, ReportFolder;
        public string[] OutputRelativePaths;
        public string DisplayDestination, DisplayRunFolder;
        public Dictionary<string, string> DisplayRoots;
        public bool OutputCreated;
        public bool DirectCopy, UpdateExisting, SaveLogs;
        public OneDriveMode OneDriveMode;
    }

    public sealed class FolderResult
    {
        public string Source, Destination, LogPath, Status, Error;
        public int? ExitCode;
        internal string FilterArguments = "";
        internal string ScanLogPath;
        internal long EstimatedBytes;
        internal string IncludeArguments = "", JobPath;
        internal bool FlatCopy, AppendLog, FileCopy, RootFiles;
        internal List<FolderResult> Batches;
    }

    public sealed class CopyResult
    {
        public string RunFolder, ReportFolder, Status;
        public readonly List<FolderResult> Folders = new List<FolderResult>();
        public int CopiedCount, WarningCount, FailedCount;
        public int SkippedCount, ExcludedCount;
        public bool Cancelled;
        public bool WorkerStopped;
        public CopySession Session;
        public bool SaveLogs;
        public OneDriveMode OneDriveMode;
        public bool EstimateRequested;
        public double? EstimatedTotalBytes;
        public string EstimateError;
        public string CleanupWarning;
        public string DestinationFileSystem;
        public int CompatibilityWarningCount;
        internal readonly List<ExcludedPath> Exclusions = new List<ExcludedPath>();
        internal readonly List<ExcludedPath> MetadataWarnings = new List<ExcludedPath>();
    }

    public static class CopyEngine
    {
        internal static Action<CopyResult> StateChanged;

        public static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static CopyRequest Validate(CopyRequest request, Action<string> progress = null)
        {
            if (request == null || request.Sources == null || request.Sources.Length == 0)
                throw new InvalidOperationException("Add at least one source file or folder.");
            if (request.Threads < 1 || request.Threads > 128)
                throw new InvalidOperationException("Copy threads must be between 1 and 128.");
            if (request.DirectCopy && request.UpdateExisting)
                throw new InvalidOperationException("Choose either Direct copy or Update existing backup, not both.");
            if (!Enum.IsDefined(typeof(OneDriveMode), request.OneDriveMode))
                throw new InvalidOperationException("Choose a valid OneDrive copy option.");
            if (request.BackupMode && !IsAdministrator())
                throw new InvalidOperationException("Protected-file copying requires running this app as administrator. Uncheck that option for a normal copy.");

            ReportScan(progress, "Validate destination", 0, request.Destination);
            string destination = ExistingLocalDirectory(request.Destination);
            ReportScan(progress, "Resolve destination", 0, destination);
            string destinationTarget = CanonicalDirectory(destination);
            ReportScan(progress, "Destination filesystem", 0, destination);
            string fileSystem = FileSystemFor(destination);
            DestinationPolicy.ValidateFileSystem(fileSystem, request.BackupMode);

            List<string> sources = new List<string>();
            List<string> targets = new List<string>();
            List<string> entries = new List<string>();
            foreach (string selected in request.Sources)
            {
                ReportScan(progress, "Validate source", 0, selected);
                string source = ExistingLocalPath(selected, false);
                if (request.DirectCopy && source.Length == 3)
                    throw new InvalidOperationException("Direct copy needs a named source folder. Select folders inside the drive, or choose a backup mode for a whole drive.");
                ReportScan(progress, "Resolve source", 0, source);
                string target = CanonicalDirectory(source);
                string entry = CanonicalEntry(source);
                if (IsSameOrChild(destinationTarget, target))
                    throw new InvalidOperationException("The destination cannot be the source folder or a folder inside it, including through a link: " + source);
                bool included = false;
                for (int i = targets.Count - 1; i >= 0; i--)
                {
                    if (IsSameOrChild(entry, entries[i]))
                    {
                        included = true;
                        break;
                    }
                    if (IsSameOrChild(entries[i], entry))
                    {
                        targets.RemoveAt(i);
                        entries.RemoveAt(i);
                        sources.RemoveAt(i);
                    }
                }
                if (!included)
                {
                    sources.Add(source);
                    targets.Add(target);
                    entries.Add(entry);
                }
            }
            if (request.DirectCopy || request.UpdateExisting || request.Session != null)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string source in sources)
                {
                    if (request.DirectCopy && !names.Add(Path.GetFileName(source)))
                        throw new InvalidOperationException("Direct copy cannot combine two sources named " + Path.GetFileName(source) + ". Copy them separately or use a backup mode.");
                    string output = OutputFor(request, source);
                    ReportScan(progress, "Resolve copy output", 0, output);
                    string outputTarget = ExistingAttributes(output).HasValue ? CanonicalDirectory(output) : destinationTarget.TrimEnd('\\') + "\\" + output.Substring(destination.TrimEnd('\\').Length).TrimStart('\\');
                    foreach (string target in targets)
                        if (IsSameOrChild(outputTarget, target) || IsSameOrChild(target, outputTarget))
                            throw new InvalidOperationException("The copy output overlaps a selected source: " + output + ". Choose a different destination.");
                }
            }
            List<string> exclusions = new List<string>();
            List<string> exclusionTargets = new List<string>();
            foreach (string selected in request.ExcludedFolders ?? new string[0])
            {
                ReportScan(progress, "Validate excluded folder", 0, selected);
                string excluded = ExistingLocalDirectory(selected);
                ReportScan(progress, "Resolve excluded folder", 0, excluded);
                string target = CanonicalDirectory(excluded);
                bool covered = false;
                for (int i = 0; i < exclusions.Count; i++)
                    if (IsSameOrChild(excluded, exclusions[i]) && IsSameOrChild(target, exclusionTargets[i])) { covered = true; break; }
                if (covered) continue;
                for (int i = exclusionTargets.Count - 1; i >= 0; i--)
                    if (IsSameOrChild(exclusions[i], excluded) && IsSameOrChild(exclusionTargets[i], target)) { exclusionTargets.RemoveAt(i); exclusions.RemoveAt(i); }
                exclusions.Add(excluded); exclusionTargets.Add(target);
            }
            // Distinct alias spellings stay visible because excluding a junction also excludes the link itself.
            return new CopyRequest { Sources = sources.ToArray(), Destination = destination, Threads = request.Threads, BackupMode = request.BackupMode, UpdateExisting = request.UpdateExisting, DirectCopy = request.DirectCopy, SaveLogs = request.SaveLogs, DestinationFileSystem = fileSystem, OneDriveMode = request.OneDriveMode, EstimateProgress = request.EstimateProgress, ExcludedFolders = exclusions.ToArray(), WorkerTemporaryFolder = request.WorkerTemporaryFolder, Session = request.Session, Bindings = request.Bindings };
        }

        public static CopyResult Run(CopyRequest request, Action<string> progress, CancellationToken cancellation, Action<double?> speed = null, Action<CopyProgress> progressDetails = null)
        {
            return RunForTests(request, progress, cancellation, null, speed, progressDetails);
        }

        internal static CopyResult RunForTests(CopyRequest request, Action<string> progress, CancellationToken cancellation, IEnumerable<string> oneDriveRoots, Action<double?> speed = null, Action<CopyProgress> progressDetails = null, string destinationFileSystem = null)
        {
            if (speed != null) speed(null);
            if (request.Session == null)
            {
                request = BeginSession(Validate(request, progress));
                try { CaptureOneDriveRoots(request, oneDriveRoots, progress, cancellation); }
                catch (OperationCanceledException) { return new CopyResult { Status = "Cancelled", Cancelled = true, SaveLogs = request.SaveLogs }; }
            }
            request = Validate(BindSession(request), progress);
            if (StateChanged != null) StateChanged(new CopyResult { Status = "Preparing", Session = request.Session, SaveLogs = request.SaveLogs, RunFolder = VolumeIdentity.BoundPath(request.Session.RunFolder) });
            if (destinationFileSystem != null)
            {
                DestinationPolicy.ValidateFileSystem(destinationFileSystem, request.BackupMode);
                request.DestinationFileSystem = destinationFileSystem;
            }
            string temporary = null;
            RobocopyBindings bindings = null;
            CopyResult result = null;
            try
            {
                List<VolumePath> volumes = new List<VolumePath>(request.Session.Sources);
                volumes.Add(request.Session.Destination);
                bindings = new RobocopyBindings(volumes, request.WorkerTemporaryFolder);
                request.Bindings = bindings;
                if (!request.SaveLogs && !cancellation.IsCancellationRequested) temporary = CreateTemporaryFolder(request, progress);
                result = RunCore(request, progress, cancellation, speed, progressDetails, temporary);
                return result;
            }
            finally
            {
                if (bindings != null)
                {
                    try { bindings.Dispose(); }
                    catch (Exception error)
                    {
                        string warning = "Temporary volume paths could not be removed: " + error.Message;
                        if (result != null) result.CleanupWarning = warning;
                        Notify(progress, "Cleanup warning: " + warning);
                    }
                }
                if (temporary != null)
                {
                    try { DeleteTemporaryFolder(temporary); }
                    catch (Exception error)
                    {
                        string warning = "Temporary transfer files could not be removed: " + temporary + ". " + error.Message;
                        if (result != null) result.CleanupWarning = warning;
                        Notify(progress, "Cleanup warning: " + warning);
                    }
                }
            }
        }

        private static CopyResult RunCore(CopyRequest request, Action<string> progress, CancellationToken cancellation, Action<double?> speed, Action<CopyProgress> progressDetails, string temporary)
        {
            CopyEstimateProgress estimate = new CopyEstimateProgress(request.EstimateProgress, progressDetails);
            if (cancellation.IsCancellationRequested)
            {
                estimate.Finish(false);
                return new CopyResult { Status = "Cancelled", Cancelled = true, SaveLogs = request.SaveLogs, Session = request.Session };
            }

            Dictionary<string, OneDrivePlan> plans;
            List<ExcludedPath> metadataWarnings;
            try
            {
                List<string> roots = new List<string>();
                foreach (VolumePath root in request.Session.OneDriveRoots) roots.Add(VolumeIdentity.Verify(root));
                if (request.OneDriveMode != OneDriveMode.All) Notify(progress, "Detected OneDrive roots: " + roots.Count);
                plans = OneDrivePolicy.Build(request, roots, progress, cancellation);
                if (request.Bindings != null) ExcludeTemporaryFolder(request, plans, request.Bindings.DirectoryPath, progress);
                if (temporary != null) ExcludeTemporaryFolder(request, plans, temporary, progress);
                if (request.WorkerTemporaryFolder != null) ExcludeTemporaryFolder(request, plans, request.WorkerTemporaryFolder, progress);
                metadataWarnings = DestinationPolicy.Check(request, plans, progress, cancellation);
                CheckUpdateTargets(request, plans, progress, cancellation);
            }
            catch (OperationCanceledException) { estimate.Finish(false); return new CopyResult { Status = "Cancelled", Cancelled = true, SaveLogs = request.SaveLogs, Session = request.Session }; }
            string runFolder = VolumeIdentity.Verify(request.Session.RunFolder, request.Session.OutputCreated, true);
            Directory.CreateDirectory(runFolder);
            string reportFolder = request.SaveLogs ? VolumeIdentity.Verify(request.Session.ReportFolder, request.Session.OutputCreated, true) : null;
            CopyResult result = new CopyResult { RunFolder = runFolder, ReportFolder = reportFolder, Status = "Running", OneDriveMode = request.OneDriveMode, EstimateRequested = request.EstimateProgress, SaveLogs = request.SaveLogs, DestinationFileSystem = request.DestinationFileSystem, Session = request.Session };
            result.MetadataWarnings.AddRange(metadataWarnings);
            result.CompatibilityWarningCount = metadataWarnings.Count;
            string logs = request.SaveLogs ? Path.Combine(reportFolder, "Logs") : temporary;
            if (request.SaveLogs) VolumeIdentity.Verify(VolumeIdentity.Child(request.Session.ReportFolder, "Logs", true), request.Session.OutputCreated, true);
            Directory.CreateDirectory(logs);
            request.Session.RunFolder = VolumeIdentity.Capture(runFolder, true);
            if (request.SaveLogs) request.Session.ReportFolder = VolumeIdentity.Capture(reportFolder, true);
            request.Session.OutputCreated = true;
            for (int i = 0; i < request.Sources.Length; i++)
            {
                OneDrivePlan plan = plans[request.Sources[i]];
                result.Exclusions.AddRange(plan.Paths);
                result.Folders.Add(new FolderResult {
                    Source = request.Sources[i], Destination = OutputFor(request, request.Sources[i]), FileCopy = !IsDirectory(request.Sources[i]),
                    LogPath = request.SaveLogs ? Path.Combine(logs, (i + 1).ToString("D3", CultureInfo.InvariantCulture) + ".log") : null, Status = plan.SkipSource ? "Skipped" : "NotStarted"
                });
                result.Folders[i].ScanLogPath = Path.Combine(logs, (i + 1).ToString("D3", CultureInfo.InvariantCulture) + "-scan.log");
                if (plan.SkipSource) result.SkippedCount++;
            }
            result.ExcludedCount = result.Exclusions.Count;
            WriteReport(result);
            Notify(progress, (request.DirectCopy ? "Copy folder: " : "Backup folder: ") + runFolder);
            foreach (FolderResult folder in result.Folders)
            {
                try
                {
                    OneDrivePlan plan = plans[folder.Source];
                    folder.Batches = PrepareBatches(request, folder, plan, progress);
                    foreach (FolderResult batch in folder.Batches)
                    {
                        if (request.SaveLogs) CheckHelperFile(batch.LogPath);
                        if (request.EstimateProgress) CheckHelperFile(batch.ScanLogPath);
                    }
                }
                catch (Exception error)
                {
                    folder.Status = "Failed"; folder.Error = error.Message; result.FailedCount++; result.Status = "Failed";
                    WriteReport(result);
                    Notify(progress, "Failed before copying: " + error.Message);
                    estimate.Finish(false);
                    return result;
                }
            }

            if (request.EstimateProgress)
            {
                try
                {
                    long total = 0;
                    foreach (FolderResult folder in result.Folders)
                    {
                        if (folder.Status == "Skipped") continue;
                        long folderBytes = 0;
                        foreach (FolderResult batch in folder.Batches)
                        {
                            batch.EstimatedBytes = CopyEstimate.Scan(request, batch, progress, cancellation);
                            folderBytes = checked(folderBytes + batch.EstimatedBytes);
                        }
                        folder.EstimatedBytes = folderBytes;
                        total = checked(total + folder.EstimatedBytes);
                    }
                    result.EstimatedTotalBytes = total;
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = true; result.Status = "Cancelled";
                    WriteReport(result); estimate.Finish(false);
                    Notify(progress, "Cancelled during sizing. No source files were copied.");
                    return result;
                }
                catch (Exception error)
                {
                    result.EstimateError = error.Message;
                    Notify(progress, "Progress estimate unavailable: " + error.Message + " Copying will continue.");
                }
            }
            estimate.Ready(result.EstimatedTotalBytes);
            Notify(progress, "Scan complete.");

            foreach (FolderResult folder in result.Folders)
            {
                if (folder.Status == "Skipped") { Notify(progress, "Skipped by exclusions: " + folder.Source); continue; }
                if (cancellation.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }
                Notify(progress, "Copying: " + folder.Source);
                folder.Status = "Running";
                WriteReport(result);
                bool failed = false, warned = false;
                foreach (FolderResult batch in folder.Batches)
                {
                    if (cancellation.IsCancellationRequested) { result.Cancelled = true; break; }
                    estimate.BeginFolder(batch.EstimatedBytes);
                    RunBatch(request, batch, progress, cancellation, speed, estimate);
                    estimate.EndFolder(batch.Status == "Completed");
                    failed |= batch.Status == "Failed"; warned |= batch.Status == "Warning";
                    if (batch.ExitCode.HasValue) folder.ExitCode = (folder.ExitCode ?? 0) | batch.ExitCode.Value;
                    if (!String.IsNullOrEmpty(batch.Error) && !Object.ReferenceEquals(batch, folder))
                    {
                        folder.Error = (folder.Error ?? "") + batch.Source + ": " + batch.Error + Environment.NewLine;
                        if (folder.Error.Length > 16000) folder.Error = folder.Error.Substring(folder.Error.Length - 16000);
                    }
                    if (batch.Status == "Cancelled") { result.Cancelled = true; break; }
                    folder.Status = "Running";
                    if (StateChanged != null) StateChanged(result);
                }
                folder.Status = result.Cancelled ? "Cancelled" : failed ? "Failed" : warned ? "Warning" : "Completed";
                if (speed != null) speed(null);
                if (folder.Status == "Completed") result.CopiedCount++;
                else if (folder.Status == "Warning") result.WarningCount++;
                else if (folder.Status == "Failed") result.FailedCount++;
                Notify(progress, folder.Status + ": " + folder.Source + (folder.ExitCode.HasValue ? " (Robocopy " + folder.ExitCode.Value + ")" : "") + (String.IsNullOrEmpty(folder.Error) ? "" : " — " + folder.Error));
                WriteReport(result);
                if (result.Cancelled) break;
            }
            result.Cancelled = result.Cancelled || cancellation.IsCancellationRequested;
            result.Status = result.Cancelled ? "Cancelled" : result.FailedCount > 0 ? "Failed" : result.WarningCount > 0 || result.CompatibilityWarningCount > 0 ? "CompletedWithWarnings" : "Completed";
            WriteReport(result);
            estimate.Finish(result.Status == "Completed");
            Notify(progress, result.Status + (result.SaveLogs ? ". Reports: " + result.ReportFolder : ". Logging was disabled."));
            return result;
        }

        private static List<FolderResult> PrepareBatches(CopyRequest request, FolderResult folder, OneDrivePlan plan, Action<string> progress)
        {
            ReportScan(progress, "Prepare copy selection", 0, folder.Source);
            List<FolderResult> batches = new List<FolderResult>();
            FolderResult recursiveBatch = null;
            if (plan.SkipSource) return batches;
            if (!folder.RootFiles && IsVolumeRoot(folder.Source)) return PrepareVolumeBatches(request, folder, plan, progress);
            if (folder.FileCopy)
            {
                KeyValuePair<string, string> selection = ExactFileArguments(Path.GetFileName(folder.Source));
                folder.IncludeArguments = selection.Key;
                folder.FilterArguments = selection.Value + (plan.IsCachedPath(folder.Source) ? " /XA:O" : "");
                batches.Add(folder);
                return batches;
            }
            if (plan.CachedScopes.Count == 0 || !plan.OneDriveOnly)
            {
                OneDrivePlan recursive = new OneDrivePlan();
                foreach (ExcludedPath excluded in plan.Paths) recursive.AddExcluded(excluded);
                foreach (string scope in plan.CachedScopes) recursive.AddExcluded(new ExcludedPath { Path = scope, Directory = true, Reason = "Handled by cached directory batches" });
                folder.JobPath = Path.ChangeExtension(folder.LogPath ?? folder.ScanLogPath, ".rcj");
                recursive.PrepareArguments(folder.JobPath, request.Bindings == null ? (Func<string, string>)null : request.Bindings.ToRobocopy);
                folder.FilterArguments = recursive.Arguments;
                recursiveBatch = new FolderResult { Source = folder.Source, Destination = folder.Destination, LogPath = folder.LogPath, ScanLogPath = folder.ScanLogPath, JobPath = folder.JobPath, FilterArguments = folder.FilterArguments, RootFiles = folder.RootFiles, Status = "NotStarted" };
            }
            // Finish descendants before parents so /DCOPY:T restores times after child creation.
            for (int index = plan.CachedDirectories.Count - 1; index >= 0; index--)
            {
                CachedDirectory directory = plan.CachedDirectories[index];
                if (plan.IsExcluded(directory.Path)) continue;
                List<KeyValuePair<string, string>> chunks = new List<KeyValuePair<string, string>>();
                StringBuilder names = new StringBuilder();
                foreach (string name in directory.Files)
                {
                    if (plan.IsExcluded(Path.Combine(directory.Path, name))) continue;
                    KeyValuePair<string, string> selection = ExactFileArguments(name);
                    if (selection.Value.Length > 0)
                    {
                        if (names.Length > 0) { chunks.Add(new KeyValuePair<string, string>(names.ToString(), "")); names.Clear(); }
                        chunks.Add(selection);
                        continue;
                    }
                    if (names.Length + selection.Key.Length > 18000 && names.Length > 0) { chunks.Add(new KeyValuePair<string, string>(names.ToString(), "")); names.Clear(); }
                    names.Append(selection.Key);
                }
                if (names.Length > 0 || chunks.Count == 0) chunks.Add(new KeyValuePair<string, string>(names.ToString(), ""));
                foreach (KeyValuePair<string, string> chunk in chunks)
                {
                    string suffix = directory.Path.Substring(folder.Source.TrimEnd('\\').Length).TrimStart('\\');
                    batches.Add(new FolderResult {
                        Source = directory.Path, Destination = Path.Combine(folder.Destination, suffix), LogPath = folder.LogPath,
                        ScanLogPath = Path.ChangeExtension(folder.ScanLogPath, null) + "-" + batches.Count.ToString("D4", CultureInfo.InvariantCulture) + ".log",
                        Status = "NotStarted", FlatCopy = true, RootFiles = folder.RootFiles, IncludeArguments = chunk.Key,
                        FilterArguments = (chunk.Key.Length == 0 ? " /XF *" : " /XA:O") + chunk.Value, AppendLog = batches.Count > 0
                    });
                }
            }
            if (recursiveBatch != null) { recursiveBatch.AppendLog = batches.Count > 0; batches.Add(recursiveBatch); }
            return batches;
        }

        private static List<FolderResult> PrepareVolumeBatches(CopyRequest request, FolderResult folder, OneDrivePlan plan, Action<string> progress)
        {
            List<FolderResult> batches = new List<FolderResult>();
            // /SJ would copy our volume-root junction itself; copy child trees and root files separately.
            foreach (OneDrivePolicy.Entry entry in OneDrivePolicy.Enumerate(folder.Source, plan.IsCachedPath(folder.Source)))
            {
                if ((entry.Attributes & 16) == 0) continue;
                string child = Path.Combine(folder.Source, entry.Name);
                if (!plan.IsSelected(child)) continue;
                FolderResult selected = new FolderResult { Source = child, Destination = Path.Combine(folder.Destination, entry.Name), LogPath = folder.LogPath,
                    ScanLogPath = Path.ChangeExtension(folder.ScanLogPath, null) + "-root-" + batches.Count.ToString("D4", CultureInfo.InvariantCulture) + ".log" };
                batches.AddRange(PrepareBatches(request, selected, ScopePlan(plan, child, true), progress));
            }
            FolderResult rootFiles = new FolderResult { Source = folder.Source, Destination = folder.Destination, LogPath = folder.LogPath,
                ScanLogPath = folder.ScanLogPath, RootFiles = true };
            batches.AddRange(PrepareBatches(request, rootFiles, ScopePlan(plan, folder.Source, false), progress));
            for (int i = 0; i < batches.Count; i++) batches[i].AppendLog = i > 0;
            return batches;
        }

        private static OneDrivePlan ScopePlan(OneDrivePlan original, string source, bool recursive)
        {
            OneDrivePlan plan = new OneDrivePlan();
            foreach (ExcludedPath excluded in original.Paths) plan.AddExcluded(excluded);
            foreach (string scope in original.CachedScopes)
            {
                if (IsSameOrChild(source, scope)) { plan.CachedScopes.Add(source); plan.OneDriveOnly = true; }
                else if (recursive && IsSameOrChild(scope, source)) plan.CachedScopes.Add(scope);
            }
            foreach (CachedDirectory directory in original.CachedDirectories)
                if (String.Equals(source, directory.Path, StringComparison.OrdinalIgnoreCase) || (recursive && IsSameOrChild(directory.Path, source))) plan.CachedDirectories.Add(directory);
            return plan;
        }

        internal static bool IsVolumeRoot(string path)
        {
            return path.Length == 3 && path[1] == ':' || path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && path.IndexOf('}') == path.TrimEnd('\\').Length - 1;
        }

        private static KeyValuePair<string, string> ExactFileArguments(string name)
        {
            // Robocopy treats even quoted leading '-' as a switch; exclude every nonempty prefix from the safe mask.
            return name.StartsWith("-", StringComparison.Ordinal)
                ? new KeyValuePair<string, string>(" " + Quote("*" + name), " /XF " + Quote("?*" + name))
                : new KeyValuePair<string, string>(" " + Quote(name), "");
        }

        private static void RunBatch(CopyRequest request, FolderResult folder, Action<string> progress, CancellationToken cancellation, Action<double?> speed, CopyEstimateProgress estimate)
        {
            folder.Status = "Running";
            using (Process process = new Process())
            {
                OutputTail output = new OutputTail();
                try
                {
                    if (request.SaveLogs) CheckHelperFile(folder.LogPath);
                    if (folder.JobPath != null && folder.FilterArguments.IndexOf(" /JOB:", StringComparison.OrdinalIgnoreCase) >= 0) CheckHelperFile(folder.JobPath);
                    process.StartInfo = new ProcessStartInfo {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"),
                        Arguments = BuildArguments(request, folder), UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { output.Add(e.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { output.Add(e.Data); };
                    process.Start();
                    ProcessWriteSpeed meter = new ProcessWriteSpeed();
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    Notify(progress, "Robocopy running: " + folder.Source);
                    while (!process.WaitForExit(100))
                    {
                        meter.Sample(process, speed, estimate.Sample, false);
                        if (!cancellation.IsCancellationRequested) continue;
                        folder.Status = "Cancelled"; Stop(process); break;
                    }
                    if (process.HasExited)
                    {
                        meter.Sample(process, speed, estimate.Sample, true);
                        process.WaitForExit(); folder.ExitCode = process.ExitCode;
                    }
                    if (folder.Status != "Cancelled") folder.Status = ClassifyExitCode(folder.ExitCode ?? 16, request.UpdateExisting || request.DirectCopy || folder.FlatCopy || folder.RootFiles);
                    if (folder.Status == "Failed" || folder.Status == "Warning") folder.Error = output.ToString();
                }
                catch (Exception error)
                {
                    folder.Status = "Failed"; folder.Error = error.Message;
                    try { Stop(process); }
                    catch (Exception stopError) { folder.Error += " Robocopy could not be stopped: " + stopError.Message; }
                }
            }
            if (speed != null) speed(null);
        }

        internal static string DestinationFor(string runFolder, string source, bool directCopy = false)
        {
            return directCopy ? Path.Combine(runFolder, Path.GetFileName(source)) : Path.Combine(runFolder, source.Substring(0, 1).ToUpperInvariant(), source.Substring(3));
        }

        private static CopyRequest BeginSession(CopyRequest request)
        {
            CopySession session = new CopySession {
                Sources = new VolumePath[request.Sources.Length], ExcludedFolders = new VolumePath[request.ExcludedFolders.Length],
                OutputRelativePaths = new string[request.Sources.Length], Destination = VolumeIdentity.Capture(request.Destination, true),
                DirectCopy = request.DirectCopy, UpdateExisting = request.UpdateExisting, SaveLogs = request.SaveLogs, OneDriveMode = request.OneDriveMode
            };
            for (int i = 0; i < request.Sources.Length; i++)
            {
                string source = request.Sources[i];
                session.Sources[i] = VolumeIdentity.Capture(source);
                session.OutputRelativePaths[i] = request.DirectCopy ? Path.GetFileName(source) : (source.Substring(0, 1).ToUpperInvariant() + "\\" + source.Substring(3)).TrimEnd('\\');
            }
            for (int i = 0; i < request.ExcludedFolders.Length; i++) session.ExcludedFolders[i] = VolumeIdentity.Capture(request.ExcludedFolders[i]);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            session.RunFolder = VolumeIdentity.Child(session.Destination, request.DirectCopy || request.UpdateExisting ? "" : "SapienCopy-" + stamp, true);
            if (request.SaveLogs) session.ReportFolder = VolumeIdentity.Child(session.RunFolder, request.DirectCopy || request.UpdateExisting ? "Reports-" + stamp : "", true);
            request.Session = session;
            return request;
        }

        private static void CaptureOneDriveRoots(CopyRequest request, IEnumerable<string> knownRoots, Action<string> progress, CancellationToken cancellation)
        {
            List<VolumePath> captured = new List<VolumePath>();
            if (request.OneDriveMode != OneDriveMode.All)
            {
                List<string> sources = new List<string>();
                foreach (VolumePath source in request.Session.Sources) sources.Add(CanonicalDirectory(VolumeIdentity.Verify(source)));
                foreach (string root in knownRoots ?? OneDrivePolicy.DiscoverRoots(progress, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    ReportScan(progress, "Remember OneDrive volume", captured.Count, root);
                    VolumePath binding = VolumeIdentity.Capture(root);
                    string target = CanonicalDirectory(VolumeIdentity.BoundPath(binding));
                    foreach (string source in sources)
                        if (IsSameOrChild(source, target) || IsSameOrChild(target, source)) { captured.Add(binding); break; }
                }
            }
            request.Session.OneDriveRoots = captured.ToArray();
        }

        private static CopyRequest BindSession(CopyRequest request)
        {
            CopySession session = request.Session;
            if (session == null || session.Sources == null || session.ExcludedFolders == null || session.OneDriveRoots == null || session.OutputRelativePaths == null ||
                session.Sources.Length == 0 || session.Sources.Length != session.OutputRelativePaths.Length ||
                session.DirectCopy != request.DirectCopy || session.UpdateExisting != request.UpdateExisting || session.SaveLogs != request.SaveLogs || session.OneDriveMode != request.OneDriveMode)
                throw new InvalidOperationException("The saved transfer settings are incomplete or changed. Start a new transfer.");
            string destination = VolumeIdentity.Verify(session.Destination, true, true);
            VolumeIdentity.Verify(session.RunFolder, session.OutputCreated, true);
            if (!session.OutputCreated && !session.RunFolder.HasFileId && Directory.Exists(VolumeIdentity.BoundPath(session.RunFolder)))
                throw new InvalidOperationException("The planned output folder already exists without a saved folder identity. Start a new transfer; the existing folder will be retained.");
            if (request.SaveLogs)
            {
                VolumeIdentity.Verify(session.ReportFolder, session.OutputCreated, true);
                if (!session.OutputCreated && !session.ReportFolder.HasFileId && Directory.Exists(VolumeIdentity.BoundPath(session.ReportFolder)))
                    throw new InvalidOperationException("The planned report folder already exists without a saved folder identity. Start a new transfer; the existing folder will be retained.");
                VolumeIdentity.Verify(VolumeIdentity.Child(session.ReportFolder, "Logs", true), session.OutputCreated, true);
            }
            string[] sources = new string[session.Sources.Length], exclusions = new string[session.ExcludedFolders.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = VolumeIdentity.Verify(session.Sources[i]);
                VolumeIdentity.Child(session.RunFolder, session.OutputRelativePaths[i], session.Sources[i].IsDirectory);
            }
            for (int i = 0; i < exclusions.Length; i++) exclusions[i] = VolumeIdentity.Verify(session.ExcludedFolders[i]);
            foreach (VolumePath root in session.OneDriveRoots) VolumeIdentity.Verify(root);
            session.DisplayDestination = VolumeIdentity.FriendlyPath(session.Destination);
            session.DisplayRunFolder = VolumeIdentity.FriendlyPath(session.RunFolder);
            session.DisplayRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<VolumePath> displayVolumes = new List<VolumePath>(session.Sources);
            displayVolumes.AddRange(session.ExcludedFolders); displayVolumes.Add(session.Destination);
            foreach (VolumePath volume in displayVolumes)
                if (!session.DisplayRoots.ContainsKey(volume.VolumeRoot))
                    session.DisplayRoots.Add(volume.VolumeRoot, VolumeIdentity.FriendlyPath(new VolumePath { VolumeRoot = volume.VolumeRoot, SerialNumber = volume.SerialNumber, FileSystem = volume.FileSystem, RelativePath = "", IsDirectory = true }));
            return new CopyRequest { Sources = sources, Destination = destination, Threads = request.Threads, BackupMode = request.BackupMode,
                UpdateExisting = request.UpdateExisting, DirectCopy = request.DirectCopy, SaveLogs = request.SaveLogs, OneDriveMode = request.OneDriveMode,
                EstimateProgress = request.EstimateProgress, ExcludedFolders = exclusions, WorkerTemporaryFolder = request.WorkerTemporaryFolder, Session = session };
        }

        private static string OutputFor(CopyRequest request, string source)
        {
            if (request.Session == null) return DestinationFor(request.Destination, source, request.DirectCopy);
            for (int i = 0; i < request.Session.Sources.Length; i++)
                if (String.Equals(source, VolumeIdentity.BoundPath(request.Session.Sources[i]), StringComparison.OrdinalIgnoreCase))
                    return VolumeIdentity.BoundPath(VolumeIdentity.Child(request.Session.RunFolder, request.Session.OutputRelativePaths[i], request.Session.Sources[i].IsDirectory));
            throw new InvalidOperationException("The selected source does not belong to this saved transfer: " + source);
        }

        private static string CreateTemporaryFolder(CopyRequest request, Action<string> progress)
        {
            string parent = Path.GetFullPath(Path.GetTempPath());
            ReportScan(progress, "Resolve temporary folder", 0, parent);
            string canonical = CanonicalDirectory(parent);
            string name = "SapienCopy-" + Guid.NewGuid().ToString("N");
            string temporary = Path.Combine(parent, name);
            string canonicalTemporary = canonical.TrimEnd('\\') + "\\" + name;
            if (request.DirectCopy || request.UpdateExisting)
                foreach (string source in request.Sources)
                {
                    string target = OutputFor(request, source);
                    ReportScan(progress, "Check temporary output overlap", 0, target);
                    string canonicalTarget = Directory.Exists(target) ? CanonicalDirectory(target) : CanonicalDirectory(request.Destination) + "\\" + target.Substring(request.Destination.TrimEnd('\\').Length).TrimStart('\\');
                    if (IsSameOrChild(canonicalTemporary, canonicalTarget))
                        throw new InvalidOperationException("The Windows temporary folder is inside a copy output folder. Enable Save logs and reports, choose another destination, or set TEMP to a folder outside that output before running without logs.");
                }
            Directory.CreateDirectory(temporary);
            return temporary;
        }

        private static void ExcludeTemporaryFolder(CopyRequest request, Dictionary<string, OneDrivePlan> plans, string temporary, Action<string> progress)
        {
            ReportScan(progress, "Resolve temporary exclusion", 0, temporary);
            string canonicalTemporary = CanonicalDirectory(temporary);
            foreach (string source in request.Sources)
            {
                ReportScan(progress, "Check temporary source overlap", 0, source);
                string canonicalSource = CanonicalDirectory(source);
                if (IsSameOrChild(temporary, source))
                    plans[source].AddExcluded(new ExcludedPath { Path = temporary, Directory = true, Reason = "Temporary transfer files" });
                if (IsSameOrChild(canonicalTemporary, canonicalSource))
                {
                    string mapped = source.TrimEnd('\\') + canonicalTemporary.Substring(canonicalSource.Length);
                    plans[source].AddExcluded(new ExcludedPath { Path = mapped, Directory = true, Reason = "Temporary transfer files" });
                }
            }
        }

        private static void DeleteTemporaryFolder(string temporary)
        {
            // Only this run's flat helper directory is removed; never recurse or follow a replacement link.
            string full = Path.GetFullPath(temporary);
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            if (!String.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("SapienCopy-", StringComparison.Ordinal) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Temporary transfer files could not be safely removed: " + full);
            foreach (string file in Directory.GetFiles(full)) File.Delete(file);
            Directory.Delete(full, false);
        }

        internal sealed class OutputTail
        {
            private readonly Queue<string> lines = new Queue<string>();
            private int characters;
            internal void Add(string line)
            {
                if (String.IsNullOrWhiteSpace(line)) return;
                lock (lines)
                {
                    if (line.Length > 4000) line = line.Substring(line.Length - 4000);
                    lines.Enqueue(line); characters += line.Length;
                    while (characters > 12000 && lines.Count > 1) characters -= lines.Dequeue().Length;
                }
            }
            public override string ToString() { lock (lines) return String.Join(Environment.NewLine, lines.ToArray()); }
        }

        internal static string BuildArguments(CopyRequest request, FolderResult folder, bool scan = false)
        {
            string paths = Quote(folder.FileCopy ? Path.GetDirectoryName(folder.Source) : folder.Source) + " " + Quote(folder.FileCopy ? Path.GetDirectoryName(folder.Destination) : folder.Destination);
            if (request.Bindings != null) paths = request.Bindings.ToRobocopy(paths);
            string log = scan ? folder.ScanLogPath : folder.LogPath, filters = folder.FilterArguments;
            if (request.Bindings != null)
            {
                if (scan || request.SaveLogs) log = request.Bindings.ToHelperPath(log);
                if (folder.JobPath != null && filters.IndexOf(" /JOB:", StringComparison.OrdinalIgnoreCase) >= 0)
                    filters = filters.Replace(" /JOB:" + Quote(folder.JobPath), " /JOB:" + Quote(request.Bindings.ToHelperPath(folder.JobPath)));
            }
            return paths + folder.IncludeArguments +
                (folder.FileCopy || folder.RootFiles ? " /MT:" : folder.FlatCopy ? " /E /LEV:1 /MT:" : " /E /MT:") + request.Threads.ToString(CultureInfo.InvariantCulture) +
                " /R:1 /W:1" + (DestinationPolicy.IsPortable(request.DestinationFileSystem) ? " /COPY:DATX" : " /COPY:DAT") +
                (folder.FileCopy || folder.RootFiles ? " /NODCOPY" : DestinationPolicy.IsPortable(request.DestinationFileSystem) ? " /DCOPY:DATX" : " /DCOPY:DAT") +
                (String.Equals(request.DestinationFileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) ? " /FFT" : "") + " /SL" + (folder.RootFiles || folder.FileCopy ? "" : " /SJ") + " /NP /NDL" +
                (scan || request.SaveLogs ? (scan || !folder.AppendLog ? " /UNILOG:" : " /UNILOG+:") + Quote(log) : "") +
                (scan ? " /L /BYTES /FP /NC /NJH /NJS /XX" : " /NFL") +
                (request.BackupMode ? " /ZB" : " /Z") + filters;
        }

        internal static string ClassifyExitCode(int exitCode, bool updateExisting)
        {
            return exitCode < 0 || exitCode >= 8 ? "Failed" : (exitCode & (updateExisting ? 4 : 6)) != 0 ? "Warning" : "Completed";
        }

        private static void CheckUpdateTargets(CopyRequest request, Dictionary<string, OneDrivePlan> plans, Action<string> progress, CancellationToken cancellation)
        {
            ScanReporter scan = new ScanReporter(progress, "Existing destination links");
            foreach (string source in request.Sources)
            {
                cancellation.ThrowIfCancellationRequested();
                OneDrivePlan plan = plans[source];
                if (plan.SkipSource) continue;
                string root = request.Session == null ? request.Destination : VolumeIdentity.BoundPath(request.Session.RunFolder);
                string target = OutputFor(request, source);
                Notify(progress, "Checking existing destination links: " + target);
                scan.Folder(target);
                string relative = target.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
                string ancestor = root;
                bool sourceDirectory = IsDirectory(source);
                foreach (string part in relative.Split('\\'))
                {
                    ancestor = Path.Combine(ancestor, part);
                    scan.Folder(ancestor);
                    FileAttributes? attributes = ExistingAttributes(ancestor);
                    if (!attributes.HasValue) break;
                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("The existing destination path contains a link: " + ancestor + ". Select a fresh destination to prevent copying into the link's target.");
                    bool final = String.Equals(ancestor, target, StringComparison.OrdinalIgnoreCase);
                    bool directory = (attributes.Value & FileAttributes.Directory) != 0;
                    if ((!final && !directory) || (final && directory != sourceDirectory))
                        throw new InvalidOperationException("A file and folder conflict at the destination: " + ancestor + ". Choose another destination or resolve the conflicting item before copying.");
                    if (final) RejectHardLinkedFile(ancestor, attributes.Value);
                }
                if (!sourceDirectory) continue;
                if (!Directory.Exists(target)) continue;
                Stack<KeyValuePair<string, string>> pending = new Stack<KeyValuePair<string, string>>();
                pending.Push(new KeyValuePair<string, string>(source, target));
                while (pending.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    KeyValuePair<string, string> pair = pending.Pop();
                    scan.Folder(pair.Value);
                    foreach (string existing in Directory.EnumerateFileSystemEntries(pair.Value))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        scan.Entry();
                        string original = Path.Combine(pair.Key, Path.GetFileName(existing));
                        if (!plan.IsSelected(original)) continue;
                        FileAttributes destinationAttributes = File.GetAttributes(existing);
                        FileAttributes? sourceAttributes = ExistingAttributes(original);
                        if (!sourceAttributes.HasValue) continue; // Destination-only files are retained.
                        RejectHardLinkedFile(existing, destinationAttributes);
                        bool sourceLink = (sourceAttributes.Value & FileAttributes.ReparsePoint) != 0 && OneDrivePolicy.IsNameSurrogate(OneDrivePolicy.GetEntry(original, plan.IsCachedPath(original)).Tag);
                        bool destinationLink = (destinationAttributes & FileAttributes.ReparsePoint) != 0;
                        if (destinationLink && !sourceLink)
                            throw new InvalidOperationException("An existing destination link would redirect the copy: " + existing + ". Select a fresh destination or resolve that link before updating.");
                        if (!sourceLink && !destinationLink && (sourceAttributes.Value & FileAttributes.Directory) != 0 && (destinationAttributes & FileAttributes.Directory) != 0)
                            pending.Push(new KeyValuePair<string, string>(original, existing));
                    }
                }
            }
        }

        private static FileAttributes? ExistingAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void RejectHardLinkedFile(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.Directory) == 0 && VolumeIdentity.HardLinkCount(path) > 1)
                throw new InvalidOperationException("An existing destination file shares its data with another path: " + path + ". Choose a fresh destination to avoid changing files outside this copy.");
        }

        internal static void CheckHelperFile(string path)
        {
            FileAttributes? attributes = ExistingAttributes(path);
            if (!attributes.HasValue) return;
            if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException("An existing report or transfer helper is a folder or link: " + path + ". Use a fresh copy session or resolve this item before resuming.");
            RejectHardLinkedFile(path, attributes.Value);
        }

        internal static string Quote(string value)
        {
            // A quoted drive root needs its final backslash doubled for Windows argument parsing.
            return "\"" + value.TrimEnd('\\') + new string('\\', (value.Length - value.TrimEnd('\\').Length) * 2) + "\"";
        }

        internal static void Stop(Process process)
        {
            try
            {
                if (process.HasExited) return;
                process.Kill();
                if (!process.WaitForExit(5000))
                    throw new InvalidOperationException("Robocopy did not exit after cancellation. Check Task Manager before disconnecting the drive.");
            }
            catch (InvalidOperationException)
            {
                // A process that was never started has nothing to stop.
                try { int id = process.Id; }
                catch (InvalidOperationException) { return; }
                throw;
            }
        }

        private static string ExistingLocalDirectory(string value) { return ExistingLocalPath(value, true); }

        internal static bool IsDirectory(string path) { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }

        private static string ExistingLocalPath(string value, bool directoryOnly)
        {
            bool volumePath = value != null && value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);
            if (!volumePath && (String.IsNullOrWhiteSpace(value) || value.Length < 3 || !Char.IsLetter(value[0]) || value[1] != ':' || (value[2] != '\\' && value[2] != '/')))
                throw new InvalidOperationException("Choose an existing local " + (directoryOnly ? "folder" : "file or folder") + " with a drive letter, such as D:\\Backups.");
            if (value.IndexOf(':', 2) >= 0) throw new InvalidOperationException("Select the file itself rather than an alternate data stream: " + value);
            string path = Path.GetFullPath(value);
            if (!IsVolumeRoot(path)) path = path.TrimEnd('\\');
            FileAttributes? attributes = ExistingAttributes(path);
            if (!attributes.HasValue || (directoryOnly && (attributes.Value & FileAttributes.Directory) == 0))
                throw new InvalidOperationException((directoryOnly ? "Folder" : "File or folder") + " is missing or cannot be accessed: " + path);
            if (!volumePath && new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network)
                throw new InvalidOperationException("Choose a local drive or USB drive. Network destinations are not supported by this version.");
            return path;
        }

        internal static bool IsSameOrChild(string path, string parent)
        {
            return String.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        internal static string CanonicalDirectory(string path)
        {
            return CanonicalPath(path, false);
        }

        internal static string CanonicalEntry(string path)
        {
            // Links are copied as entries; following their final targets would drop separately selected data.
            return CanonicalPath(path, true);
        }

        private static string CanonicalPath(string path, bool preserveFinalEntry)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, preserveFinalEntry ? 0x02200000u : 0x02000000u, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve path: " + path);
                StringBuilder finalPath = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 1);
                if (length == 0 || length >= finalPath.Capacity)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve path: " + path);
                return finalPath.ToString().TrimEnd('\\');
            }
        }

        private static string FileSystemFor(string path)
        {
            StringBuilder volume = new StringBuilder(32768);
            if (!GetVolumePathName(path, volume, (uint)volume.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
            StringBuilder fileSystem = new StringBuilder(64);
            uint serial, maxName, flags;
            if (!GetVolumeInformation(volume.ToString(), null, 0, out serial, out maxName, out flags, fileSystem, (uint)fileSystem.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return fileSystem.ToString();
        }

        private static void Notify(Action<string> progress, string message)
        {
            if (progress != null) progress(message);
        }

        internal static void ReportScan(Action<string> progress, string stage, long count, string path)
        {
            Notify(progress, "Scan: " + stage + " | " + count.ToString(CultureInfo.InvariantCulture) + " | " + (path ?? ""));
        }

        internal sealed class ScanReporter
        {
            private readonly Action<string> progress;
            private readonly string stage;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private string current;
            private long entries;
            internal ScanReporter(Action<string> progress, string stage) { this.progress = progress; this.stage = stage; }
            internal void Folder(string path) { current = path; ReportScan(progress, stage, entries, current); clock.Restart(); }
            internal void Entry()
            {
                entries++;
                if (clock.ElapsedMilliseconds < 1000) return;
                ReportScan(progress, stage, entries, current); clock.Restart();
            }
            internal void Complete() { ReportScan(progress, stage, entries, current); }
        }

        internal static ulong? ProcessActivity(Process process)
        {
            IoCounters counters;
            if (!GetProcessIoCounters(process.Handle, out counters)) return null;
            return unchecked(counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherOperationCount);
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static void WriteReport(CopyResult result)
        {
            if (StateChanged != null) StateChanged(result);
            if (!result.SaveLogs) return;
            foreach (string name in new string[] { "Summary.csv", "Exclusions.csv", "STATUS.txt" }) CheckHelperFile(Path.Combine(result.ReportFolder, name));
            StringBuilder csv = new StringBuilder("Source,Destination,Status,RobocopyExitCode,LogFile,Error\r\n");
            foreach (FolderResult folder in result.Folders)
                csv.AppendLine(String.Join(",", new string[] { Csv(folder.Source), Csv(folder.Destination), Csv(folder.Status), Csv(folder.ExitCode.HasValue ? folder.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : ""), Csv(folder.LogPath), Csv(folder.Error) }));
            File.WriteAllText(Path.Combine(result.ReportFolder, "Summary.csv"), csv.ToString(), new UTF8Encoding(true));
            StringBuilder exclusions = new StringBuilder("Path,Type,Reason\r\n");
            foreach (ExcludedPath excluded in result.Exclusions)
                exclusions.AppendLine(Csv(excluded.Path) + "," + Csv(excluded.Directory ? "Directory subtree" : "File") + "," + Csv(excluded.Reason));
            foreach (ExcludedPath warning in result.MetadataWarnings)
                exclusions.AppendLine(Csv(warning.Path) + "," + Csv("Metadata") + "," + Csv(warning.Reason));
            File.WriteAllText(Path.Combine(result.ReportFolder, "Exclusions.csv"), exclusions.ToString(), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(result.ReportFolder, "STATUS.txt"),
                "Status: " + result.Status + Environment.NewLine +
                "Updated: " + DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
                "Completed sources: " + result.CopiedCount + Environment.NewLine +
                "Folders with warnings: " + result.WarningCount + Environment.NewLine +
                "Failed sources: " + result.FailedCount + Environment.NewLine +
                "Skipped sources: " + result.SkippedCount + Environment.NewLine +
                "Intentionally excluded paths: " + result.ExcludedCount + Environment.NewLine +
                "Destination filesystem: " + result.DestinationFileSystem + Environment.NewLine +
                "Metadata compatibility warnings: " + result.CompatibilityWarningCount + Environment.NewLine +
                "OneDrive option: " + result.OneDriveMode + Environment.NewLine +
                "Estimate requested: " + result.EstimateRequested + Environment.NewLine +
                "Estimated bytes to copy: " + (result.EstimatedTotalBytes.HasValue ? result.EstimatedTotalBytes.Value.ToString("F0", CultureInfo.InvariantCulture) : "Unavailable") + Environment.NewLine +
                "Estimate error: " + (result.EstimateError ?? "") + Environment.NewLine +
                "Cancelled: " + result.Cancelled + Environment.NewLine + Environment.NewLine +
                "See Summary.csv and Logs for each source file or folder. NotStarted sources were not copied. Skipped sources and File/Directory rows in Exclusions.csv were intentionally omitted; Metadata rows describe omitted or unsupported metadata, not exclusion of primary file data." + Environment.NewLine +
                "Downloaded-only checks current file metadata without reading file contents; directory listings may be fetched from OneDrive. Cloud state can change after checking; avoid freeing space during a copy. This is a file-availability snapshot, not a guarantee against concurrent cloud-state changes." + Environment.NewLine +
                "Direct copy and Update mode keep existing destination files, including files now excluded by folder selections or the OneDrive option." + Environment.NewLine +
                "Completed means Robocopy reported no copy failures or mismatches. Destination-only extras are expected when updating. Codes 8 and above are failures." + Environment.NewLine +
                "File contents were not hash-verified. A running app's files may change during copying; close apps for consistent backups." + Environment.NewLine +
                "NTFS copies preserve links as links. Other supported destinations reject links before copying. Targets outside selected folders are not included automatically." + Environment.NewLine +
                "File data and supported attributes and times are copied. Original ACLs, ownership and auditing permissions are not copied. FAT32 uses two-second timestamp comparisons. Compatibility checks are a metadata snapshot; close applications that may change source files during copying." + Environment.NewLine,
                new UTF8Encoding(true));
        }

        private sealed class ProcessWriteSpeed
        {
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private double previousSeconds, smoothed;
            private ulong previousBytes;
            private bool sampled;

            internal void Sample(Process process, Action<double?> report, Action<ulong, double?> transfer, bool final)
            {
                if (report == null && transfer == null) return;
                double seconds = clock.Elapsed.TotalSeconds;
                double elapsed = seconds - previousSeconds;
                if (elapsed < (final ? 0.01 : 1.0)) return;
                IoCounters counters;
                if (!GetProcessIoCounters(process.Handle, out counters)) { if (report != null) report(null); return; }
                double current = (counters.WriteTransferCount - previousBytes) / elapsed;
                smoothed = sampled ? (smoothed + current) / 2 : current;
                sampled = true;
                previousBytes = counters.WriteTransferCount;
                previousSeconds = seconds;
                if (report != null) report(smoothed);
                if (transfer != null) transfer(counters.WriteTransferCount, smoothed);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathName(string path, StringBuilder volumePath, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder volumeName, uint volumeNameSize, out uint serial, out uint maxNameLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    }
}
