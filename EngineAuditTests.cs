using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SapienCopy;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class EngineAuditTests
{
    private static int checks, skips;
    public static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        string root = Make(AppDomain.CurrentDomain.BaseDirectory, "audit-fixtures-" + Guid.NewGuid().ToString("N"));
        try
        {
            string actual = Make(root, "actual"), child = Make(actual, "child"), link = Make(root, "link");
            File.WriteAllText(Path.Combine(actual, "payload.txt"), "Real target bytes must be included when explicitly selected.");
            File.WriteAllText(Path.Combine(child, "child.txt"), "Selected child data.");
            Junction(link, actual);
            foreach (bool reverse in new bool[] { false, true })
            {
                string output = Make(root, "both-" + reverse);
                CopyRequest request = Request(output, reverse ? new string[] { actual, link } : new string[] { link, actual });
                Assert(CopyEngine.Validate(request).Sources.Length == 2, "Selected link and target remain distinct in either order");
                CopyResult result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && result.CopiedCount == 2 && File.ReadAllText(Path.Combine(output, "actual\\payload.txt")) == File.ReadAllText(Path.Combine(actual, "payload.txt")), "Separate target data is physically copied in either order");
                Assert((File.GetAttributes(Path.Combine(output, "actual")) & FileAttributes.ReparsePoint) == 0 && (File.GetAttributes(Path.Combine(output, "link")) & FileAttributes.ReparsePoint) != 0, "Target copy is ordinary data while selected junction stays a link");
            }

            string aliasParent = Make(root, "parent alias"); Junction(aliasParent, root);
            Assert(CopyEngine.Validate(Request(Make(root, "dedup output"), actual, Path.Combine(aliasParent, "actual"))).Sources.Length == 1, "Aliases in parent components still deduplicate the same source entry");
            string selectedParent = Make(root, "selected parent"), nestedLink = Make(selectedParent, "nested link"); Junction(nestedLink, actual);
            foreach (bool reverse in new bool[] { false, true })
            {
                string output = Make(root, "parent-order-" + reverse);
                CopyRequest request = Request(output, reverse ? new string[] { selectedParent, actual, nestedLink } : new string[] { nestedLink, actual, selectedParent });
                Assert(CopyEngine.Validate(request).Sources.Length == 2, "Selected parent covers its nested link without dropping the separately selected target");
                CopyResult result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && File.Exists(Path.Combine(output, "actual\\payload.txt")) && (File.GetAttributes(Path.Combine(output, "selected parent\\nested link")) & FileAttributes.ReparsePoint) != 0, "Parent/link ordering preserves both real target data and the nested link");
            }
            CopyRequest linkedChild = Request(Make(root, "linked child output"), link, Path.Combine(link, "child"));
            Assert(CopyEngine.Validate(linkedChild).Sources.Length == 2, "A final source junction does not cover a separately selected child reached through it");
            CopyResult copiedChild = CopyEngine.Run(linkedChild, null, CancellationToken.None);
            Assert(copiedChild.Status == "Completed" && File.Exists(Path.Combine(linkedChild.Destination, "child\\child.txt")), "Explicit child selection reached through a junction copies real bytes");
            bool overlapRejected = false;
            try { CopyEngine.Validate(Request(actual, link)); } catch (InvalidOperationException) { overlapRejected = true; }
            Assert(overlapRejected, "Destination overlap checks still follow a selected link's target");

            string fileLink = Path.Combine(root, "payload-link.txt"), file = Path.Combine(actual, "payload.txt");
            if (CreateSymbolicLink(fileLink, file, 2))
            {
                CopyRequest request = Request(Make(root, "file links"), fileLink, file);
                Assert(CopyEngine.Validate(request).Sources.Length == 2, "Selected file symlink and target remain distinct");
                CopyResult result = CopyEngine.Run(request, null, CancellationToken.None);
                Assert(result.Status == "Completed" && File.ReadAllText(Path.Combine(request.Destination, "payload.txt")) == File.ReadAllText(file) && (File.GetAttributes(Path.Combine(request.Destination, "payload-link.txt")) & FileAttributes.ReparsePoint) != 0, "Selected file symlink and real target both survive copying");
            }
            else { skips++; Console.WriteLine("SKIP: File symbolic links require Windows symbolic-link permission; error " + Marshal.GetLastWin32Error() + "."); }
            HardLinkTargets(root);
            ReportTargets(root);
            LongHelperPaths(root);
            Console.WriteLine("PASS: " + checks + " engine audit regression assertions; " + skips + " unavailable file-link checks. Fixture: " + root);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void HardLinkTargets(string root)
    {
        string source = Make(root, "hardlink source"), sourceNested = Make(source, "nested");
        string data = Path.Combine(sourceNested, "data.txt"), sibling = Path.Combine(source, "ordinary.txt");
        File.WriteAllText(data, "New source bytes must not change an unselected hard-link target.");
        File.WriteAllText(sibling, "Ordinary source.");
        foreach (bool individual in new bool[] { true, false })
        {
            string output = Make(root, "hardlink destination " + individual);
            string parent = individual ? output : Make(Make(output, Path.GetFileName(source)), "nested");
            string outside = Path.Combine(root, "unselected " + individual + ".txt"), target = Path.Combine(parent, "data.txt");
            File.WriteAllText(outside, "Keep unselected bytes."); HardLink(target, outside);
            CopyRequest request = individual ? Request(output, data, sibling) : Request(output, source);
            Assert(Rejected(delegate { CopyEngine.Run(request, null, CancellationToken.None); }), "Existing hard-linked " + (individual ? "individual" : "nested") + " destination file rejects before copying");
            Assert(File.ReadAllText(outside) == "Keep unselected bytes." && File.ReadAllText(target) == "Keep unselected bytes.", "Unselected hard-link target retains its bytes");
            Assert(!File.Exists(Path.Combine(individual ? output : Path.Combine(output, Path.GetFileName(source)), "ordinary.txt")), "Preflight rejection occurs before another selected file is copied");
        }

        string retainedOutput = Make(root, "retained hardlinks"), copiedRoot = Make(retainedOutput, Path.GetFileName(source));
        string excludedTarget = Make(copiedRoot, "nested"), sentinel = Path.Combine(root, "retained sentinel.txt");
        File.WriteAllText(sentinel, "Retained unrelated data.");
        HardLink(Path.Combine(excludedTarget, "data.txt"), sentinel);
        HardLink(Path.Combine(copiedRoot, "destination-only.txt"), sentinel);
        CopyRequest retained = Request(retainedOutput, source); retained.ExcludedFolders = new string[] { sourceNested };
        CopyResult retainedResult = CopyEngine.Run(retained, null, CancellationToken.None);
        Assert(retainedResult.Status == "Completed" && File.ReadAllText(sentinel) == "Retained unrelated data." && File.ReadAllText(Path.Combine(copiedRoot, "ordinary.txt")) == "Ordinary source.", "Excluded and destination-only hard links are retained while selected ordinary data copies");

        string linkedSource = Path.Combine(source, "source alias.txt"); HardLink(linkedSource, sibling);
        string fresh = Make(root, "source hardlinks output");
        CopyResult sourceResult = CopyEngine.Run(Request(fresh, sibling, linkedSource), null, CancellationToken.None);
        Assert(sourceResult.Status == "Completed" && File.ReadAllText(Path.Combine(fresh, "ordinary.txt")) == "Ordinary source." && File.ReadAllText(Path.Combine(fresh, "source alias.txt")) == "Ordinary source.", "Source hard-linked files remain supported");
        Assert(VolumeIdentity.HardLinkCount(Path.Combine(fresh, "ordinary.txt")) == 1 && VolumeIdentity.HardLinkCount(Path.Combine(fresh, "source alias.txt")) == 1, "Copied source hard links produce independent destination files");
        File.WriteAllText(sibling, "Ordinary changed data.");
        Assert(CopyEngine.Run(Request(fresh, sibling), null, CancellationToken.None).Status == "Completed" && File.ReadAllText(Path.Combine(fresh, "ordinary.txt")) == "Ordinary changed data.", "Ordinary existing output files can still be updated");
    }

    private static void ReportTargets(string root)
    {
        string source = Make(root, "report source"), output = Make(root, "report output"), data = Path.Combine(source, "data.txt");
        File.WriteAllText(data, "Report test data.");
        CopyRequest request = Request(output, source); request.SaveLogs = true;
        CopyResult initial = CopyEngine.Run(request, null, CancellationToken.None);
        Assert(initial.Status == "Completed", "Report fixture copies successfully before resume");
        request.Session = initial.Session;
        foreach (string name in new string[] { "Summary.csv", "Exclusions.csv", "STATUS.txt", "Logs\\001.log", "Logs\\001-scan.log" })
        {
            string helper = Path.Combine(initial.ReportFolder, name), sentinel = Path.Combine(root, "report sentinel " + checks + ".txt");
            File.WriteAllText(sentinel, "Keep unrelated report target.");
            File.Delete(helper); HardLink(helper, sentinel);
            bool rejected = false;
            try { rejected = CopyEngine.Run(request, null, CancellationToken.None).Status == "Failed"; }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && File.ReadAllText(sentinel) == "Keep unrelated report target.", "Resume rejects hard-linked " + name + " without changing its other path");
            File.Delete(helper);
        }
        Assert(CopyEngine.Run(request, null, CancellationToken.None).Status == "Completed", "Resume succeeds after replacing unsafe helper entries with fresh files");

        string job = Path.Combine(root, "selection.rcj"), outside = Path.Combine(root, "job sentinel.txt");
        File.WriteAllText(outside, "Keep unrelated job target."); HardLink(job, outside);
        OneDrivePlan plan = new OneDrivePlan();
        for (int i = 0; i < 500; i++) plan.AddExcluded(new ExcludedPath { Path = Path.Combine(source, "excluded-" + i + new string('x', 80)), Directory = true });
        Assert(Rejected(delegate { plan.PrepareArguments(job); }) && File.ReadAllText(outside) == "Keep unrelated job target.", "Large exclusion job refuses a hard-linked destination before writing");
        File.Delete(job); plan.PrepareArguments(job);
        Assert(File.Exists(job) && plan.Arguments.IndexOf(" /JOB:", StringComparison.Ordinal) >= 0, "Large exclusion jobs still write ordinary files");
    }

    private static void LongHelperPaths(string root)
    {
        string source = Make(root, "long-helper source"), output = Make(root, "long-helper destination");
        output = VolumeIdentity.BoundPath(VolumeIdentity.Capture(output));
        while (output.Length < 280) output = Make(output, new string('d', 55));
        string data = Path.Combine(source, "payload.txt");
        File.WriteAllText(data, "Long report paths must not prevent copying or progress estimates.");
        CopyRequest request = Request(output, source); request.SaveLogs = true;
        CopyResult result = CopyEngine.Run(request, null, CancellationToken.None);
        string log = Path.Combine(result.ReportFolder, "Logs\\001.log");
        Assert(log.Length > 260 && result.Status == "Completed", "Copy completes with a report path beyond MAX_PATH");
        Assert(result.EstimatedTotalBytes == new FileInfo(data).Length && String.IsNullOrEmpty(result.EstimateError), "Long scan log paths retain percentage and ETA sizing");
        Assert(File.Exists(log) && File.ReadAllText(Path.Combine(output, Path.GetFileName(source), "payload.txt")) == File.ReadAllText(data), "Long-path logs and copied bytes stay at the selected destination");
        Assert(String.IsNullOrEmpty(result.CleanupWarning), "Long-path log junctions are removed after copying");

        string job = Path.Combine(result.ReportFolder, "Logs\\selection.rcj"), bindingFolder;
        using (RobocopyBindings bindings = new RobocopyBindings(new[] { VolumeIdentity.Capture(source), VolumeIdentity.Capture(output) }))
        {
            bindingFolder = bindings.DirectoryPath;
            string boundSource = VolumeIdentity.BoundPath(VolumeIdentity.Capture(source));
            OneDrivePlan plan = new OneDrivePlan();
            for (int i = 0; i < 500; i++) plan.AddExcluded(new ExcludedPath { Path = Path.Combine(boundSource, "excluded-" + i + new string('x', 80)), Directory = true });
            plan.PrepareArguments(job, bindings.ToRobocopy);
            FolderResult batch = new FolderResult { Source = boundSource, Destination = VolumeIdentity.BoundPath(VolumeIdentity.Capture(output)), JobPath = job,
                FilterArguments = plan.Arguments, ScanLogPath = Path.Combine(result.ReportFolder, "Logs\\job-scan.log") };
            CopyRequest scan = Request(output, source); scan.Bindings = bindings;
            Assert(plan.Arguments.IndexOf(" /JOB:", StringComparison.Ordinal) >= 0 && CopyEstimate.Scan(scan, batch, null, CancellationToken.None) == new FileInfo(data).Length,
                "Long exclusion-job paths open correctly and their sizing headers reverse-map correctly");
            string nested = Make(Path.GetDirectoryName(job), "nested"), nestedFile = Path.Combine(nested, "nested.log");
            string outerAlias = bindings.ToHelperPath(job), nestedAlias = bindings.ToHelperPath(nestedFile);
            Assert(bindings.ToVolumePath(outerAlias) == job && bindings.ToVolumePath(nestedAlias) == nestedFile && bindings.ToVolumePath(bindings.ToRobocopy(boundSource)) == boundSource,
                "Overlapping helper parents retain exact reverse mapping without changing source bindings");
        }
        Assert(!Directory.Exists(bindingFolder) && File.Exists(job) && File.Exists(log), "Helper cleanup removes private junctions while retaining destination logs and jobs");
    }

    private static bool Rejected(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    private static void HardLink(string path, string target) { if (!CreateHardLink(path, target, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }

    private static CopyRequest Request(string output, params string[] sources) { return new CopyRequest { Sources = sources, Destination = output, DirectCopy = true, SaveLogs = false }; }
    private static string Make(string parent, string name) { return Directory.CreateDirectory(Path.Combine(parent, name)).FullName; }
    private static void Junction(string link, string target) { typeof(RobocopyBindings).GetMethod("SetJunction", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { link, VolumeIdentity.BoundPath(VolumeIdentity.Capture(target)) + "\\" }); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool CreateSymbolicLink(string link, string target, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string path, string target, IntPtr securityAttributes);
}
