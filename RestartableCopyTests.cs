using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SapienCopy;

static class RestartableCopyTests
{
    static int Size = 32 * 1024 * 1024, stopAfter = 12 * 1024 * 1024;
    static string throttle = " /IORATE:4m";
    static bool uncSource;
    static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        try
        {
            bool longProbe = args.Length > 0 && args[0] == "long";
            uncSource = args.Length > 0 && args[0] == "unc-source";
            if (args.Length > 0 && args[0] == "nooffload") throttle += " /NOOFFLOAD";
            if (longProbe) { Size = 64 * 1024 * 1024; stopAfter = 32 * 1024 * 1024; throttle = " /IORATE:1m"; }
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "restart-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Console.WriteLine("Evidence: " + root);
            string source = Path.Combine(root, "source.bin");
            byte[] data = new byte[Size]; new Random(7813).NextBytes(data); File.WriteAllBytes(source, data);
            Probe(root, source, false);
            if (!longProbe) Probe(root, source, true);
            Console.WriteLine("PASS: partial-file recovery preserves data with same/recreated bindings; completed files are skipped. Partial-offset reuse is reported separately.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Probe(string root, string source, bool recreate)
    {
        string destination = Path.Combine(root, recreate ? "New bindings" : "Same bindings");
        Directory.CreateDirectory(destination);
        VolumePath sourcePath = VolumeIdentity.Capture(source), destinationPath = VolumeIdentity.Capture(destination, true);
        RobocopyBindings bindings = new RobocopyBindings(new[] { sourcePath, destinationPath }, root);
        try
        {
            var request = new CopyRequest { DirectCopy = true, SaveLogs = false, DestinationFileSystem = "NTFS", Bindings = bindings };
            var folder = new FolderResult { Source = VolumeIdentity.BoundPath(sourcePath), Destination = VolumeIdentity.BoundPath(VolumeIdentity.Child(destinationPath, "source.bin", false)), FileCopy = true, IncludeArguments = " " + CopyEngine.Quote("source.bin") };
            // Diagnostic only: use the fixture drive's existing administrative share; app inputs remain local.
            if (uncSource) folder.Source = @"\\localhost\" + Path.GetPathRoot(source).Substring(0, 1) + "$" + source.Substring(2);
            string label = recreate ? "new" : "same", target = Path.Combine(destination, "source.bin");
            IoCounters partial = Copy(request, folder, true, Path.Combine(root, label + "-initial.txt"));
            byte[] original = File.ReadAllBytes(source), interrupted = File.ReadAllBytes(target);
            int prefix = 0;
            while (prefix < Math.Min(original.Length, interrupted.Length) && original[prefix] == interrupted[prefix]) prefix++;
            int interior = 1024 * 1024;
            while (interior < Math.Min(original.Length, interrupted.Length) && original[interior] == interrupted[interior]) interior++;
            if (interior < 2 * 1024 * 1024 || interior >= Size) throw new Exception("The destination did not contain a partial file body.");
            Console.WriteLine("Partial metadata: prefix=" + prefix + "; matching body ends=" + interior + "; length=" + interrupted.Length + "; attributes=" + File.GetAttributes(target));
            if (recreate) { bindings.Dispose(); bindings = new RobocopyBindings(new[] { sourcePath, destinationPath }, root); request.Bindings = bindings; }
            IoCounters resumed = Copy(request, folder, false, Path.Combine(root, label + "-resumed.txt"));
            using (var hash = SHA256.Create())
            using (var first = File.OpenRead(source))
            using (var second = File.OpenRead(target))
                if (Convert.ToBase64String(hash.ComputeHash(first)) != Convert.ToBase64String(hash.ComputeHash(second))) throw new Exception("Resumed file hash mismatch.");
            IoCounters unchanged = Copy(request, folder, false, Path.Combine(root, label + "-unchanged.txt"));
            if (unchanged.WriteBytes >= 1024 * 1024) throw new Exception("The completed unchanged file was copied again.");
            Console.WriteLine((recreate ? "New aliases" : "Same aliases") + ": copied body ends=" + interior + "; initial writes=" + partial.WriteBytes + "; resumed reads=" + resumed.ReadBytes + "; resumed writes=" + resumed.WriteBytes + "; full file=" + Size + "; unchanged writes=" + unchanged.WriteBytes);
            if (uncSource && resumed.WriteBytes >= (ulong)(Size * 0.9)) throw new Exception("The UNC checkpoint was not reused.");
            // Same-volume local copying on the tested Windows build did not reuse the checkpoint.
            if (resumed.WriteBytes >= (ulong)(Size * 0.9)) Console.WriteLine("Observed limitation: /Z recopied the interrupted file; completed-file skipping and final data integrity passed.");
        }
        finally { bindings.Dispose(); }
    }

    static IoCounters Copy(CopyRequest request, FolderResult folder, bool interrupt, string evidence)
    {
        using (var process = new Process())
        {
            // Test-only throttling makes a real mid-file interruption deterministic on fast local storage.
            process.StartInfo = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "robocopy.exe"), Arguments = CopyEngine.BuildArguments(request, folder) + throttle, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            if (!process.StartInfo.Arguments.Contains(" /Z ")) throw new Exception("Restartable mode was omitted.");
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            var watch = Stopwatch.StartNew(); IoCounters counters = new IoCounters();
            try
            {
                while (!process.WaitForExit(10))
                {
                    if (!GetProcessIoCounters(process.Handle, out counters)) throw new Exception("Cannot sample owned Robocopy process.");
                    if (interrupt && counters.WriteBytes >= (ulong)stopAfter) { process.Kill(); break; }
                    if (watch.Elapsed > TimeSpan.FromSeconds(100)) throw new Exception("Synthetic throttled copy exceeded timeout.");
                }
                if (!process.WaitForExit(5000)) throw new Exception("Owned Robocopy probe did not stop.");
                if (!GetProcessIoCounters(process.Handle, out counters)) throw new Exception("Cannot read final Robocopy counters.");
                File.WriteAllText(evidence, process.StartInfo.Arguments + Environment.NewLine + output.Result + errors.Result);
                Console.WriteLine((interrupt ? "Interrupted" : "Finished") + " after " + watch.Elapsed.TotalSeconds.ToString("F1") + "s; read=" + counters.ReadBytes + "; write=" + counters.WriteBytes);
                if (!interrupt && process.ExitCode >= 8) throw new Exception("Robocopy failed: " + output.Result + errors.Result);
                if (interrupt && (counters.WriteBytes < 8 * 1024 * 1024 || counters.WriteBytes >= (ulong)Size)) throw new Exception("Test did not interrupt a partially written file.");
                return counters;
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
