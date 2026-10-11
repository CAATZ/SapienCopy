using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace SapienCopy
{
    internal static class ResumeTests
    {
        private static int checks;
        public static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            string root = Path.Combine(args.Length == 0 ? Environment.CurrentDirectory : Path.GetFullPath(args[0]), "resume-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine("Synthetic fixture root: " + root);
            try
            {
                Modes(root);
                IdentityFailures(root);
                OneDriveResume(root);
                RootBatches(root);
                Console.WriteLine("PASS: " + checks + " pause/resume assertions.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { CopyEngine.StateChanged = null; }
        }

        private static void Modes(string root)
        {
            string source = Make(root, "Source ñ"), child = Make(source, "folder");
            File.WriteAllText(Path.Combine(source, ".settings.json"), "synthetic dotfile");
            File.WriteAllText(Path.Combine(child, "-selected.txt"), "exact option-like filename");
            File.WriteAllText(Path.Combine(source, "unselected.txt"), "do not copy sibling");
            foreach (int mode in new int[] { 0, 1, 2 })
            {
                string destination = Make(root, "Mode" + mode);
                CopyRequest request = new CopyRequest { Sources = new string[] { Path.Combine(source, ".settings.json"), child }, Destination = destination,
                    DirectCopy = mode == 0, UpdateExisting = mode == 1, SaveLogs = mode != 0 };
                CopySession preparing = null;
                CopyEngine.StateChanged = delegate(CopyResult state) { if (state.Session != null) preparing = state.Session; };
                CancellationTokenSource stop = new CancellationTokenSource();
                CopyResult paused = CopyEngine.Run(request, delegate(string message) { if (message == "Scan complete.") stop.Cancel(); }, stop.Token);
                Assert(paused.Cancelled && paused.Session != null && Object.ReferenceEquals(preparing, paused.Session), "Session is available before copy and survives pause in mode " + mode);
                string run = paused.RunFolder;
                Assert(paused.Session.OutputCreated && Directory.Exists(run), "Original output exists before pause in mode " + mode);
                request.Session = paused.Session;
                request.Destination = "Z:\\not-the-original-drive";
                request.Sources = new string[] { "Z:\\not-the-original-source" };
                List<CopyProgress> progress = new List<CopyProgress>();
                CopyResult resumed = CopyEngine.Run(request, null, CancellationToken.None, null, progress.Add);
                Assert(resumed.Status == "Completed" && resumed.RunFolder == run, "Resume ignores stale letters and reuses exact output in mode " + mode);
                Assert(File.ReadAllText(resumed.Folders[0].Destination) == "synthetic dotfile" && File.ReadAllText(Path.Combine(resumed.Folders[1].Destination, "-selected.txt")) == "exact option-like filename", "Resumed mixed sources copy exact selected data in mode " + mode);
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(resumed.Folders[0].Destination), "unselected.txt")), "Unselected sibling remains excluded in mode " + mode);
                Assert(resumed.EstimateError == null && progress.Exists(delegate(CopyProgress item) { return item.Percent == 100; }), "Sizing supports bound paths and completes in mode " + mode);
                if (mode == 2) Assert(Directory.GetDirectories(destination, "SapienCopy-*").Length == 1, "Resuming dated backup never creates a second dated output");
                CopyResult again = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(again.Status == "Completed" && again.EstimatedTotalBytes == 0, "Completed unchanged files are skipped on another resume in mode " + mode);
                string arguments = CopyEngine.BuildArguments(request, resumed.Folders[0]);
                Assert((arguments.Contains(" /Z ") || arguments.EndsWith(" /Z", StringComparison.Ordinal)) && !arguments.Contains(" /B "), "Normal transfer enables restartable mode");
                request.BackupMode = true;
                arguments = CopyEngine.BuildArguments(request, resumed.Folders[0]);
                Assert(arguments.Contains(" /ZB") && !arguments.Contains(" /B "), "Protected transfer uses restartable mode with backup fallback");
            }
        }

        private static void IdentityFailures(string root)
        {
            string source = Make(root, "Identity source"), destination = Make(root, "Identity destination");
            File.WriteAllText(Path.Combine(source, "data.txt"), "identity fixture");
            CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = destination, SaveLogs = false };
            CancellationTokenSource stop = new CancellationTokenSource();
            CopyResult paused = CopyEngine.Run(request, delegate(string message) { if (message == "Scan complete.") stop.Cancel(); }, stop.Token);
            request.Session = paused.Session;
            uint serial = request.Session.Destination.SerialNumber;
            request.Session.Destination.SerialNumber ^= 1;
            Reject(delegate { CopyEngine.Run(request, null, CancellationToken.None); }, "Wrong destination identity is rejected before any source copy");
            request.Session.Destination.SerialNumber = serial;
            serial = request.Session.Sources[0].SerialNumber;
            request.Session.Sources[0].SerialNumber ^= 1;
            Reject(delegate { CopyEngine.Run(request, null, CancellationToken.None); }, "Wrong source identity is rejected before any source copy");
            request.Session.Sources[0].SerialNumber = serial;
            Assert(Directory.GetFileSystemEntries(paused.RunFolder).Length == 0, "Rejected identities leave the output empty");
            Directory.Delete(paused.RunFolder, false);
            Reject(delegate { CopyEngine.Run(request, null, CancellationToken.None); }, "Missing original dated output is not silently recreated");
            Directory.CreateDirectory(paused.RunFolder);
            Reject(delegate { CopyEngine.Run(request, null, CancellationToken.None); }, "Replacement output at the same path is rejected by directory identity");
        }


        private static void RootBatches(string root)
        {
            string source = Make(root, "Root batch source"), child = Make(source, "child"), excluded = Make(source, "excluded"), output = Make(root, "Root batch output");
            File.WriteAllText(Path.Combine(source, "root.txt"), "root file");
            File.WriteAllText(Path.Combine(child, "child.txt"), "child file");
            File.WriteAllText(Path.Combine(excluded, "private.txt"), "must remain excluded");
            VolumePath sourcePath = VolumeIdentity.Capture(source), outputPath = VolumeIdentity.Capture(output, true);
            Assert(CopyEngine.IsVolumeRoot(sourcePath.VolumeRoot) && !CopyEngine.IsVolumeRoot(sourcePath.VolumeRoot + "folder}"), "Volume-root detection does not misclassify names ending with a brace");
            MethodInfo begin = typeof(CopyEngine).GetMethod("BeginSession", BindingFlags.NonPublic | BindingFlags.Static);
            CopyRequest rootRequest = (CopyRequest)begin.Invoke(null, new object[] { new CopyRequest { Sources = new string[] { Path.GetPathRoot(source) }, Destination = output } });
            Assert(rootRequest.Session.OutputRelativePaths[0] == Path.GetPathRoot(source).Substring(0, 1).ToUpperInvariant(), "Whole-drive output layout retains its original letter without an invalid trailing separator");
            using (RobocopyBindings bindings = new RobocopyBindings(new VolumePath[] { sourcePath, outputPath }))
            {
                CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = output, SaveLogs = false, Bindings = bindings, DestinationFileSystem = "NTFS" };
                FolderResult folder = new FolderResult { Source = VolumeIdentity.BoundPath(sourcePath), Destination = VolumeIdentity.BoundPath(outputPath), ScanLogPath = Path.Combine(root, "root-scan.log") };
                OneDrivePlan plan = new OneDrivePlan();
                plan.AddExcluded(new ExcludedPath { Path = Path.Combine(folder.Source, "excluded"), Directory = true });
                MethodInfo prepare = typeof(CopyEngine).GetMethod("PrepareVolumeBatches", BindingFlags.NonPublic | BindingFlags.Static);
                List<FolderResult> batches = (List<FolderResult>)prepare.Invoke(null, new object[] { request, folder, plan, null });
                Assert(batches.Count == 2 && !batches[0].RootFiles && batches[1].RootFiles, "Whole-volume plan splits child trees from root files and omits excluded children");
                string arguments = CopyEngine.BuildArguments(request, batches[1]);
                Assert(arguments.Contains(" /NODCOPY") && !arguments.Contains(" /E ") && !arguments.Contains(" /SJ "), "Root-files batch cannot copy the private volume junction itself");
                long total = 0;
                foreach (FolderResult batch in batches)
                {
                    total += CopyEstimate.Scan(request, batch, null, CancellationToken.None);
                    using (Process process = new Process())
                    {
                        process.StartInfo = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"), Arguments = CopyEngine.BuildArguments(request, batch), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        process.OutputDataReceived += delegate { }; process.ErrorDataReceived += delegate { };
                        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        if (!process.WaitForExit(10000)) { CopyEngine.Stop(process); throw new Exception("Root batch timed out."); }
                        process.WaitForExit(); Assert(process.ExitCode < 8, "Synthetic volume-root batch copies successfully");
                    }
                }
                Assert(total == new FileInfo(Path.Combine(source, "root.txt")).Length + new FileInfo(Path.Combine(child, "child.txt")).Length, "Whole-volume sizing counts root and child file bytes once");
                Assert(File.Exists(Path.Combine(output, "root.txt")) && File.Exists(Path.Combine(output, "child\\child.txt")) && !Directory.Exists(Path.Combine(output, "excluded")) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) == 0, "Whole-volume split produces ordinary copied data and honors exclusions");
            }
        }

        private static void OneDriveResume(string root)
        {
            string source = Make(root, "Synthetic OneDrive"), output = Make(root, "OneDrive resumed output");
            File.WriteAllText(Path.Combine(source, "local.txt"), "downloaded fixture");
            string offline = Path.Combine(source, "offline.txt");
            File.WriteAllText(offline, "synthetic metadata only"); File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
            CopyRequest request = new CopyRequest { Sources = new string[] { source }, Destination = output, DirectCopy = true, SaveLogs = false, OneDriveMode = OneDriveMode.DownloadedOnly };
            CancellationTokenSource stop = new CancellationTokenSource();
            CopyResult paused = CopyEngine.RunForTests(request, delegate(string message) { if (message == "Scan complete.") stop.Cancel(); }, stop.Token, new string[] { source });
            Assert(paused.Cancelled && paused.Session.OneDriveRoots.Length == 1, "Pause captures the relevant OneDrive volume before it becomes resumable");
            request.Session = paused.Session;
            CopyResult resumed = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[0]);
            Assert(resumed.Status == "Completed" && File.Exists(Path.Combine(resumed.Folders[0].Destination, "local.txt")) && !File.Exists(Path.Combine(resumed.Folders[0].Destination, "offline.txt")), "Resume retains downloaded-only classification even when root discovery loses the old letter");
        }

        private static string Make(string parent, string name) { return Directory.CreateDirectory(Path.Combine(parent, name)).FullName; }
        private static void Reject(Action action, string description) { try { action(); } catch (Exception) { Assert(true, description); return; } throw new Exception("FAIL: " + description); }
        private static void Assert(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); checks++; Console.WriteLine("PASS: " + description); }
    }
}
