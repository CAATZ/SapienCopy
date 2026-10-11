using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace SapienCopy
{
    internal static class CopyEngineTests
    {
        private static int checks;

        public static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            try
            {
                string root = Path.Combine(args.Length == 0 ? Environment.CurrentDirectory : args[0], "fixtures-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                Console.WriteLine("Fixture root: " + root);
                string source = Path.Combine(root, "source with spaces & unicode ñ");
                string child = Path.Combine(source, "empty folder");
                string destination = Path.Combine(root, "destination");
                Directory.CreateDirectory(child);
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(source, "hello.txt"), "A small real Robocopy fixture.\r\n", Encoding.UTF8);
                string hidden = Path.Combine(source, ".hidden.txt");
                File.WriteAllText(hidden, "hidden fixture");
                File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
                byte[] binary = new byte[524288];
                new Random(19).NextBytes(binary);
                File.WriteAllBytes(Path.Combine(source, "binary.bin"), binary);

                CopyRequest request = new CopyRequest { Sources = new string[] { child, source, source }, Destination = destination, Threads = 4 };
                CopyRequest normalized = CopyEngine.Validate(request);
                Assert(request.EstimateProgress && normalized.EstimateProgress, "Progress estimation is enabled by default and preserved by validation");
                Assert(normalized.Sources.Length == 1 && normalized.Sources[0] == source, "Parent selection includes duplicate and nested folders once");
                ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = child }); }, "Destination inside source rejected");
                ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = source }); }, "Equal destination rejected");
                ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = destination, Threads = 0 }); }, "Invalid thread count rejected");
                if (!CopyEngine.IsAdministrator())
                    ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = destination, BackupMode = true }); }, "Backup mode rejected without elevation");

                CopyResult copied = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(copied.Status == "Completed" && copied.CopiedCount == 1 && copied.FailedCount == 0, "Real Robocopy completes");
                Assert(Path.GetFileName(copied.RunFolder).StartsWith("SapienCopy-", StringComparison.Ordinal), "Dated copy uses the application name");
                Assert(copied.EstimatedTotalBytes == DirectoryBytes(source), "New-copy estimate counts selected file bytes");
                string mapped = CopyEngine.DestinationFor(copied.RunFolder, source);
                Console.WriteLine("Mapped fixture: " + mapped + " (" + mapped.Length + " characters)");
                Assert(mapped == Path.Combine(copied.RunFolder, source.Substring(0, 1), source.Substring(3)), "Full drive-relative source path retained");
                Assert(Directory.Exists(Path.Combine(mapped, "empty folder")), "Empty folder copied");
                foreach (string input in Directory.GetFiles(source))
                    Assert(Hash(input) == Hash(Path.Combine(mapped, Path.GetFileName(input))), "Hash matches: " + Path.GetFileName(input));
                Assert((File.GetAttributes(Path.Combine(mapped, ".hidden.txt")) & FileAttributes.Hidden) != 0, "Hidden attribute preserved");
                Assert(File.Exists(Path.Combine(copied.ReportFolder, "STATUS.txt")) && File.Exists(Path.Combine(copied.ReportFolder, "Summary.csv")) && File.Exists(copied.Folders[0].LogPath), "Status, summary and Robocopy log written");

                File.WriteAllText(Path.Combine(mapped, "destination only.txt"), "Keep me.");
                File.WriteAllText(Path.Combine(source, "hello.txt"), "Updated, longer source file content for the update test.");
                CopyRequest update = new CopyRequest { Sources = new string[] { source }, Destination = copied.RunFolder, UpdateExisting = true, Threads = 4 };
                CopyResult updated = CopyEngine.Run(update, null, CancellationToken.None);
                Assert(updated.Status == "Completed" && updated.RunFolder == copied.RunFolder, "Update mode treats retained extras as expected");
                Assert(updated.EstimatedTotalBytes == new FileInfo(Path.Combine(source, "hello.txt")).Length, "Update sizing omits unchanged files and destination extras");
                Assert(updated.ReportFolder != copied.ReportFolder && File.Exists(Path.Combine(copied.ReportFolder, "Summary.csv")), "Update preserves previous reports");
                Assert(Hash(Path.Combine(source, "hello.txt")) == Hash(Path.Combine(mapped, "hello.txt")), "Update replaces changed destination file");
                Assert(File.Exists(Path.Combine(mapped, "destination only.txt")), "Update retains destination-only files");

                string badSource = Path.Combine(root, "locked source");
                Directory.CreateDirectory(badSource);
                string locked = Path.Combine(badSource, "locked.txt");
                File.WriteAllText(locked, "Locked fixture.");
                using (FileStream hold = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    int speedSamples = 0;
                    double? lastSpeed = 0;
                    bool failureReachedComplete = false;
                    CopyResult failed = CopyEngine.Run(new CopyRequest { Sources = new string[] { badSource }, Destination = destination, Threads = 1 }, null, CancellationToken.None,
                        delegate(double? speed) { lastSpeed = speed; if (speed.HasValue) { Assert(speed.Value >= 0 && !Double.IsNaN(speed.Value) && !Double.IsInfinity(speed.Value), "Speed sample is finite and nonnegative"); speedSamples++; } },
                        delegate(CopyProgress detail) { if (detail.Percent == 100 || detail.IsComplete) failureReachedComplete = true; });
                    Assert(!failureReachedComplete, "Failed copying never reports 100 percent or complete");
                    Assert(speedSamples > 0 && !lastSpeed.HasValue, "Live process speed sampled and cleared after completion");
                    Assert(failed.Status == "Failed" && failed.FailedCount == 1 && failed.Folders[0].ExitCode >= 8, "Locked file produces a real reported Robocopy failure");
                    Assert(File.ReadAllText(Path.Combine(failed.ReportFolder, "STATUS.txt")).Contains("Status: Failed"), "Failure persists in status report");

                    using (CancellationTokenSource cancellation = new CancellationTokenSource())
                    {
                        CopyResult cancelled = CopyEngine.Run(new CopyRequest { Sources = new string[] { badSource, source }, Destination = destination, Threads = 1 },
                            delegate(string message) { if (message.StartsWith("Robocopy running:")) cancellation.Cancel(); }, cancellation.Token);
                        Assert(cancelled.Cancelled && cancelled.Status == "Cancelled", "Cancellation reports incomplete run");
                        Assert(cancelled.Folders[0].Status == "Cancelled" && cancelled.Folders[1].Status == "NotStarted", "Running child is cancelled and next source never starts");
                        Assert(cancelled.Folders[0].ExitCode.HasValue, "Cancelled child exited before Run returned");
                        Assert(!Directory.Exists(cancelled.Folders[1].Destination), "Cancelled queue creates no second output folder");
                    }
                }

                string alias = Path.Combine(root, "source alias");
                if (MakeJunction(alias, source))
                {
                    ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = alias }); }, "Junction alias of source rejected as destination");
                    Assert(CopyEngine.Validate(new CopyRequest { Sources = new string[] { source, alias }, Destination = destination }).Sources.Length == 2, "A selected source link stays distinct from its separately selected target");
                    string external = Path.Combine(root, "external link target");
                    Directory.CreateDirectory(external);
                    File.WriteAllText(Path.Combine(external, "external.txt"), "Link target fixture.");
                    if (MakeJunction(Path.Combine(source, "linked folder"), external))
                    {
                        CopyResult linked = CopyEngine.Run(request, null, CancellationToken.None);
                        Assert(linked.Status == "Completed", "Directory junction copied in normal mode");
                        string linkedOutput = Path.Combine(CopyEngine.DestinationFor(linked.RunFolder, source), "linked folder");
                        Assert((File.GetAttributes(linkedOutput) & FileAttributes.ReparsePoint) != 0, "Copied directory junction remains a link");
                        CopyResult linkedUpdate = CopyEngine.Run(new CopyRequest { Sources = new string[] { source }, Destination = linked.RunFolder, UpdateExisting = true }, null, CancellationToken.None);
                        Assert(linkedUpdate.Status == "Completed", "Update accepts matching source and destination links");
                    }
                    string live = Path.Combine(source, "live child");
                    Directory.CreateDirectory(live);
                    File.WriteAllText(Path.Combine(live, "never overwrite.txt"), "Original live data");
                    string malicious = Path.Combine(mapped, "live child");
                    if (MakeJunction(malicious, live))
                    {
                        ExpectFailure(delegate { CopyEngine.Run(update, null, CancellationToken.None); }, "Update refuses destination junction pointing at ordinary source directory");
                        Assert(File.ReadAllText(Path.Combine(live, "never overwrite.txt")) == "Original live data", "Rejected update leaves live target unchanged");
                    }
                }
                else Console.WriteLine("SKIP: junction fixtures could not be created with this account.");

                Assert(CopyEngine.ClassifyExitCode(0, false) == "Completed" && CopyEngine.ClassifyExitCode(1, false) == "Completed", "Exit codes 0/1 classified");
                Assert(CopyEngine.ClassifyExitCode(2, true) == "Completed" && CopyEngine.ClassifyExitCode(3, true) == "Completed", "Update extras classified");
                Assert(CopyEngine.ClassifyExitCode(4, true) == "Warning" && CopyEngine.ClassifyExitCode(7, true) == "Warning", "Mismatch exit codes classified");
                Assert(CopyEngine.ClassifyExitCode(8, false) == "Failed" && CopyEngine.ClassifyExitCode(16, false) == "Failed" && CopyEngine.ClassifyExitCode(-1, false) == "Failed", "Failure exit codes classified");
                string options = CopyEngine.BuildArguments(request, copied.Folders[0]);
                Assert(!options.Contains("/MIR") && !options.Contains("/PURGE") && !options.Contains("/MOVE") && !options.Contains(" /B"), "Default options contain no delete or backup mode");
                TestOneDrive(root, destination);
                TestEstimates(root, destination);
                TestExcludedFolders(root, destination);
                TestDirectAndLogging(root, destination);
                TestDestinationCompatibility(root);
                TestCachedSelection(root);
                Console.WriteLine("PASS: " + checks + " assertions. Fixtures retained for inspection.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void TestOneDrive(string root, string destination)
        {
            Assert(OneDrivePolicy.NeedsDownload(0x1000) && OneDrivePolicy.NeedsDownload(0x40000) && OneDrivePolicy.NeedsDownload(0x400000), "All three offline/recall flags require exclusion");
            Assert(!OneDrivePolicy.NeedsDownload(0x80000) && !OneDrivePolicy.NeedsDownload(0x100000), "Pinned and unpinned intent alone does not mean missing data");
            Assert(!OneDrivePolicy.IsNameSurrogate(0x9000001A) && !OneDrivePolicy.IsNameSurrogate(0x9000F01A) && OneDrivePolicy.IsNameSurrogate(0xA000000C) && OneDrivePolicy.IsNameSurrogate(0xA0000003), "Cloud reparse tags distinguished from actual links");
            string source = Path.Combine(root, "policy fixture");
            string cloud = Path.Combine(source, "Mock OneDrive ñ");
            string notDownloadedDirectory = Path.Combine(cloud, "online folder");
            Directory.CreateDirectory(notDownloadedDirectory);
            File.WriteAllText(Path.Combine(cloud, "downloaded.txt"), "Downloaded data.");
            string notDownloaded = Path.Combine(cloud, "online 中文.txt");
            File.WriteAllText(notDownloaded, "Synthetic offline data, no provider attached.");
            File.SetAttributes(notDownloaded, File.GetAttributes(notDownloaded) | FileAttributes.Offline);
            File.WriteAllText(Path.Combine(notDownloadedDirectory, "cached child.txt"), "Cached child inside partly virtual directory.");
            File.SetAttributes(notDownloadedDirectory, File.GetAttributes(notDownloadedDirectory) | FileAttributes.Offline);
            string unrelated = Path.Combine(source, "unrelated offline.txt");
            File.WriteAllText(unrelated, "Outside OneDrive; should copy.");
            File.SetAttributes(unrelated, File.GetAttributes(unrelated) | FileAttributes.Offline);
            string[] roots = new string[] { notDownloadedDirectory, cloud, cloud };
            CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = destination, OneDriveMode = OneDriveMode.DownloadedOnly };
            CopyResult filtered = CopyEngine.RunForTests(request, null, CancellationToken.None, roots);
            string mapped = CopyEngine.DestinationFor(filtered.RunFolder, source);
            string mappedCloud = Path.Combine(mapped, "Mock OneDrive ñ");
            Assert(filtered.Status == "Completed" && filtered.ExcludedCount == 1 && filtered.SkippedCount == 0, "Downloaded-only reports file exclusions");
            Assert(filtered.EstimatedTotalBytes == DirectoryBytes(source) - new FileInfo(notDownloaded).Length, "Sizing honors OneDrive exclusion policy");
            Assert(File.Exists(Path.Combine(mappedCloud, "downloaded.txt")), "Downloaded OneDrive file copied");
            Assert(!File.Exists(Path.Combine(mappedCloud, "online 中文.txt")), "Unicode online file omitted");
            Assert(File.Exists(Path.Combine(mappedCloud, "online folder\\cached child.txt")), "Cached file inside an offline-marked directory remains eligible");
            Assert(File.Exists(Path.Combine(mapped, "unrelated offline.txt")), "OneDrive policy leaves unrelated offline files eligible");
            Assert(File.ReadAllText(Path.Combine(filtered.ReportFolder, "Exclusions.csv")).Contains("online 中文.txt"), "Exclusion report retains Unicode paths");

            request.OneDriveMode = OneDriveMode.All;
            CopyResult all = CopyEngine.RunForTests(request, null, CancellationToken.None, roots);
            Assert(all.Status == "Completed" && File.Exists(Path.Combine(CopyEngine.DestinationFor(all.RunFolder, cloud), "online 中文.txt")), "Include-all retains native copy behavior");
            request.OneDriveMode = OneDriveMode.Exclude;
            CopyResult excluded = CopyEngine.RunForTests(request, null, CancellationToken.None, roots);
            Assert(excluded.Status == "Completed" && excluded.ExcludedCount == 1 && !Directory.Exists(CopyEngine.DestinationFor(excluded.RunFolder, cloud)), "Excluded OneDrive root omitted beneath selected parent");
            request.Sources = new string[] { cloud };
            CopyResult skipped = CopyEngine.RunForTests(request, null, CancellationToken.None, roots);
            Assert(skipped.Status == "Completed" && skipped.SkippedCount == 1 && skipped.Folders[0].Status == "Skipped" && !Directory.Exists(skipped.Folders[0].Destination), "Whole OneDrive source intentionally skipped without copy");
            request.Sources = new string[] { notDownloadedDirectory };
            skipped = CopyEngine.RunForTests(request, null, CancellationToken.None, roots);
            Assert(skipped.SkippedCount == 1, "Selected descendant of excluded OneDrive root skipped");
            request.OneDriveMode = OneDriveMode.DownloadedOnly;
            skipped = CopyEngine.RunForTests(request, null, CancellationToken.None, roots);
            Assert(skipped.CopiedCount == 1 && File.Exists(Path.Combine(skipped.Folders[0].Destination, "cached child.txt")), "Selected offline-marked directory still copies cached child files");

            request.Sources = new string[] { cloud };
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                CopyResult cancelled = CopyEngine.RunForTests(request, delegate(string message) { if (message.StartsWith("Checking downloaded")) cancel.Cancel(); }, cancel.Token, roots);
                Assert(cancelled.Cancelled && cancelled.RunFolder == null, "Cancelled metadata preflight creates no backup");
            }

            string alias = Path.Combine(root, "cloud alias");
            if (MakeJunction(alias, cloud))
            {
                request.Sources = new string[] { alias };
                request.OneDriveMode = OneDriveMode.Exclude;
                Assert(CopyEngine.RunForTests(request, null, CancellationToken.None, roots).SkippedCount == 1, "Selected junction alias of OneDrive root excluded");
            }

            string jobSource = Path.Combine(root, "job filter fixture ñ");
            Directory.CreateDirectory(jobSource);
            File.WriteAllText(Path.Combine(jobSource, "keep.txt"), "Retain this file.");
            OneDrivePlan jobPlan = new OneDrivePlan();
            for (int i = 0; i < 170; i++)
            {
                string excludedFile = Path.Combine(jobSource, "excluded file ñ " + i.ToString("D3") + ".txt");
                File.WriteAllText(excludedFile, "Must be omitted.");
                jobPlan.Paths.Add(new ExcludedPath { Path = excludedFile, Directory = false, Reason = "Test exclusion" });
            }
            string jobPath = Path.Combine(root, "exclusions.rcj");
            jobPlan.PrepareArguments(jobPath);
            Assert(jobPlan.Arguments.Contains("/JOB:"), "Long exclusion list uses a job file");
            string jobDestination = Path.Combine(root, "job output");
            FolderResult folder = new FolderResult { Source = jobSource, Destination = jobDestination, LogPath = Path.Combine(root, "job.log"), FilterArguments = jobPlan.Arguments };
            FolderResult scanFolder = new FolderResult { Source = jobSource, Destination = jobDestination, LogPath = Path.ChangeExtension(jobPath, ".log"), ScanLogPath = Path.Combine(root, "job-scan.log"), FilterArguments = jobPlan.Arguments };
            Assert(CopyEstimate.Scan(new CopyRequest(), scanFolder, null, CancellationToken.None) == new FileInfo(Path.Combine(jobSource, "keep.txt")).Length, "Sizing honors long-list job exclusions and ignores localized job heading");
            ProcessStartInfo start = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"), Arguments = CopyEngine.BuildArguments(new CopyRequest(), folder), UseShellExecute = false, CreateNoWindow = true };
            using (Process process = Process.Start(start)) { process.WaitForExit(); Assert(process.ExitCode < 8, "Native Robocopy accepts generated job file"); }
            Assert(Directory.GetFiles(jobDestination).Length == 1 && File.Exists(Path.Combine(jobDestination, "keep.txt")), "Job file excludes every OEM non-ASCII path correctly");
            foreach (ExcludedPath path in jobPlan.Paths) path.Path += "中文";
            ExpectFailure(delegate { jobPlan.PrepareArguments(Path.Combine(root, "unsupported.rcj")); }, "Huge unencodable exclusion list fails before copy");
        }

        private static void TestEstimates(string root, string destination)
        {
            string a = Path.Combine(root, "estimate A");
            string b = Path.Combine(root, "estimate B");
            Directory.CreateDirectory(a); Directory.CreateDirectory(b);
            File.WriteAllBytes(Path.Combine(a, "one.bin"), new byte[123]);
            File.WriteAllBytes(Path.Combine(b, "two.bin"), new byte[456]);
            CopyRequest request = new CopyRequest { Sources = new string[] { a, b }, Destination = destination };
            List<CopyProgress> details = new List<CopyProgress>();
            CopyResult result = CopyEngine.Run(request, null, CancellationToken.None, null, details.Add);
            Assert(result.Status == "Completed" && result.EstimatedTotalBytes == 579, "Multiple source sizes combine into one total");
            double previous = 0;
            bool monotonic = true;
            foreach (CopyProgress detail in details)
                if (detail.Percent.HasValue) { if (detail.Percent < previous || (detail.Percent == 100 && !detail.IsComplete)) monotonic = false; previous = detail.Percent.Value; }
            Assert(details[0].IsScanning && details[details.Count - 1].IsComplete && previous == 100 && monotonic, "Global progress is monotonic and reaches 100 only at successful completion");

            request.EstimateProgress = false;
            Assert(!CopyEngine.Validate(request).EstimateProgress, "Unchecked estimation preserved by validation");
            details.Clear();
            result = CopyEngine.Run(request, null, CancellationToken.None, null, details.Add);
            Assert(result.Status == "Completed" && !result.EstimatedTotalBytes.HasValue && Directory.GetFiles(Path.Combine(result.ReportFolder, "Logs"), "*-scan.log").Length == 0, "Unchecked option performs no sizing pass or sizing logs");
            Assert(!details.Exists(delegate(CopyProgress detail) { return detail.IsScanning || detail.Percent.HasValue; }), "Unchecked option emits no fake percentage or scanning phase");

            request.EstimateProgress = true;
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                details.Clear();
                result = CopyEngine.Run(request, delegate(string message) { if (message.StartsWith("Estimating copy size:")) cancel.Cancel(); }, cancel.Token, null, details.Add);
                Assert(result.Cancelled && result.Status == "Cancelled" && result.CopiedCount == 0, "Sizing cancellation stops before copying any source");
                Assert(!Directory.Exists(result.Folders[0].Destination) && !Directory.Exists(result.Folders[1].Destination), "Read-only sizing creates no source-data destination folders");
                Assert(!details.Exists(delegate(CopyProgress detail) { return detail.Percent == 100 || detail.IsComplete; }), "Cancelled sizing never reports completion");
            }

            string empty = Path.Combine(root, "estimate zero");
            Directory.CreateDirectory(empty);
            File.WriteAllBytes(Path.Combine(empty, "zero.bin"), new byte[0]);
            details.Clear();
            result = CopyEngine.Run(new CopyRequest { Sources = new string[] { empty }, Destination = destination }, null, CancellationToken.None, null, details.Add);
            Assert(result.EstimatedTotalBytes == 0 && details[details.Count - 1].Percent == 100 && !details.Exists(delegate(CopyProgress detail) { return !detail.IsComplete && detail.Percent.HasValue && detail.Percent != 0; }), "Zero-byte work stays at zero until copy succeeds");

            string failing = Path.Combine(root, "estimate disappearing source");
            string parked = Path.Combine(root, "estimate parked source");
            Directory.CreateDirectory(failing);
            File.WriteAllText(Path.Combine(failing, "preserved.txt"), "Fixture data is retained.");
            if (!Path.GetFullPath(failing).StartsWith(Path.GetFullPath(root) + "\\", StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(parked).StartsWith(Path.GetFullPath(root) + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture move must remain inside its own generated root.");
            details.Clear();
            try
            {
                result = CopyEngine.Run(new CopyRequest { Sources = new string[] { failing }, Destination = destination }, delegate(string message) { if (message.StartsWith("Backup folder:")) Directory.Move(failing, parked); }, CancellationToken.None, null, details.Add);
                Assert(result.Status == "Failed" && !result.EstimatedTotalBytes.HasValue && !String.IsNullOrEmpty(result.EstimateError), "Sizing failure is reported as unavailable rather than a partial or zero total");
                Assert(!details.Exists(delegate(CopyProgress detail) { return detail.Percent.HasValue || detail.IsComplete; }), "Failed sizing and copy never show a misleading percentage");
            }
            finally { if (Directory.Exists(parked)) Directory.Move(parked, failing); }

            string synthetic = "\t 123\tC:\\source\\café 中文.bin\r\n\t0\tC:\\source\\empty.txt\r\n";
            Assert(CopyEstimate.Parse(new StringReader(synthetic), @"C:\source", null, CancellationToken.None) == 123, "Sizing parser handles invariant counts and Unicode paths without translated labels");
            try { CopyEstimate.Parse(new StringReader("100%\r\n"), @"C:\source", null, CancellationToken.None); throw new Exception("Unexpected sizing row accepted"); }
            catch (InvalidDataException) { Assert(true, "Unexpected sizing output disables estimates instead of inventing a total"); }
            CopyProgress state = null;
            CopyEstimateProgress meter = new CopyEstimateProgress(true, delegate(CopyProgress detail) { state = detail; });
            meter.Ready(1000); meter.BeginFolder(1000); meter.Sample(500, 100); meter.Finish(false);
            Assert(state.Percent == 50 && state.Remaining.HasValue && state.Remaining.Value.TotalSeconds == 5, "ETA derives from remaining estimated bytes and a positive rate");
            meter.Sample(9000, 100); meter.Finish(false);
            Assert(state.Percent < 100 && !state.IsComplete, "I/O overhead cannot force premature 100 percent");
        }

        private static long DirectoryBytes(string directory)
        {
            long bytes = 0;
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) bytes += new FileInfo(file).Length;
            return bytes;
        }

        private static void TestExcludedFolders(string root, string destination)
        {
            string source = Path.Combine(root, "manual exclusions source");
            string excluded = Path.Combine(source, "private");
            string nested = Path.Combine(excluded, "nested");
            string sameName = Path.Combine(source, "public\\private");
            Directory.CreateDirectory(nested); Directory.CreateDirectory(sameName);
            File.WriteAllBytes(Path.Combine(source, "keep.bin"), new byte[111]);
            File.WriteAllBytes(Path.Combine(sameName, "keep too.bin"), new byte[44]);
            File.WriteAllBytes(Path.Combine(nested, "omit.bin"), new byte[222]);
            string offline = Path.Combine(excluded, "online.txt");
            File.WriteAllText(offline, "Synthetic cloud placeholder data.");
            File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
            CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = destination, ExcludedFolders = new string[] { nested, excluded, excluded.ToUpperInvariant() } };
            CopyRequest normalized = CopyEngine.Validate(request);
            Assert(normalized.ExcludedFolders.Length == 1 && String.Equals(normalized.ExcludedFolders[0], excluded, StringComparison.OrdinalIgnoreCase), "Excluded folder duplicates and nested selections normalize to their parent");
            CopyResult filtered = CopyEngine.Run(normalized, null, CancellationToken.None);
            string mapped = CopyEngine.DestinationFor(filtered.RunFolder, source);
            Assert(filtered.Status == "Completed" && filtered.ExcludedCount == 1 && filtered.EstimatedTotalBytes == 155, "Manual exclusion removes its entire subtree from copy and sizing");
            Assert(!Directory.Exists(Path.Combine(mapped, "private")) && File.Exists(Path.Combine(mapped, "public\\private\\keep too.bin")), "Full-path exclusion preserves unrelated folders with the same name");
            Assert(File.ReadAllText(Path.Combine(filtered.ReportFolder, "Exclusions.csv")).Contains("Folder excluded by user"), "Manual exclusion reason is saved in report");
            normalized.ExcludedFolders = new string[0];
            Assert(CopyEngine.Validate(normalized).ExcludedFolders.Length == 0, "Clearing normalized exclusions does not retain old aliases");
            ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = destination, ExcludedFolders = new string[] { Path.Combine(root, "missing exclusion") } }); }, "Missing excluded folders rejected before copying");

            request.Sources = new string[] { excluded };
            filtered = CopyEngine.Run(request, null, CancellationToken.None);
            Assert(filtered.SkippedCount == 1 && filtered.Status == "Completed" && !Directory.Exists(filtered.Folders[0].Destination), "Excluded source itself is intentionally skipped");
            request.Sources = new string[] { nested };
            request.ExcludedFolders = new string[] { source };
            filtered = CopyEngine.Run(request, null, CancellationToken.None);
            Assert(filtered.SkippedCount == 1 && filtered.EstimatedTotalBytes == 0, "Excluding a source ancestor skips the selected source");
            request.ExcludedFolders = new string[] { Path.GetPathRoot(source) };
            Assert(CopyEngine.Run(request, null, CancellationToken.None).SkippedCount == 1, "Drive-root exclusion applies to a selected descendant");
            OneDrivePlan rootPlan = new OneDrivePlan();
            rootPlan.AddExcluded(new ExcludedPath { Path = @"C:\", Directory = true, Reason = "Test root" });
            Assert(rootPlan.IsExcluded(@"C:\") && rootPlan.IsExcluded(@"C:\child"), "Directory exclusion matcher handles the drive root itself");

            request.Sources = new string[] { source };
            request.ExcludedFolders = new string[] { excluded };
            request.OneDriveMode = OneDriveMode.DownloadedOnly;
            DirectorySecurity originalSecurity = Directory.GetAccessControl(excluded);
            bool denied = false;
            try
            {
                filtered = CopyEngine.RunForTests(request, delegate(string message) {
                    if (message.StartsWith("Checking downloaded OneDrive files:"))
                    {
                        DirectorySecurity security = Directory.GetAccessControl(excluded);
                        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) security.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.ListDirectory, AccessControlType.Deny));
                        Directory.SetAccessControl(excluded, security);
                        denied = true;
                    }
                }, CancellationToken.None, new string[] { source });
                Assert(denied && filtered.Status == "Completed" && filtered.ExcludedCount == 1 && filtered.EstimatedTotalBytes == 155, "Manual exclusion prevents OneDrive prewalk entering an unreadable excluded subtree");
            }
            finally
            {
                DirectorySecurity restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(originalSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access);
                Directory.SetAccessControl(excluded, restored);
            }

            request.OneDriveMode = OneDriveMode.All;
            string aliasParent = Path.Combine(root, "manual source parent alias");
            string alias = Path.Combine(aliasParent, Path.GetFileName(source));
            string excludedAlias = Path.Combine(root, "manual private alias");
            if (MakeJunction(aliasParent, root) && MakeJunction(excludedAlias, excluded))
            {
                request.Sources = new string[] { alias };
                request.ExcludedFolders = new string[] { excluded, excludedAlias };
                Assert(CopyEngine.Validate(request).ExcludedFolders.Length == 2, "Distinct explicit exclusion aliases stay visible for repeatable runs");
                filtered = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(filtered.Status == "Completed" && filtered.ExcludedCount == 1 && !Directory.Exists(Path.Combine(filtered.Folders[0].Destination, "private")), "Canonical exclusion maps correctly through a selected source alias");
                request.Sources = new string[] { source };
                request.ExcludedFolders = new string[] { excludedAlias };
                Assert(CopyEngine.Run(request, null, CancellationToken.None).EstimatedTotalBytes == 155, "An excluded alias outside the source maps to its target inside the source");
            }

            string external = Path.Combine(root, "manual external target");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "untouched.txt"), "External fixture stays untouched.");
            string link = Path.Combine(source, "external link");
            if (MakeJunction(link, external))
            {
                request.Sources = new string[] { source };
                request.ExcludedFolders = new string[] { external, link, excluded };
                filtered = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(filtered.Status == "Completed" && !Directory.Exists(Path.Combine(filtered.Folders[0].Destination, "external link")), "Explicit junction exclusion removes the link even when an alias target was selected first");
            }

            CopyResult original = CopyEngine.Run(new CopyRequest { Sources = new string[] { source }, Destination = destination }, null, CancellationToken.None);
            Assert(original.Status == "Completed", "Full fixture copy succeeds before exclusion-update test");
            string originalFile = Path.Combine(CopyEngine.DestinationFor(original.RunFolder, excluded), "nested\\omit.bin");
            File.WriteAllBytes(Path.Combine(nested, "omit.bin"), new byte[333]);
            request.Sources = new string[] { source }; request.ExcludedFolders = new string[] { excluded };
            request.Destination = original.RunFolder; request.UpdateExisting = true;
            filtered = CopyEngine.Run(request, null, CancellationToken.None);
            Assert(filtered.Status == "Completed" && new FileInfo(originalFile).Length == 222, "Update preserves old files inside excluded destination folders");

            string updateRoot = Path.Combine(root, "manual update safety");
            string updateMapped = CopyEngine.DestinationFor(updateRoot, source);
            Directory.CreateDirectory(updateMapped);
            if (MakeJunction(Path.Combine(updateMapped, "private"), external))
            {
                request.Destination = updateRoot;
                filtered = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(filtered.Status == "Completed" && File.ReadAllText(Path.Combine(external, "untouched.txt")) == "External fixture stays untouched.", "Excluded update subtree bypasses redirect checks and remains untouched");
            }
        }

        private static void TestDirectAndLogging(string root, string destination)
        {
            string area = Path.Combine(root, "direct fixtures");
            string source = Path.Combine(area, "inputs\\Alpha folder");
            string second = Path.Combine(area, "inputs\\Beta folder");
            string excluded = Path.Combine(source, "omit");
            string output = Path.Combine(area, "output");
            string temporary = Path.Combine(area, "temporary");
            Directory.CreateDirectory(excluded); Directory.CreateDirectory(second);
            Directory.CreateDirectory(output); Directory.CreateDirectory(temporary);
            File.WriteAllBytes(Path.Combine(source, "keep.bin"), new byte[123]);
            File.WriteAllBytes(Path.Combine(second, "second.bin"), new byte[456]);
            File.WriteAllBytes(Path.Combine(excluded, "private.bin"), new byte[999]);
            Assert(new CopyRequest().SaveLogs && !new CopyRequest().DirectCopy, "Existing backup and logs defaults are retained");
            CopyRequest request = new CopyRequest { Sources = new string[] { source, second }, Destination = output, DirectCopy = true, SaveLogs = false, ExcludedFolders = new string[] { excluded } };
            CopyRequest validated = CopyEngine.Validate(request);
            Assert(validated.DirectCopy && !validated.SaveLogs && !validated.UpdateExisting, "Validation retains direct and logging options");
            ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = output, DirectCopy = true, UpdateExisting = true }); }, "Conflicting copy modes rejected");
            ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { Path.GetPathRoot(source) }, Destination = output, DirectCopy = true }); }, "Direct copy rejects unnamed whole-drive source");
            ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = Path.GetDirectoryName(source), DirectCopy = true }); }, "Direct mapping onto the source itself rejected");
            string duplicate = Path.Combine(area, "other\\ALPHA FOLDER");
            Directory.CreateDirectory(duplicate);
            ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source, duplicate }, Destination = output, DirectCopy = true }); }, "Direct duplicate basenames rejected case-insensitively");
            string crossSource = Path.Combine(output, "Alpha folder\\Nested");
            Directory.CreateDirectory(crossSource);
            ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source, crossSource }, Destination = output, DirectCopy = true }); }, "Direct target containing another selected source rejected");

            string oldTemp = Environment.GetEnvironmentVariable("TEMP");
            string oldTmp = Environment.GetEnvironmentVariable("TMP");
            Environment.SetEnvironmentVariable("TEMP", temporary); Environment.SetEnvironmentVariable("TMP", temporary);
            try
            {
                List<CopyProgress> details = new List<CopyProgress>();
                CopyResult result = CopyEngine.Run(request, null, CancellationToken.None, null, details.Add);
                string mapped = Path.Combine(output, "Alpha folder");
                Assert(result.Status == "Completed" && CopyEngine.CanonicalDirectory(result.RunFolder) == CopyEngine.CanonicalDirectory(output) && result.CopiedCount == 2, "Direct copy maps multiple source basenames into the chosen folder");
                Assert(!result.SaveLogs && result.ReportFolder == null && result.Folders[0].LogPath == null && result.CleanupWarning == null, "Logs-off result exposes no nonexistent report or copy log");
                Assert(Hash(Path.Combine(source, "keep.bin")) == Hash(Path.Combine(mapped, "keep.bin")) && File.Exists(Path.Combine(output, "Beta folder\\second.bin")), "Direct copied fixture contents match");
                Assert(!Directory.Exists(Path.Combine(mapped, "omit")) && result.EstimatedTotalBytes == 579 && details[details.Count - 1].Percent == 100, "Direct exclusions also apply to sizing and progress");
                Assert(Directory.GetFiles(output).Length == 0 && Directory.GetDirectories(output).Length == 2 && Directory.GetFileSystemEntries(temporary).Length == 0, "Direct logs-off leaves only source folder outputs and cleans local helpers");
                Assert(!CopyEngine.BuildArguments(request, result.Folders[0]).Contains("/UNILOG"), "Real copy has no log switch when logging is off");

                File.WriteAllText(Path.Combine(mapped, "extra.txt"), "Keep destination-only file.");
                File.WriteAllBytes(Path.Combine(source, "keep.bin"), new byte[321]);
                request.SaveLogs = true;
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && result.EstimatedTotalBytes == 321 && new FileInfo(Path.Combine(mapped, "keep.bin")).Length == 321 && File.Exists(Path.Combine(mapped, "extra.txt")), "Direct merge replaces changed files and retains destination extras");
                Assert(result.SaveLogs && CopyEngine.CanonicalDirectory(Path.GetDirectoryName(result.ReportFolder)) == CopyEngine.CanonicalDirectory(output) && Path.GetFileName(result.ReportFolder).StartsWith("Reports-", StringComparison.Ordinal) && File.Exists(Path.Combine(result.ReportFolder, "STATUS.txt")) && File.Exists(result.Folders[0].LogPath), "Direct logs-on writes a separate report folder");

                request = new CopyRequest { Sources = new string[] { second }, Destination = destination, SaveLogs = false };
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && !result.SaveLogs && result.ReportFolder == null && Directory.GetFiles(result.RunFolder).Length == 0 && Directory.GetDirectories(result.RunFolder).Length == 1, "Dated backup without logs contains only drive-mapped data");
                request.Destination = result.RunFolder; request.UpdateExisting = true;
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && result.EstimatedTotalBytes == 0 && Directory.GetDirectories(result.RunFolder).Length == 1 && Directory.GetFiles(result.RunFolder).Length == 0, "Updating without logs creates no report folder or status files");
                request.UpdateExisting = false; request.DirectCopy = true; request.Destination = output; request.EstimateProgress = false;
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(!result.EstimatedTotalBytes.HasValue && Directory.GetFileSystemEntries(temporary).Length == 0, "Unchecked sizing with logs off leaves no helpers");

                string insideTemp = Path.Combine(source, "local temporary");
                Directory.CreateDirectory(insideTemp);
                Environment.SetEnvironmentVariable("TEMP", insideTemp); Environment.SetEnvironmentVariable("TMP", insideTemp);
                request = new CopyRequest { Sources = new string[] { source }, Destination = output, DirectCopy = true, SaveLogs = false, ExcludedFolders = new string[] { excluded } };
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && Directory.GetFileSystemEntries(insideTemp).Length == 0 && Directory.GetDirectories(Path.Combine(mapped, "local temporary")).Length == 0, "A selected source containing TEMP never copies its own scan or job files");
                Environment.SetEnvironmentVariable("TEMP", temporary); Environment.SetEnvironmentVariable("TMP", temporary);

                string lockedSource = Path.Combine(area, "locked source");
                Directory.CreateDirectory(lockedSource);
                string locked = Path.Combine(lockedSource, "locked.txt"); File.WriteAllText(locked, "Synthetic locked data.");
                request = new CopyRequest { Sources = new string[] { lockedSource }, Destination = output, DirectCopy = true, SaveLogs = false, Threads = 1 };
                using (FileStream hold = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    result = CopyEngine.Run(request, null, CancellationToken.None);
                    Assert(result.Status == "Failed" && result.Folders[0].ExitCode >= 8 && !String.IsNullOrWhiteSpace(result.Folders[0].Error) && Directory.GetFileSystemEntries(temporary).Length == 0, "Logless failure retains Robocopy error text and cleans helpers");
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        result = CopyEngine.Run(request, delegate(string message) { if (message.StartsWith("Robocopy running:")) cancel.Cancel(); }, cancel.Token);
                        Assert(result.Cancelled && result.Folders[0].ExitCode.HasValue && Directory.GetFileSystemEntries(temporary).Length == 0, "Logless copy cancellation waits for its child and cleans helpers");
                    }
                }
                request.Sources = new string[] { second };
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    result = CopyEngine.Run(request, delegate(string message) { if (message.StartsWith("Estimating copy size:")) cancel.Cancel(); }, cancel.Token);
                    Assert(result.Cancelled && result.CopiedCount == 0 && Directory.GetFileSystemEntries(temporary).Length == 0, "Logless sizing cancellation cleans temporary logs before returning");
                }

                string aliasOutput = Path.Combine(area, "aliased source parent");
                if (MakeJunction(aliasOutput, Path.GetDirectoryName(source)))
                    ExpectFailure(delegate { CopyEngine.Validate(new CopyRequest { Sources = new string[] { source }, Destination = aliasOutput, DirectCopy = true }); }, "Direct self-copy through an aliased destination rejected");
                string safeDestination = Path.Combine(area, "redirect output");
                string live = Path.Combine(area, "live target");
                Directory.CreateDirectory(safeDestination); Directory.CreateDirectory(live);
                if (MakeJunction(Path.Combine(safeDestination, Path.GetFileName(second)), live))
                {
                    ExpectFailure(delegate { CopyEngine.Run(new CopyRequest { Sources = new string[] { second }, Destination = safeDestination, DirectCopy = true, SaveLogs = false }, null, CancellationToken.None); }, "Direct copy rejects an existing redirected output before writing");
                    Assert(Directory.GetFileSystemEntries(live).Length == 0 && Directory.GetFileSystemEntries(temporary).Length == 0, "Rejected direct preflight preserves link target and cleans helpers");
                }

                string jobSource = Path.Combine(area, "large exclusions"); Directory.CreateDirectory(jobSource);
                List<string> excludedFolders = new List<string>();
                for (int i = 0; i < 190; i++)
                {
                    string item = Path.Combine(jobSource, "omit " + i.ToString("D3") + " " + new string('x', 50));
                    Directory.CreateDirectory(item); excludedFolders.Add(item);
                }
                File.WriteAllBytes(Path.Combine(jobSource, "keep.bin"), new byte[77]);
                request = new CopyRequest { Sources = new string[] { jobSource }, Destination = output, DirectCopy = true, SaveLogs = false, ExcludedFolders = excludedFolders.ToArray() };
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && result.EstimatedTotalBytes == 77 && result.Folders[0].FilterArguments.Contains("/JOB:"), "Logless large exclusion list works through a temporary job file for sizing and copy");
                Assert(Directory.GetDirectories(result.Folders[0].Destination).Length == 0 && Directory.GetFileSystemEntries(temporary).Length == 0, "Temporary job file is removed after exclusions are honored");

                request.Sources = new string[] { second }; request.ExcludedFolders = new string[0];
                FileStream heldHelper = null;
                string heldPath = null;
                try
                {
                    result = CopyEngine.Run(request, delegate(string message) {
                        if (!message.StartsWith("Completed.")) return;
                        heldPath = Directory.GetFiles(Directory.GetDirectories(temporary, "SapienCopy-*", SearchOption.TopDirectoryOnly)[0], "*-scan.log", SearchOption.TopDirectoryOnly)[0];
                        heldHelper = File.Open(heldPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }, CancellationToken.None);
                    Assert(result.Status == "Completed" && !String.IsNullOrWhiteSpace(result.CleanupWarning) && result.CopiedCount == 1, "Temporary cleanup failure preserves copy outcome and reports a warning");
                }
                finally
                {
                    if (heldHelper != null) heldHelper.Dispose();
                    if (heldPath != null) { File.Delete(heldPath); Directory.Delete(Path.GetDirectoryName(heldPath), false); }
                }
            }
            finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
        }

        private static void TestDestinationCompatibility(string root)
        {
            DestinationPolicy.ValidateFileSystem("exFAT", false);
            DestinationPolicy.ValidateFileSystem("FAT32", false);
            Assert(DestinationPolicy.IsPortable("EXFAT") && DestinationPolicy.IsPortable("fat32") && !DestinationPolicy.IsPortable("NTFS"), "Portable filesystems recognized without changing NTFS policy");
            ExpectFailure(delegate { DestinationPolicy.ValidateFileSystem("ReFS", false); }, "Unknown destination filesystem rejected clearly");
            ExpectFailure(delegate { DestinationPolicy.ValidateFileSystem("exFAT", true); }, "Protected copying blocked when it would override ADS omission");
            Assert(DestinationPolicy.UnsupportedReason("FAT32", 0, 0, UInt32.MaxValue) == null && DestinationPolicy.UnsupportedReason("FAT32", 0, 0, (ulong)UInt32.MaxValue + 1) != null, "FAT32 exact payload boundary checked without allocating large files");
            Assert(DestinationPolicy.UnsupportedReason("exFAT", 0, 0, (ulong)UInt32.MaxValue + 1) == null && DestinationPolicy.UnsupportedReason("NTFS", 0x4000, 0, 1) == null, "exFAT allows large data and NTFS retains existing EFS behavior");
            Assert(DestinationPolicy.UnsupportedReason("FAT32", 0x4000, 0, 1) != null && DestinationPolicy.UnsupportedReason("exFAT", 0x400, 0xA000000C, 1) != null, "Portable destination rejects EFS and symbolic links");
            Assert(DestinationPolicy.IsCloud(0x9000001A) && DestinationPolicy.IsCloud(0x9000F01A) && !DestinationPolicy.IsCloud(0xA0000003), "Cloud placeholders distinguished from unsupported reparse types");
            List<ExcludedPath> cloudWarnings = new List<ExcludedPath>();
            DestinationPolicy.CheckEntry("exFAT", Path.Combine(root, "nonexistent cloud metadata fixture"), new OneDrivePolicy.Entry { Attributes = 0x400 | OneDrivePolicy.RecallOnDataAccess, Tag = 0x9000001A }, cloudWarnings);
            Assert(cloudWarnings.Count == 1 && cloudWarnings[0].Reason.Contains("deferred"), "Cloud stream check reads no content or stream handles and reports deferred inspection");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DestinationPolicy.CheckName(names, Path.Combine(root, "Case.txt"));
            ExpectFailure(delegate { DestinationPolicy.CheckName(names, Path.Combine(root, "case.txt")); }, "Case-folding filename collision blocked for portable destinations");

            string area = Path.Combine(root, "filesystem compatibility");
            string source = Path.Combine(area, "source");
            string output = Path.Combine(area, "output");
            string temporary = Path.Combine(area, "temporary");
            Directory.CreateDirectory(source); Directory.CreateDirectory(output); Directory.CreateDirectory(temporary);
            string file = Path.Combine(source, "main data.bin");
            File.WriteAllBytes(file, new byte[160]);
            WriteFixtureStream(file + ":fixture stream", new byte[] { 1, 2, 3 });
            WriteFixtureStream(source + ":directory fixture", new byte[] { 4, 5 });
            Assert(new List<string>(DestinationPolicy.NamedStreams(file)).Count == 1 && new List<string>(DestinationPolicy.NamedStreams(source)).Count == 1, "Actual file and directory alternate streams are enumerated");
            List<ExcludedPath> storageWarnings = new List<ExcludedPath>();
            DestinationPolicy.CheckEntry("exFAT", file, new OneDrivePolicy.Entry { Attributes = 0x200 | 0x800, SizeLow = 160 }, storageWarnings);
            Assert(storageWarnings.Count == 2 && storageWarnings[1].Reason.Contains("Sparse/compressed storage metadata") && storageWarnings[1].Reason.Contains("primary file data"), "Sparse/compression flags report storage loss without excluding primary data");
            CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = output, DirectCopy = true };
            request.DestinationFileSystem = "FAT32";
            Assert(CopyEngine.Validate(request).DestinationFileSystem == "NTFS", "Public validation resolves actual filesystem rather than trusting supplied state");
            string oldTemp = Environment.GetEnvironmentVariable("TEMP");
            string oldTmp = Environment.GetEnvironmentVariable("TMP");
            Environment.SetEnvironmentVariable("TEMP", temporary); Environment.SetEnvironmentVariable("TMP", temporary);
            try
            {
                List<CopyProgress> details = new List<CopyProgress>();
                CopyResult result = CopyEngine.RunForTests(request, null, CancellationToken.None, null, null, details.Add, "exFAT");
                string mapped = result.Folders[0].Destination;
                Assert(result.Status == "CompletedWithWarnings" && result.CompatibilityWarningCount == 2 && result.ExcludedCount == 0 && result.CopiedCount == 1, "Portable ADS loss warns separately while primary data still copies");
                Assert(result.EstimatedTotalBytes == 160 && Hash(file) == Hash(Path.Combine(mapped, "main data.bin")), "Portable copy and sizing retain complete primary file bytes");
                Assert(new List<string>(DestinationPolicy.NamedStreams(Path.Combine(mapped, "main data.bin"))).Count == 0 && new List<string>(DestinationPolicy.NamedStreams(mapped)).Count == 0, "DATX and DCOPY:DATX omit real file and directory streams");
                Assert(!details.Exists(delegate(CopyProgress detail) { return detail.IsComplete || detail.Percent == 100; }), "Metadata loss never reports unqualified full completion");
                string report = File.ReadAllText(Path.Combine(result.ReportFolder, "Exclusions.csv"));
                Assert(report.Contains("Metadata") && report.Contains(":fixture stream:$DATA") && report.Contains("primary file data"), "Metadata report names omitted streams without claiming primary data was excluded");
                CopyRequest flags = new CopyRequest { DestinationFileSystem = "exFAT" };
                string arguments = CopyEngine.BuildArguments(flags, result.Folders[0]);
                Assert(arguments.Contains("/COPY:DATX") && arguments.Contains("/DCOPY:DATX") && !arguments.Contains("/FFT"), "exFAT options omit streams without unnecessary FAT32 timestamp tolerance");
                flags.DestinationFileSystem = "FAT32";
                Assert(CopyEngine.BuildArguments(flags, result.Folders[0]).Contains("/FFT") && CopyEngine.BuildArguments(flags, result.Folders[0], true).Contains("/FFT"), "FAT32 copy and sizing use matching two-second time precision");

                request.SaveLogs = false;
                List<string> activity = new List<string>();
                result = CopyEngine.RunForTests(request, activity.Add, CancellationToken.None, null, destinationFileSystem: "FAT32");
                Assert(result.Status == "CompletedWithWarnings" && result.ReportFolder == null && activity.Exists(delegate(string line) { return line.StartsWith("Metadata warning:"); }) && Directory.GetFileSystemEntries(temporary).Length == 0, "Portable metadata warnings remain visible and helpers clean up without logs");
                string ntfsOutput = Path.Combine(area, "NTFS output"); Directory.CreateDirectory(ntfsOutput);
                request.Destination = ntfsOutput;
                result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && result.CompatibilityWarningCount == 0 && new List<string>(DestinationPolicy.NamedStreams(Path.Combine(result.Folders[0].Destination, "main data.bin"))).Count == 1, "Actual NTFS path retains file streams and avoids portable warnings");

                string linkParent = Path.Combine(source, "unsupported"); Directory.CreateDirectory(linkParent);
                string link = Path.Combine(linkParent, "junction");
                string external = Path.Combine(area, "external"); Directory.CreateDirectory(external);
                if (MakeJunction(link, external))
                {
                    string blockedOutput = Path.Combine(area, "blocked output"); Directory.CreateDirectory(blockedOutput);
                    request.Destination = blockedOutput;
                    ExpectFailure(delegate { CopyEngine.RunForTests(request, null, CancellationToken.None, null, destinationFileSystem: "exFAT"); }, "Portable copy blocks an unsupported nested junction before writing source data");
                    Assert(Directory.GetFileSystemEntries(blockedOutput).Length == 0 && Directory.GetFileSystemEntries(temporary).Length == 0, "Compatibility failure leaves destination empty and removes local helpers");
                    request.ExcludedFolders = new string[] { linkParent };
                    result = CopyEngine.RunForTests(request, null, CancellationToken.None, null, destinationFileSystem: "exFAT");
                    Assert(result.CopiedCount == 1 && result.ExcludedCount == 1 && !Directory.Exists(Path.Combine(result.Folders[0].Destination, "unsupported")), "Manual exclusion bypasses unsupported subtree safely");
                    request.ExcludedFolders = new string[0]; request.OneDriveMode = OneDriveMode.Exclude;
                    result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { linkParent }, destinationFileSystem: "exFAT");
                    Assert(result.CopiedCount == 1 && result.ExcludedCount == 1, "OneDrive exclusions also precede compatibility checking");
                    request.OneDriveMode = OneDriveMode.All; request.Sources = new string[] { link };
                    ExpectFailure(delegate { CopyEngine.RunForTests(request, null, CancellationToken.None, null, destinationFileSystem: "FAT32"); }, "An unsupported whole selected source is blocked instead of silently skipped");
                    request.ExcludedFolders = new string[] { link };
                    result = CopyEngine.RunForTests(request, null, CancellationToken.None, null, destinationFileSystem: "FAT32");
                    Assert(result.Status == "Completed" && result.SkippedCount == 1, "Explicitly excluded whole source remains an intentional successful skip");
                }
                request.Sources = new string[] { source }; request.ExcludedFolders = new string[0]; request.OneDriveMode = OneDriveMode.All;
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    result = CopyEngine.RunForTests(request, delegate(string message) { if (message.StartsWith("Checking destination compatibility:")) cancel.Cancel(); }, cancel.Token, null, destinationFileSystem: "exFAT");
                    Assert(result.Cancelled && result.RunFolder == null && Directory.GetFileSystemEntries(temporary).Length == 0, "Compatibility preflight cancellation creates no backup and cleans helpers");
                }
                Console.WriteLine("NOTE: Portable flags were exercised on synthetic NTFS-hosted fixtures; no external FAT32/exFAT volume was written or formatted.");
            }
            finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
        }

        private static void TestCachedSelection(string root)
        {
            string area = Path.Combine(root, "cached selection");
            string source = Path.Combine(area, "source");
            string cloud = Path.Combine(source, "OneDrive fixture");
            string mixed = Path.Combine(cloud, "mixed folder");
            string empty = Path.Combine(cloud, "empty folder");
            string sibling = Path.Combine(cloud, "sibling");
            string excluded = Path.Combine(cloud, "private");
            string destination = Path.Combine(area, "destination");
            string temporary = Path.Combine(area, "temporary");
            foreach (string path in new string[] { mixed, empty, sibling, excluded, destination, temporary }) Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(source, "ordinary.bin"), new byte[13]);
            File.WriteAllBytes(Path.Combine(cloud, "root.txt"), new byte[17]);
            string local = Path.Combine(mixed, "cached 中文 & spaces.txt");
            File.WriteAllBytes(local, new byte[23]);
            File.SetAttributes(mixed, File.GetAttributes(mixed) | FileAttributes.Offline);
            File.WriteAllBytes(Path.Combine(excluded, "private.bin"), new byte[211]);
            string offline = Path.Combine(cloud, "offline.bin"); File.WriteAllBytes(offline, new byte[19]);
            File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
            string external = Path.Combine(area, "link target"); Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "untouched.txt"), "Synthetic target must remain untouched.");
            string link = Path.Combine(cloud, "directory link"); bool hasLink = MakeJunction(link, external);
            CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = destination, DirectCopy = true, OneDriveMode = OneDriveMode.DownloadedOnly, ExcludedFolders = new string[] { excluded } };
            string oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
            Environment.SetEnvironmentVariable("TEMP", temporary); Environment.SetEnvironmentVariable("TMP", temporary);
            try
            {
                int snapshots = 0;
                CopyEngine.StateChanged = delegate(CopyResult state) { snapshots++; };
                List<string> messages = new List<string>();
                List<CopyProgress> progress = new List<CopyProgress>();
                bool inserted = false;
                CopyResult result = CopyEngine.RunForTests(request, delegate(string line) {
                    messages.Add(line);
                    if (!line.StartsWith("Copy folder:") || inserted) return;
                    inserted = true;
                    File.WriteAllBytes(Path.Combine(sibling, "root.txt"), new byte[901]);
                    string unseen = Path.Combine(cloud, "unseen virtual folder"); Directory.CreateDirectory(unseen);
                    File.WriteAllBytes(Path.Combine(unseen, "root.txt"), new byte[902]);
                    File.WriteAllBytes(Path.Combine(source, "ordinary late.bin"), new byte[29]);
                    Directory.SetLastWriteTimeUtc(mixed, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(cloud, new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(source, new DateTime(2019, 2, 3, 4, 5, 6, DateTimeKind.Utc));
                }, CancellationToken.None, new string[] { cloud }, null, progress.Add);
                string mapped = Path.Combine(destination, "source");
                string mappedCloud = Path.Combine(mapped, "OneDrive fixture");
                Assert(inserted && result.Status == "Completed" && result.CopiedCount == 1 && result.EstimatedTotalBytes == 82, "Cached batches aggregate into one successful selected source with accurate ordinary-plus-cached sizing");
                Assert(!File.Exists(Path.Combine(mappedCloud, "sibling\\root.txt")) && !Directory.Exists(Path.Combine(mappedCloud, "unseen virtual folder")), "Files and folders unseen in the cached snapshot never enter later sizing or copy passes");
                Assert(File.Exists(Path.Combine(mapped, "ordinary late.bin")) && Hash(local) == Hash(Path.Combine(mappedCloud, "mixed folder\\cached 中文 & spaces.txt")), "Ordinary recursion remains complete and cached files in mixed virtual directories copy with Unicode names");
                Assert(Directory.Exists(Path.Combine(mappedCloud, "empty folder")) && !Directory.Exists(Path.Combine(mappedCloud, "private")) && !File.Exists(Path.Combine(mappedCloud, "offline.bin")), "Empty cached folders survive while manual and offline exclusions remain effective");
                if (hasLink)
                    Assert((File.GetAttributes(Path.Combine(mappedCloud, "directory link")) & FileAttributes.ReparsePoint) != 0 && CopyEngine.CanonicalDirectory(Path.Combine(mappedCloud, "directory link")) == CopyEngine.CanonicalDirectory(external), "Cached directory junction preserves its exact target instead of becoming an empty ordinary folder");
                Assert(Directory.GetLastWriteTimeUtc(mappedCloud) == Directory.GetLastWriteTimeUtc(cloud) && Directory.GetLastWriteTimeUtc(mapped) == Directory.GetLastWriteTimeUtc(source), "Deepest-first cached batches and final ordinary pass preserve parent directory timestamps");
                Assert(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(result.Folders[0].LogPath), "ROBOCOPY").Count >= result.Folders[0].Batches.Count, "All cached and ordinary copy batches append to the selected source's log");
                Assert(snapshots > 0 && messages.Exists(delegate(string text) { return text.StartsWith("Scan: Downloaded OneDrive files") && text.Contains(CopyEngine.CanonicalDirectory(mixed)); }) && messages.IndexOf("Scan complete.") < messages.FindIndex(delegate(string text) { return text.StartsWith("Robocopy running:"); }), "Worker snapshots and current-directory scan records precede the explicit transfer phase");
                Assert(progress[progress.Count - 1].Percent == 100, "Cached aggregate progress reaches completion only after all batches finish");

                string unselected = Path.Combine(sibling, "new unselected");
                string destinationLink = Path.Combine(mappedCloud, "sibling\\new unselected");
                if (MakeJunction(destinationLink, external))
                {
                    bool added = false;
                    result = CopyEngine.RunForTests(request, delegate(string line) {
                        if (added || !line.StartsWith("Checking existing destination links:")) return;
                        added = true; Directory.CreateDirectory(unselected);
                        File.WriteAllText(Path.Combine(unselected, "untouched.txt"), "Must not be written through destination link.");
                    }, CancellationToken.None, new string[] { cloud });
                    Assert(added && result.Status == "Completed" && File.ReadAllText(Path.Combine(external, "untouched.txt")) == "Synthetic target must remain untouched.", "Update preflight and copying ignore unselected cached paths even when an existing destination link points elsewhere");
                }

                string workerRoot = Path.Combine(mixed, "owned worker scratch"); Directory.CreateDirectory(workerRoot);
                File.WriteAllText(Path.Combine(workerRoot, "helper.bin"), "Not source data.");
                request.Sources = new string[] { mixed }; request.WorkerTemporaryFolder = workerRoot; request.SaveLogs = false;
                result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
                Assert(result.Status == "Completed" && result.Folders[0].Batches.TrueForAll(delegate(FolderResult batch) { return batch.FlatCopy; }) && !Directory.Exists(Path.Combine(result.Folders[0].Destination, "owned worker scratch")), "A selected OneDrive descendant uses only positive cached batches and excludes the entire worker scratch root");
                File.WriteAllBytes(local, new byte[47]);
                using (FileStream held = File.Open(local, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
                    Assert(result.Status == "Failed" && result.FailedCount == 1 && result.Folders[0].ExitCode >= 8, "A failed cached batch marks the selected source incomplete");
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        result = CopyEngine.RunForTests(request, delegate(string text) { if (text.StartsWith("Robocopy running:")) cancel.Cancel(); }, cancel.Token, new string[] { cloud });
                        Assert(result.Cancelled && result.Folders[0].Status == "Cancelled" && Directory.GetFileSystemEntries(temporary).Length == 0, "Cached-batch cancellation stops its child and cleans logless temporary files");
                    }
                }

                string wide = Path.Combine(cloud, "wide Unicode selection"); Directory.CreateDirectory(wide);
                for (int i = 0; i < 160; i++) File.WriteAllBytes(Path.Combine(wide, i.ToString("D3") + " 中文 " + new string('x', 120) + ".txt"), new byte[1]);
                request.Sources = new string[] { wide }; request.WorkerTemporaryFolder = null;
                result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
                Assert(result.Status == "Completed" && result.EstimatedTotalBytes == 160 && result.Folders[0].Batches.Count > 1 && Directory.GetFiles(result.Folders[0].Destination).Length == 160, "Huge exact-name cached selections split safely into Unicode command batches without broadening file selection");
            }
            finally
            {
                CopyEngine.StateChanged = null;
                Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp);
            }
        }

        private static void WriteFixtureStream(string path, byte[] bytes)
        {
            using (SafeFileHandle handle = CreateFixtureFile(@"\\?\" + path, 0x40000000, 7, IntPtr.Zero, 2, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                using (FileStream stream = new FileStream(handle, FileAccess.Write)) stream.Write(bytes, 0, bytes.Length);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFixtureFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        private static bool MakeJunction(string path, string target)
        {
            ProcessStartInfo info = new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:ROBO_TEST_LINK -Target $env:ROBO_TEST_TARGET | Out-Null\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            info.EnvironmentVariables["ROBO_TEST_LINK"] = path;
            info.EnvironmentVariables["ROBO_TEST_TARGET"] = target;
            using (Process process = Process.Start(info))
            {
                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) Console.WriteLine("Junction unavailable: " + errors + output);
                return process.ExitCode == 0;
            }
        }

        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                return Convert.ToBase64String(algorithm.ComputeHash(file));
        }

        private static void Assert(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            checks++;
            Console.WriteLine("PASS: " + description);
        }

        private static void ExpectFailure(Action action, string description)
        {
            try { action(); }
            catch (InvalidOperationException) { Assert(true, description); return; }
            throw new Exception("FAIL: " + description + " (accepted unexpectedly)");
        }
    }
}
