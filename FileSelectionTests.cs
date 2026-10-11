using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace SapienCopy
{
    internal static class FileSelectionTests
    {
        private static int checks, skipped;

        public static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            string oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                string root = Directory.CreateDirectory(Path.Combine(args.Length == 0 ? Environment.CurrentDirectory : args[0], "file-fixtures-" + Guid.NewGuid().ToString("N"))).FullName;
                Console.WriteLine("Synthetic fixture root: " + root);
                string scratch = Directory.CreateDirectory(Path.Combine(root, "scratch")).FullName;
                Environment.SetEnvironmentVariable("TEMP", scratch); Environment.SetEnvironmentVariable("TMP", scratch);
                TestCopies(root);
                TestPolicies(root);
                TestSafety(root);
                TestOptionNames(root);
                Assert(Directory.GetFileSystemEntries(scratch).Length == 0, "No-log file transfers clean their owned temporary files");
                Console.WriteLine("PASS: " + checks + " individual-file assertions; " + skipped + " unavailable link checks.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
        }

        private static void TestCopies(string root)
        {
            string parent = MakeDirectory(root, "source parent"), output = MakeDirectory(root, "direct");
            string file = Path.Combine(parent, ".settings ñ.json");
            File.WriteAllText(file, "{\"synthetic\":true}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(parent, "unselected.txt"), "Do not copy me.");
            string child = MakeDirectory(parent, "unselected child");
            File.WriteAllText(Path.Combine(child, Path.GetFileName(file)), "Do not recurse.");
            File.SetAttributes(parent, File.GetAttributes(parent) | FileAttributes.Hidden | FileAttributes.System);
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
            CopyRequest request = Request(output, file);
            List<CopyProgress> details = new List<CopyProgress>();
            CopyResult result = CopyEngine.Run(request, null, CancellationToken.None, null, details.Add);
            Assert(result.Status == "Completed" && result.CopiedCount == 1, "Direct selected dotfile with Unicode name completes");
            Assert(CopyEngine.CanonicalDirectory(result.Folders[0].Source) == CopyEngine.CanonicalDirectory(file) && CopyEngine.CanonicalDirectory(result.Folders[0].Destination) == CopyEngine.CanonicalDirectory(Path.Combine(output, Path.GetFileName(file))), "Result paths identify the actual file");
            Assert(File.ReadAllText(result.Folders[0].Destination) == File.ReadAllText(file), "Selected file bytes match");
            Assert(Directory.GetFileSystemEntries(output).Length == 1, "No siblings, child folders, logs or wrappers leak into Direct copy");
            Assert((File.GetAttributes(output) & (FileAttributes.Hidden | FileAttributes.System)) == 0, "Unselected parent directory attributes do not alter destination");
            Assert((File.GetAttributes(result.Folders[0].Destination) & FileAttributes.Hidden) != 0, "Selected file attributes are preserved");
            Assert(result.EstimatedTotalBytes == new FileInfo(file).Length && result.EstimateError == null && details.Exists(delegate(CopyProgress item) { return item.Percent == 100; }), "File-only sizing measures exact bytes and reaches complete progress");
            string arguments = CopyEngine.BuildArguments(request, result.Folders[0]);
            Assert(arguments.Contains(" /NODCOPY") && !arguments.Contains(" /E ") && arguments.Contains(CopyEngine.Quote(Path.GetFileName(file))), "File batch is exact-name and nonrecursive with no parent metadata copy");
            CopyRequest normalized = CopyEngine.Validate(Request(output, file, file.ToUpperInvariant()));
            Assert(normalized.Sources.Length == 1, "Repeated file paths normalize once");
            normalized = CopyEngine.Validate(Request(output, file, parent));
            Assert(normalized.Sources.Length == 1 && normalized.Sources[0] == parent, "Selected parent covers an earlier individual file");
            normalized = CopyEngine.Validate(Request(output, parent, file));
            Assert(normalized.Sources.Length == 1 && normalized.Sources[0] == parent, "Selected parent covers a later individual file");

            string selectedFolder = MakeDirectory(root, "selected folder");
            File.WriteAllText(Path.Combine(selectedFolder, "inside.txt"), "Mixed source selection.");
            result = CopyEngine.Run(Request(MakeDirectory(root, "mixed"), file, selectedFolder), null, CancellationToken.None);
            Assert(result.Status == "Completed" && result.CopiedCount == 2 && File.Exists(result.Folders[0].Destination) && File.Exists(Path.Combine(result.Folders[1].Destination, "inside.txt")), "Mixed files and folders copy together");

            request = Request(MakeDirectory(root, "dated"), file); request.DirectCopy = false; request.SaveLogs = true;
            result = CopyEngine.Run(request, null, CancellationToken.None);
            string mapped = CopyEngine.DestinationFor(result.RunFolder, file);
            Assert(result.Status == "Completed" && File.Exists(mapped) && CopyEngine.CanonicalDirectory(result.Folders[0].Destination) == CopyEngine.CanonicalDirectory(mapped), "Dated backup retains selected file drive-relative path");
            Assert(Directory.GetFiles(Path.GetDirectoryName(mapped)).Length == 1, "Dated file selection excludes siblings");
            Assert(File.ReadAllText(Path.Combine(result.ReportFolder, "Summary.csv")).Contains(result.Folders[0].Source) && File.Exists(result.Folders[0].LogPath), "Verified file selection is recorded in report and Robocopy log");
            string extra = Path.Combine(Path.GetDirectoryName(mapped), "destination-only.txt"); File.WriteAllText(extra, "Keep me.");
            request.Destination = result.RunFolder; request.UpdateExisting = true;
            result = CopyEngine.Run(request, null, CancellationToken.None);
            Assert(result.Status == "Completed" && result.EstimatedTotalBytes == 0 && File.Exists(extra), "Update skips unchanged selected file and retains destination extras");
            File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllText(file, "Changed synthetic config content with a different length.");
            result = CopyEngine.Run(request, null, CancellationToken.None);
            Assert(result.Status == "Completed" && result.EstimatedTotalBytes == new FileInfo(file).Length && File.ReadAllText(mapped) == File.ReadAllText(file) && File.Exists(extra), "Update copies changed selected file and retains extras");
            File.SetAttributes(parent, FileAttributes.Directory);
        }

        private static void TestPolicies(string root)
        {
            string cloud = MakeDirectory(root, "synthetic OneDrive");
            string file = Path.Combine(cloud, "selected.json"), offline = Path.Combine(cloud, "offline.json");
            File.WriteAllText(file, "Cached file."); File.WriteAllText(offline, "Offline-marked fixture.");
            File.SetAttributes(offline, File.GetAttributes(offline) | FileAttributes.Offline);
            CopyRequest request = Request(MakeDirectory(root, "cloud copied"), file);
            request.OneDriveMode = OneDriveMode.DownloadedOnly;
            CopyResult result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
            Assert(result.Status == "Completed" && result.CopiedCount == 1 && result.EstimatedTotalBytes == new FileInfo(file).Length && File.Exists(result.Folders[0].Destination), "Downloaded-only selected cached file copies and sizes without its siblings");
            Assert(Directory.GetFileSystemEntries(request.Destination).Length == 1, "Downloaded-only exact file selection does not enumerate into a broad copy");
            request = Request(MakeDirectory(root, "cloud excluded"), file); request.OneDriveMode = OneDriveMode.Exclude;
            result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
            Assert(result.SkippedCount == 1 && !File.Exists(result.Folders[0].Destination) && result.Exclusions[0].Directory == false, "Exclude OneDrive skips a selected file with a file exclusion record");
            request = Request(MakeDirectory(root, "offline skipped"), offline); request.OneDriveMode = OneDriveMode.DownloadedOnly;
            result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
            Assert(result.SkippedCount == 1 && result.EstimatedTotalBytes == 0 && !File.Exists(result.Folders[0].Destination), "Downloaded-only skips a selected offline file before sizing or copying");
            request = Request(MakeDirectory(root, "offline all"), offline);
            result = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cloud });
            Assert(result.CopiedCount == 1 && File.Exists(result.Folders[0].Destination), "All-files mode permits an ordinary offline-marked file");
            request = Request(MakeDirectory(root, "manual excluded"), file); request.ExcludedFolders = new string[] { cloud };
            result = CopyEngine.Run(request, null, CancellationToken.None);
            Assert(result.SkippedCount == 1 && result.ExcludedCount == 1 && !File.Exists(result.Folders[0].Destination), "Excluded containing folder skips an individual file");
            foreach (string format in new string[] { "exFAT", "FAT32" })
            {
                request = Request(MakeDirectory(root, "portable " + format), file);
                result = CopyEngine.RunForTests(request, null, CancellationToken.None, null, null, null, format);
                Assert(result.Status == "Completed" && File.ReadAllText(result.Folders[0].Destination) == File.ReadAllText(file), "Selected file passes simulated " + format + " compatibility and copies");
            }
            Assert(DestinationPolicy.UnsupportedReason("FAT32", 0, 0, (ulong)UInt32.MaxValue + 1) != null, "Individual-file FAT32 oversized metadata is rejected");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                request = Request(MakeDirectory(root, "cancelled file"), file);
                result = CopyEngine.Run(request, delegate(string message) { if (message.StartsWith("Estimating copy size:")) cancel.Cancel(); }, cancel.Token);
                Assert(result.Cancelled && result.Status == "Cancelled" && !File.Exists(result.Folders[0].Destination), "Individual-file sizing cancellation creates no destination data");
            }
            File.SetAttributes(offline, FileAttributes.Normal);
        }

        private static void TestSafety(string root)
        {
            string a = MakeDirectory(root, "safe source A"), b = MakeDirectory(root, "safe source B"), output = MakeDirectory(root, "safe output");
            string file = Path.Combine(a, "same.txt"), other = Path.Combine(b, "same.txt");
            File.WriteAllText(file, "A"); File.WriteAllText(other, "B");
            Fail(delegate { CopyEngine.Validate(Request(output, file, other)); }, "Direct duplicate file basenames are rejected");
            string folder = MakeDirectory(b, "same.txt folder");
            string collisionFile = Path.Combine(a, Path.GetFileName(folder)); File.WriteAllText(collisionFile, "Collision.");
            Fail(delegate { CopyEngine.Validate(Request(output, collisionFile, folder)); }, "Direct file/folder basename collisions are rejected");
            Fail(delegate { CopyEngine.Validate(Request(a, file)); }, "Direct output onto selected source file is rejected");
            Fail(delegate { CopyEngine.Validate(Request(output, file + ":stream")); }, "Alternate-data-stream paths are rejected as individual sources");
            Directory.CreateDirectory(Path.Combine(output, Path.GetFileName(file)));
            Fail(delegate { CopyEngine.Run(Request(output, file), null, CancellationToken.None); }, "Existing destination folder cannot replace a selected file");
            string reverse = MakeDirectory(root, "reverse conflict");
            File.WriteAllText(Path.Combine(reverse, Path.GetFileName(folder)), "Retain existing file.");
            Fail(delegate { CopyEngine.Run(Request(reverse, folder), null, CancellationToken.None); }, "Existing destination file cannot replace a selected folder");
            string blocked = MakeDirectory(root, "blocked update"); File.WriteAllText(Path.Combine(blocked, "C"), "Block an intermediate directory.");
            CopyRequest update = Request(blocked, file); update.DirectCopy = false; update.UpdateExisting = true;
            Fail(delegate { CopyEngine.Run(update, null, CancellationToken.None); }, "Update rejects an existing file where an output ancestor folder is needed");

            string linkOutput = MakeDirectory(root, "file link output"), target = Path.Combine(b, "protected target.txt"); File.WriteAllText(target, "Retain target.");
            string link = Path.Combine(linkOutput, Path.GetFileName(file));
            if (CreateSymbolicLink(link, target, 2))
            {
                Fail(delegate { CopyEngine.Run(Request(linkOutput, file), null, CancellationToken.None); }, "Existing destination file symlink cannot redirect a copy");
                Assert(File.ReadAllText(target) == "Retain target.", "Rejected destination symlink target is untouched");
                string sourceLink = Path.Combine(a, "source link.txt");
                if (!CreateSymbolicLink(sourceLink, target, 2)) throw new Exception("Cannot create second synthetic file link.");
                CopyResult result = CopyEngine.Run(Request(MakeDirectory(root, "copied file link"), sourceLink), null, CancellationToken.None);
                Assert(result.Status == "Completed" && (File.GetAttributes(result.Folders[0].Destination) & FileAttributes.ReparsePoint) != 0, "Selected source file symlink is preserved as a link on NTFS");
                Fail(delegate { CopyEngine.RunForTests(Request(MakeDirectory(root, "portable link"), sourceLink), null, CancellationToken.None, null, null, null, "exFAT"); }, "Portable destination rejects selected file symlink before copying");
            }
            else { skipped++; Console.WriteLine("SKIP: File symlink checks require symbolic-link permission (Windows error " + Marshal.GetLastWin32Error() + ")."); }
        }

        private static void TestOptionNames(string root)
        {
            string source = MakeDirectory(root, "option-like names");
            string[] names = new string[] { "-MIR", "-PURGE", "-MOVE", "-", "--config.json", "-á ñ 文.txt", "-a.b.c" };
            foreach (string name in names)
            {
                string file = Path.Combine(source, name); File.WriteAllText(file, "Exact fixture " + name, Encoding.UTF8);
                foreach (string prefix in new string[] { "x", ".", "..", "x." }) File.WriteAllText(Path.Combine(source, prefix + name), "Unselected confusable neighbor.");
            }
            foreach (string name in names)
            {
                string file = Path.Combine(source, name), output = MakeDirectory(root, "single option " + Array.IndexOf(names, name));
                string sentinel = Path.Combine(output, "destination-only.txt"); File.WriteAllText(sentinel, "Keep me.");
                CopyResult result = CopyEngine.Run(Request(output, file), null, CancellationToken.None);
                Assert(result.Status == "Completed" && result.EstimateError == null && result.EstimatedTotalBytes == new FileInfo(file).Length && File.ReadAllText(result.Folders[0].Destination) == File.ReadAllText(file), "Option-like selected filename copies with exact sizing: " + name);
                Assert(Directory.GetFiles(output).Length == 2 && File.Exists(sentinel) && File.Exists(file), "Option-like filename cannot select neighbors, purge destination or move source: " + name);
            }
            string cached = MakeDirectory(root, "cached option names");
            foreach (string name in names)
            {
                File.WriteAllText(Path.Combine(cached, name), "Selected " + name, Encoding.UTF8);
                File.WriteAllText(Path.Combine(cached, "x" + name), "Also selected.");
            }
            string unavailable = Path.Combine(cached, ".-MOVE"); File.WriteAllText(unavailable, "Skip offline neighbor.");
            File.SetAttributes(unavailable, File.GetAttributes(unavailable) | FileAttributes.Offline);
            CopyRequest request = Request(MakeDirectory(root, "cached option output"), cached); request.OneDriveMode = OneDriveMode.DownloadedOnly;
            long expected = 0;
            foreach (string name in names) expected += new FileInfo(Path.Combine(cached, name)).Length + new FileInfo(Path.Combine(cached, "x" + name)).Length;
            CopyResult copied = CopyEngine.RunForTests(request, null, CancellationToken.None, new string[] { cached });
            Assert(copied.Status == "Completed" && copied.EstimateError == null && copied.EstimatedTotalBytes == expected, "Cached folder option-like filenames retain complete percentage sizing");
            Assert(Directory.GetFiles(copied.Folders[0].Destination).Length == names.Length * 2 && !File.Exists(Path.Combine(copied.Folders[0].Destination, Path.GetFileName(unavailable))), "Cached option batches retain other selected prefix names and exclude offline neighbors");
            foreach (string name in names) Assert(File.Exists(Path.Combine(cached, name)) && File.Exists(Path.Combine(copied.Folders[0].Destination, name)), "Cached option name remains data rather than a Robocopy switch: " + name);
            File.SetAttributes(unavailable, FileAttributes.Normal);
        }

        private static CopyRequest Request(string destination, params string[] sources)
        {
            return new CopyRequest { Sources = sources, Destination = destination, DirectCopy = true, SaveLogs = false, Threads = 2 };
        }
        private static string MakeDirectory(string root, string name) { return Directory.CreateDirectory(Path.Combine(root, name)).FullName; }
        private static void Assert(bool condition, string description)
        {
            if (!condition) throw new Exception("FAIL: " + description);
            checks++; Console.WriteLine("PASS: " + description);
        }
        private static void Fail(Action action, string description)
        {
            try { action(); }
            catch (InvalidOperationException) { Assert(true, description); return; }
            throw new Exception("FAIL: " + description + " (accepted unexpectedly)");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, uint flags);
    }
}
