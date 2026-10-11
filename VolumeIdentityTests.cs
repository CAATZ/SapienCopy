using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace SapienCopy
{
    internal static class VolumeIdentityTests
    {
        private static int checks, skipped;

        public static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            try
            {
                TestIdentity();
                string root = Directory.CreateDirectory(Path.Combine(args.Length == 0 ? Environment.CurrentDirectory : args[0], "volume-fixtures-" + Guid.NewGuid().ToString("N"))).FullName;
                Console.WriteLine("Synthetic fixture root: " + root);
                TestLocal(root);
                Console.WriteLine("PASS: " + checks + " volume identity assertions; " + skipped + " unavailable symlink checks.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void TestIdentity()
        {
            Assert(VolumeIdentity.IsNameSurrogateTag(0xA0000003u) && VolumeIdentity.IsNameSurrogateTag(0xA000000Cu), "Only path-surrogate junctions and symlinks require target validation");
            Assert(!VolumeIdentity.IsNameSurrogateTag(0x9000001Au) && !VolumeIdentity.IsNameSurrogateTag(0x9000F01Au), "OneDrive cloud reparse tags are not followed while capturing cached source identity");
            string root = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
            VolumePath saved = new VolumePath { VolumeRoot = root, SerialNumber = 123, FileSystem = "NTFS", RelativePath = @"Backups\MyFiles", OriginalPath = @"F:\Backups\MyFiles", IsDirectory = true };
            Assert(VolumeIdentity.BoundPath(saved) == root + @"Backups\MyFiles", "Bound destination contains GUID and relative path, never saved drive letter");
            string requestedRoot = null;
            string bound = VolumeIdentity.VerifyVolume(saved, delegate(string requested) { requestedRoot = requested; return new VolumeIdentity.VolumeDetails { Root = root, SerialNumber = 123, FileSystem = "NTFS" }; });
            Assert(requestedRoot == root && bound == VolumeIdentity.BoundPath(saved), "Reconnection lookup asks for exact original volume GUID");
            Assert(VolumeIdentity.FriendlyPath(saved, new string[] { @"G:\" }) == @"G:\Backups\MyFiles", "Same volume under new letter displays new path");
            saved.OriginalPath = @"F:\NowAnotherDrive";
            Assert(VolumeIdentity.VerifyVolume(saved, delegate(string requested) { Assert(requested != "F:", "Reassigned old letter is never looked up"); return new VolumeIdentity.VolumeDetails { Root = root, SerialNumber = 123, FileSystem = "NTFS" }; }) == bound, "Old letter cannot select a different destination");
            Throws(delegate { VolumeIdentity.VerifyVolume(saved, delegate(string requested) { return null; }); }, "Missing original drive blocks resume");
            Throws(delegate { VolumeIdentity.VerifyVolume(saved, delegate(string requested) { return new VolumeIdentity.VolumeDetails { Root = @"\\?\Volume{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}\", SerialNumber = 123, FileSystem = "NTFS" }; }); }, "Wrong volume rejects even matching filesystem and serial");
            Throws(delegate { VolumeIdentity.VerifyVolume(saved, delegate(string requested) { return new VolumeIdentity.VolumeDetails { Root = root, SerialNumber = 456, FileSystem = "NTFS" }; }); }, "Reformatted or changed serial rejects");
            Throws(delegate { VolumeIdentity.VerifyVolume(saved, delegate(string requested) { return new VolumeIdentity.VolumeDetails { Root = root, SerialNumber = 123, FileSystem = "exFAT" }; }); }, "Changed filesystem rejects");
            Assert(VolumeIdentity.FriendlyPath(saved, new string[0]) == bound, "Unmounted volume display falls back to GUID");
            Assert(VolumeIdentity.FriendlyPath(saved, new string[] { @"C:\Mounts\Drive\", @"G:\" }) == @"G:\Backups\MyFiles", "Shortest mounted drive path is displayed");
            Assert(VolumeIdentity.FriendlyPath(saved, new string[] { @"A:\", @"C:\" }).StartsWith(@"A:\"), "Display mount lookup may choose an auxiliary drive letter");
            Assert(VolumeIdentity.ScratchPath(saved, @"C:\Windows") == @"C:\Backups\MyFiles", "Private scratch I/O uses Windows' own drive root instead of the auxiliary display letter");
            Assert(VolumeIdentity.BoundPath(VolumeIdentity.Child(saved, @"Dated\child", true)) == bound + @"\Dated\child", "Planned folders keep the original volume and relative hierarchy");
            saved.HasFileId = true; saved.FileIdLow = 42;
            Assert(VolumeIdentity.Child(saved, "", true).HasFileId && VolumeIdentity.Child(saved, "", true).FileIdLow == 42, "Same-folder child retains saved directory identity");
            Assert(!VolumeIdentity.Child(saved, "New folder", true).HasFileId, "Planned child does not claim a nonexistent entry identity");
            foreach (string invalid in new string[] { @"..\escape", @"a\..\escape", @".\escape", @"\absolute", @"F:\absolute", @"file:stream", @"a\\b", "a/b", "*", null })
            {
                string value = invalid;
                Throws(delegate { VolumeIdentity.Child(saved, value, true); }, "Invalid relative path is rejected: " + value);
            }
            saved.VolumeRoot = @"F:\";
            Throws(delegate { VolumeIdentity.BoundPath(saved); }, "Drive-letter binding cannot bypass volume identity");
        }

        private static void TestLocal(string root)
        {
            string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            File.WriteAllText(Path.Combine(source, ".settings.json"), "{\"synthetic\":true}");
            VolumePath selected = VolumeIdentity.Capture(source), output = VolumeIdentity.Capture(destination, true);
            Assert(VolumeIdentity.NormalizeScratchLocation(source) == source, "An existing scratch path resolves to the stable Windows drive root");
            Assert(VolumeIdentity.NormalizeScratchLocation(Path.Combine(source, "planned", "scratch")) == Path.Combine(source, "planned", "scratch"), "Planned scratch descendants normalize before they are created");
            Assert(selected.VolumeRoot.StartsWith(@"\\?\Volume{") && selected.SerialNumber == output.SerialNumber, "Actual workspace capture obtains same GUID/serial for source and destination");
            Assert(VolumeIdentity.Verify(selected) == VolumeIdentity.BoundPath(selected), "Actual source verifies its GUID path");
            Assert(VolumeIdentity.Verify(output, true, true) == VolumeIdentity.BoundPath(output), "Actual destination verifies path, volume, and directory ID");
            Assert(VolumeIdentity.FriendlyPath(output).Equals(destination, StringComparison.OrdinalIgnoreCase), "Actual GUID maps back to current drive-letter destination");
            VolumePath file = VolumeIdentity.Capture(Path.Combine(source, ".settings.json"));
            Assert(!file.IsDirectory && File.ReadAllText(VolumeIdentity.Verify(file)).Contains("synthetic"), "Individual file binding preserves filename and reads through GUID");
            File.AppendAllText(Path.Combine(source, ".settings.json"), "\r\n");
            Assert(VolumeIdentity.Verify(file) == VolumeIdentity.BoundPath(file), "Source file content updates do not invalidate path identity");
            VolumePath planned = VolumeIdentity.Child(output, @"Dated\nested", true);
            Assert(VolumeIdentity.Verify(planned, false, true) == VolumeIdentity.BoundPath(planned), "Uncreated nested output verifies existing safe ancestors");
            Throws(delegate { VolumeIdentity.Verify(planned, true, true); }, "Missing expected output folder rejects resume");
            File.WriteAllText(VolumeIdentity.BoundPath(VolumeIdentity.Child(output, "guid-write.txt", false)), "GUID path write");
            Assert(File.ReadAllText(Path.Combine(destination, "guid-write.txt")) == "GUID path write", ".NET GUID writes reach exact workspace destination");
            using (RobocopyBindings bindings = new RobocopyBindings(new VolumePath[] { selected, output }, root))
            {
                Assert(bindings.ToVolumePath(bindings.ToRobocopy(VolumeIdentity.BoundPath(file))) == VolumeIdentity.BoundPath(file), "Private junction path round-trips to original GUID for scan parsing");
                Assert(bindings.ToVolumePath(@"\\?\" + bindings.ToRobocopy(VolumeIdentity.BoundPath(file))) == VolumeIdentity.BoundPath(file), "Extended-length log paths normalize without duplicated device prefix");
                Assert(Directory.GetDirectories(bindings.DirectoryPath).Length == 1, "Sources and destination on one volume share one private binding");
                RunRobocopy(selected, output, bindings);
                string longDirectory = VolumeIdentity.BoundPath(selected) + "\\" + new string('a', 90) + "\\" + new string('b', 90);
                Directory.CreateDirectory(longDirectory);
                File.WriteAllText(Path.Combine(longDirectory, ".settings.json"), "Long-path synthetic file.");
                VolumePath longSource = VolumeIdentity.Capture(longDirectory);
                Assert(bindings.ToRobocopy(VolumeIdentity.BoundPath(longSource)).Length > 260, "Long-path fixture exceeds MAX_PATH after private binding");
                RunRobocopy(longSource, output, bindings);
                Assert(File.ReadAllText(Path.Combine(destination, ".settings.json")) == "Long-path synthetic file.", "Robocopy handles long source paths through regular DOS aliases");
                RunRobocopy(selected, output, bindings);
            }
            Assert(Directory.GetDirectories(root, "SapienCopyBindings-*").Length == 0, "Private junctions and binding folder are removed without touching targets");
            Assert(File.ReadAllText(Path.Combine(destination, ".settings.json")) == File.ReadAllText(Path.Combine(source, ".settings.json")), "Robocopy /Z copies exact file via GUID source and destination");

            Directory.Move(destination, destination + "-original");
            Directory.CreateDirectory(destination);
            Throws(delegate { VolumeIdentity.Verify(output, true, true); }, "Deleted/recreated destination path is rejected by directory ID");
            Assert(VolumeIdentity.Verify(output) == VolumeIdentity.BoundPath(output), "Non-destination path verification permits ordinary source directory changes");

            string junction = Directory.CreateDirectory(Path.Combine(root, "junction")).FullName;
            SetJunction(junction, VolumeIdentity.BoundPath(selected));
            VolumePath selectedJunction = VolumeIdentity.Capture(junction);
            Assert(VolumeIdentity.BoundPath(selectedJunction).EndsWith("\\junction") && selectedJunction.LinkTargetPath == VolumeIdentity.BoundPath(selected), "Final source junction remains a link entry with a separately verified target");
            Assert(VolumeIdentity.Verify(selectedJunction) == VolumeIdentity.BoundPath(selectedJunction), "Unchanged source junction verifies");
            Throws(delegate { VolumeIdentity.Capture(junction, true); }, "Destination junction is rejected before copying");
            string redirectTarget = Directory.CreateDirectory(Path.Combine(root, "redirect-target")).FullName;
            Directory.Delete(junction, false);
            Directory.CreateDirectory(junction);
            SetJunction(junction, VolumeIdentity.BoundPath(VolumeIdentity.Capture(redirectTarget)));
            Throws(delegate { VolumeIdentity.Verify(selectedJunction); }, "Changed source or excluded-folder junction target rejects resume");
            Directory.Delete(junction, false);
            Directory.CreateDirectory(junction);
            VolumePath savedDirectory = VolumeIdentity.Capture(junction, true);
            Directory.Move(junction, junction + "-original");
            Directory.CreateDirectory(junction);
            SetJunction(junction, VolumeIdentity.BoundPath(selected));
            Throws(delegate { VolumeIdentity.Verify(savedDirectory, true, true); }, "Destination replaced by junction rejects resume");
            Directory.Delete(junction, false);
            Assert(File.Exists(Path.Combine(source, ".settings.json")), "Unlinking test junction never removes target files");

            string wholeVolumeFixture = Directory.CreateDirectory(Path.Combine(root, "whole-volume-style-source")).FullName;
            string wholeVolumeOutput = Directory.CreateDirectory(Path.Combine(root, "whole-volume-style-destination")).FullName;
            Directory.CreateDirectory(Path.Combine(wholeVolumeFixture, "child"));
            File.WriteAllText(Path.Combine(wholeVolumeFixture, "root.txt"), "Synthetic volume-root file.");
            File.WriteAllText(Path.Combine(wholeVolumeFixture, "child", "inside.txt"), "Only synthetic fixture content.");
            string rootJunction = Directory.CreateDirectory(Path.Combine(root, "whole-volume-style-alias")).FullName;
            SetJunction(rootJunction, VolumeIdentity.BoundPath(VolumeIdentity.Capture(wholeVolumeFixture)));
            RunRobocopyArguments(Quote(rootJunction + "\\") + " " + Quote(wholeVolumeOutput) + " root.txt /SL /COPY:DAT /NODCOPY /Z /R:0 /W:0 /NFL /NDL /NP /NJS /NJH");
            RunRobocopyArguments(Quote(Path.Combine(rootJunction, "child")) + " " + Quote(Path.Combine(wholeVolumeOutput, "child")) + " /E /SJ /SL /COPY:DAT /DCOPY:DAT /Z /R:0 /W:0 /NFL /NDL /NP /NJS /NJH");
            Assert(File.ReadAllText(Path.Combine(wholeVolumeOutput, "child", "inside.txt")) == "Only synthetic fixture content.", "Whole-volume child folder batches retain recursive /SJ /SL copying");
            Assert(File.ReadAllText(Path.Combine(wholeVolumeOutput, "root.txt")) == "Synthetic volume-root file.", "Whole-volume root files copy separately without /SJ");
            Assert((File.GetAttributes(wholeVolumeOutput) & FileAttributes.ReparsePoint) == 0, "Whole-volume-style root creates ordinary destination folders, not its private binding link");
            Directory.Delete(rootJunction, false);

            string brokenRoot;
            using (RobocopyBindings broken = new RobocopyBindings(new VolumePath[] { new VolumePath {
                VolumeRoot = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", FileSystem = "NTFS", RelativePath = "", IsDirectory = true
            } }, root)) { brokenRoot = broken.DirectoryPath; }
            Assert(!Directory.Exists(brokenRoot), "Private binding cleanup removes a junction whose target volume is unavailable");

            string link = Path.Combine(root, "link"), other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            if (!CreateSymbolicLink(link, source, 3))
            {
                skipped++;
                Console.WriteLine("SKIP: directory symlink creation unavailable (Windows error " + Marshal.GetLastWin32Error() + ").");
            }
            else
            {
                VolumePath linked = VolumeIdentity.Capture(link);
                Assert(VolumeIdentity.BoundPath(linked).EndsWith("\\link") && linked.LinkTargetPath == VolumeIdentity.BoundPath(selected), "Final source symlink entry is preserved separately from its target");
                Throws(delegate { VolumeIdentity.Capture(link, true); }, "Destination symlink capture is rejected");
                Assert(VolumeIdentity.Verify(linked) == VolumeIdentity.BoundPath(linked), "Unchanged source symlink target verifies");
                Directory.Delete(link);
                if (!CreateSymbolicLink(link, other, 3)) throw new InvalidOperationException("Cannot recreate test symlink.");
                Throws(delegate { VolumeIdentity.Verify(linked); }, "Retargeted selected source/exclusion symlink rejects resume");
            }
        }

        private static void RunRobocopy(VolumePath source, VolumePath destination, RobocopyBindings bindings)
        {
            RunRobocopyArguments(Quote(bindings.ToRobocopy(VolumeIdentity.BoundPath(source))) + " " + Quote(bindings.ToRobocopy(VolumeIdentity.BoundPath(destination))) + " .settings.json /Z /R:0 /W:0 /NFL /NDL /NP /NJS /NJH");
        }

        private static void RunRobocopyArguments(string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"), arguments) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(30000)) { process.Kill(); process.WaitForExit(); throw new TimeoutException("Synthetic GUID copy timed out."); }
                Assert(process.ExitCode < 8, "Robocopy accepts private GUID-bound source and destination with restartable mode");
            }
        }

        private static string Quote(string value) { return "\"" + value.TrimEnd('\\') + new string('\\', (value.Length - value.TrimEnd('\\').Length) * 2) + "\""; }
        private static void SetJunction(string path, string target)
        {
            typeof(RobocopyBindings).GetMethod("SetJunction", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { path, target });
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); checks++; }
        private static void Throws(Action action, string message) { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } Assert(failed, message); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, uint flags);
    }
}
