using System;
using System.IO;
using System.Text;
using System.Threading;
using SapienCopy;

internal static class CopyEstimateTests
{
    private static int assertions;

    private static void Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "estimate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, ".settings.json"), new byte[17]);
            File.WriteAllBytes(Path.Combine(source, "unselected.txt"), new byte[100]);
            FolderResult folder = new FolderResult {
                Source = source, Destination = Path.Combine(root, "destination"),
                IncludeArguments = " \".settings.json\"", FlatCopy = true,
                ScanLogPath = Path.Combine(root, "size.log"), FilterArguments = ""
            };
            Assert(CopyEstimate.Scan(new CopyRequest(), folder, null, CancellationToken.None) == 17,
                "Sizing includes only the selected hidden-style filename");
            Assert(!Directory.Exists(folder.Destination), "Sizing creates no source-data destination");

            folder.IncludeArguments = " \"-invalid-test-name.txt\"";
            folder.ScanLogPath = Path.Combine(root, "not-created.log");
            try
            {
                CopyEstimate.Scan(new CopyRequest { SaveLogs = false }, folder, null, CancellationToken.None);
                throw new Exception("Invalid Robocopy option was accepted");
            }
            catch (IOException error)
            {
                Assert(error.Message.Contains("16") && error.Message.Contains("-invalid-test-name.txt") && error.Message.Contains(source),
                    "Sizing startup errors preserve Robocopy diagnostics and the failed source without relying on a log");
            }
            Assert(!File.Exists(folder.ScanLogPath), "The regression reproduces failure before log creation");

            string selected = Path.Combine(source, ".settings.json");
            Assert(CopyEstimate.Parse(new StringReader("17\t" + selected + "\r\n"), selected, null, CancellationToken.None) == 17,
                "The sizing parser accepts an exact selected file as its source");
            try
            {
                CopyEstimate.Parse(new StringReader("100\t" + Path.Combine(source, "unselected.txt") + "\r\n"), selected, null, CancellationToken.None);
                throw new Exception("An unselected sibling was accepted");
            }
            catch (InvalidDataException) { Assert(true, "Single-file sizing rejects an unselected sibling"); }
            try
            {
                CopyEstimate.Parse(new StringReader("ERROR unexpected sizing output\r\n"), source, null, CancellationToken.None);
                throw new Exception("Unexpected output was accepted");
            }
            catch (InvalidDataException error)
            {
                Assert(error.Message.Contains("ERROR unexpected sizing output") && error.Message.Contains(source),
                    "Unexpected rows remain errors and include diagnostic context");
            }

            CopyProgress state = null;
            CopyEstimateProgress meter = new CopyEstimateProgress(true, delegate(CopyProgress value) { state = value; });
            meter.Ready(1000);
            meter.BeginFolder(100);
            meter.Sample(50, 100);
            meter.EndFolder(true);
            meter.BeginFolder(100);
            meter.Finish(false);
            Assert(state.Percent == 10 && state.Remaining.HasValue && state.Remaining.Value.TotalSeconds == 9,
                "A recent measured rate keeps ETA available across small cached folder batches");
            meter.Sample(0, 0);
            meter.Finish(false);
            Assert(!state.Remaining.HasValue, "A measured stall clears ETA instead of presenting a stale rate");
            meter.Sample(50, 100);
            Thread.Sleep(5050);
            meter.Finish(false);
            Assert(!state.Remaining.HasValue, "An old rate expires when no new transfer sample arrives");
            meter.Finish(true);
            Assert(state.Percent == 100 && state.Remaining == TimeSpan.Zero, "Successful completion retains the completed display");
            Console.WriteLine("PASS: " + assertions + " estimate assertions");
        }
        finally
        {
            string expected = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != expected || !Path.GetFileName(root).StartsWith("estimate-tests-", StringComparison.Ordinal))
                throw new IOException("Unexpected test cleanup path");
            Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        assertions++;
        Console.WriteLine("PASS: " + description);
    }
}
