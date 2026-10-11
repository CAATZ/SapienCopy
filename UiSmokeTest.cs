using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SapienCopy;

static class UiSmokeTest
{
    [STAThread]
    static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        if (CopyWorker.TryRunWorker(args, RunEngine)) return Environment.ExitCode;
        string previousOneDrive = Environment.GetEnvironmentVariable("OneDrive");
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm())
            {
                if (form.Text != "SapienCopy 1.10.0" || form.Name != "SapienCopy" || form.Icon == null) throw new Exception("Application branding or icon missing.");
                form.CreateControl();
                CreateHandles(form);
                form.PerformLayout();
                string[] names = { "SourceFolders", "ExcludedFolders", "AddFolder", "AddFiles", "RemoveFolder", "AddExcludedFolder", "RemoveExcludedFolder", "Destination", "CopyMode", "OneDriveMode", "OneDriveHint", "CopySpeed", "EstimateProgress", "SaveLogs", "ClearFolderLists", "CurrentFolder", "PercentageProgress", "TimeRemaining", "Threads", "StartCopy", "PauseResume", "CancelCopy", "OpenResult", "Activity" };
                foreach (Size size in new Size[] { form.Size, form.MinimumSize })
                {
                    form.Size = size;
                    form.PerformLayout();
                    foreach (string name in names)
                    {
                        var control = form.Controls.Find(name, true)[0];
                        if (control.Width < 20 || control.Height < 10) throw new Exception("Invalid layout: " + name);
                        var bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                        if (!form.ClientRectangle.Contains(bounds)) throw new Exception("Clipped control: " + name);
                    }
                }
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(args[0], ImageFormat.Png);
                }
                var source = (ListView)form.Controls.Find("SourceFolders", true)[0];
                var excluded = (ListView)form.Controls.Find("ExcludedFolders", true)[0];
                var target = (TextBox)form.Controls.Find("Destination", true)[0];
                var start = (Button)form.Controls.Find("StartCopy", true)[0];
                var mode = (ComboBox)form.Controls.Find("CopyMode", true)[0];
                var oneDrive = (ComboBox)form.Controls.Find("OneDriveMode", true)[0];
                var estimate = (CheckBox)form.Controls.Find("EstimateProgress", true)[0];
                var saveLogs = (CheckBox)form.Controls.Find("SaveLogs", true)[0];
                var clearLists = (CheckBox)form.Controls.Find("ClearFolderLists", true)[0];
                var bar = (ProgressBar)form.Controls.Find("CopyProgress", true)[0];
                var percentage = form.Controls.Find("PercentageProgress", true)[0];
                var remaining = form.Controls.Find("TimeRemaining", true)[0];
                var formatElapsed = typeof(MainForm).GetMethod("FormatElapsed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                foreach (var sample in new[] { new { Hours = 0, Text = "00:02:03" }, new { Hours = 25, Text = "25:02:03" }, new { Hours = 100, Text = "100:02:03" } })
                    if ((string)formatElapsed.Invoke(null, new object[] { TimeSpan.FromHours(sample.Hours) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3) }) != sample.Text) throw new Exception("Elapsed time wrapped after 24 hours.");
                if (!estimate.Checked) throw new Exception("Percentage/ETA is not checked by default.");
                if (!saveLogs.Checked || mode.Items.Count != 3) throw new Exception("Copy modes or dated-copy logging default missing.");
                if (clearLists.Checked) throw new Exception("Clearing the folder lists should be opt-in.");
                if (oneDrive.Items.Count != 3 || oneDrive.SelectedIndex != 0) throw new Exception("OneDrive modes/default missing.");
                oneDrive.SelectedIndex = 1;
                if (!form.Controls.Find("OneDriveHint", true)[0].Text.Contains("locally")) throw new Exception("Downloaded-only hint missing.");
                var fixture = Path.Combine(Path.GetDirectoryName(args[0]), "ui-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string from = Path.Combine(fixture, "Source"), to = Path.Combine(fixture, "Destination");
                Directory.CreateDirectory(from);
                Directory.CreateDirectory(to);
                File.WriteAllText(Path.Combine(from, "example.txt"), "GUI test");
                Environment.SetEnvironmentVariable("OneDrive", from);
                source.Items.Add(from);
                var dropDestination = typeof(MainForm).GetMethod("SetDestinationFromDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Drop(target, new[] { to });
                if (target.Text != to) throw new Exception("Destination folder drop did not set the path.");
                Drop(target, new[] { Path.Combine(from, "example.txt") });
                Drop(target, new[] { to, from });
                if (target.Text != to) throw new Exception("Destination drop accepted a file or multiple folders.");
                // Create a WinForms synchronization context without displaying a window.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                if (start.Enabled || oneDrive.Enabled || estimate.Enabled || saveLogs.Enabled || clearLists.Enabled || form.Controls.Find("AddFiles", true)[0].Enabled) throw new Exception("Copy settings remained enabled while copying.");
                Drop(target, new[] { from });
                dropDestination.Invoke(form, new object[] { new[] { from } });
                if (target.Text != to) throw new Exception("Destination changed during a transfer.");
                var showMessage = typeof(MainForm).GetMethod("ShowActivityMessage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var folderStatus = (Label)form.Controls.Find("CurrentFolder", true)[0];
                string cachedFolder = Path.Combine(from, "Cached entries");
                showMessage.Invoke(form, new object[] { "Scan: Checking cached entries | 42 | " + cachedFolder });
                var tooltips = (ToolTip)typeof(MainForm).GetField("tips", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
                if (folderStatus.Text != "Path: " + cachedFolder || tooltips.GetToolTip(folderStatus) != cachedFolder || !form.Controls.Find("CopyStatus", true)[0].Text.Contains("42 entries")) throw new Exception("Current scan folder, count or full-path tooltip missing.");
                if (form.Controls.Find("Activity", true)[0].Text.Contains("Scan:")) throw new Exception("Per-folder scan status flooded the activity log.");
                showMessage.Invoke(form, new object[] { "Progress estimate unavailable: Synthetic sizing failure. Copying will continue." });
                typeof(MainForm).GetMethod("UpdateProgressDisplay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { new CopyProgress() });
                if (!percentage.Text.Contains("hover") || !tooltips.GetToolTip(percentage).Contains("Synthetic sizing failure")) throw new Exception("Sizing failure reason did not remain available beside the progress indicator.");
                typeof(MainForm).GetMethod("UpdateProgressDisplay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] {
                    new CopyProgress { Percent = 42.3, TotalBytes = 200000000, EstimatedBytesCopied = 84600000, Remaining = TimeSpan.FromMinutes(12) }
                });
                if (bar.Style != ProgressBarStyle.Blocks || bar.Value != 423 || !percentage.Text.Contains("84.6") && !percentage.Text.Contains("84,6") || remaining.Text != "Remaining: about 12 min") throw new Exception("Intermediate percentage and ETA did not display correctly.");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), Path.GetFileNameWithoutExtension(args[0]) + "-progress.png"), ImageFormat.Png);
                }
                WaitForCopy(start);
                var status = form.Controls.Find("CopyStatus", true)[0].Text;
                if (status != "Copy complete.") throw new Exception("GUI copy status: " + status);
                if (!form.Controls.Find("OpenResult", true)[0].Enabled) throw new Exception("Open result stayed disabled.");
                if (!oneDrive.Enabled || form.Controls.Find("CopySpeed", true)[0].Text != "Speed: — MB/s") throw new Exception("Copy controls did not reset.");
                if (!estimate.Enabled || bar.Value != bar.Maximum || percentage.Text != "100% complete" || remaining.Text != "Remaining: 0s") throw new Exception("Successful copy did not retain completed percentage/ETA.");
                oneDrive.SelectedIndex = 2;
                if (!form.Controls.Find("OneDriveHint", true)[0].Text.Contains("Skips")) throw new Exception("Exclude hint missing.");
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                WaitForCopy(start);
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Complete with selected exclusions.") throw new Exception("GUI did not pass the exclusion policy to the engine.");
                if (!form.Controls.Find("Activity", true)[0].Text.Contains("Intentionally excluded paths: 1")) throw new Exception("GUI did not report intentional exclusions.");
                if (bar.Value != bar.Maximum) throw new Exception("All-excluded copy did not finish progress.");
                estimate.Checked = false;
                if (bar.Value != 0 || !percentage.Text.Contains("off")) throw new Exception("Unchecked estimate did not reset display.");
                oneDrive.SelectedIndex = 0;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                if (bar.Style != ProgressBarStyle.Marquee) throw new Exception("Unchecked copy did not use activity indicator.");
                showMessage.Invoke(form, new object[] { "Scan: Checking destination | 0 | " + to });
                if (folderStatus.Text != "Path: " + to) throw new Exception("Current folder depends on percentage/ETA being enabled.");
                WaitForCopy(start);
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Copy complete." || bar.Value != 0 || !percentage.Text.Contains("off")) throw new Exception("Unchecked copy did not finish with estimates off.");
                estimate.Checked = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                typeof(MainForm).GetMethod("CancelCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
                showMessage.Invoke(form, new object[] { "Scan: Checking cached entries | 99 | " + cachedFolder });
                if (!form.Controls.Find("CopyStatus", true)[0].Text.StartsWith("Cancelling...")) throw new Exception("Queued scan message overwrote cancellation status.");
                WaitForCopy(start);
                if (!form.Controls.Find("CopyStatus", true)[0].Text.StartsWith("Cancelled.") || bar.Value == bar.Maximum || remaining.Text != "Remaining: —") throw new Exception("Cancellation state: " + form.Controls.Find("CopyStatus", true)[0].Text + "; progress=" + bar.Value + "; " + remaining.Text + "; " + form.Controls.Find("Activity", true)[0].Text);
                string omit = Path.Combine(from, "Skip this folder");
                Directory.CreateDirectory(omit);
                File.WriteAllText(Path.Combine(omit, "private fixture.txt"), "This synthetic file must stay out of the copy.");
                var dropData = new DataObject(DataFormats.FileDrop, new string[] { omit, omit });
                var drag = new DragEventArgs(dropData, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);
                typeof(Control).GetMethod("OnDragDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(excluded, new object[] { drag });
                if (excluded.Items.Count != 1 || excluded.Items[0].ToolTipText != omit) throw new Exception("Excluded-folder drop, duplicate handling or full-path tooltip failed.");
                excluded.Items[0].Selected = true;
                typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(excluded, new object[] { new KeyEventArgs(Keys.Delete) });
                if (excluded.Items.Count != 0 || !File.Exists(Path.Combine(omit, "private fixture.txt"))) throw new Exception("Removing an excluded-list entry changed the source or failed.");
                typeof(Control).GetMethod("OnDragDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(excluded, new object[] { drag });
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                if (excluded.Enabled || form.Controls.Find("AddExcludedFolder", true)[0].Enabled || form.Controls.Find("RemoveExcludedFolder", true)[0].Enabled) throw new Exception("Exclusion controls remained enabled while copying.");
                WaitForCopy(start);
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Complete with selected exclusions." || !excluded.Enabled) throw new Exception("Manual exclusions did not pass through the GUI.");
                string runFolder = (string)typeof(MainForm).GetField("resultFolder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(form);
                string copiedFolder = CopyEngine.DestinationFor(runFolder, from);
                if (!File.Exists(Path.Combine(copiedFolder, "example.txt")) || Directory.Exists(Path.Combine(copiedFolder, "Skip this folder"))) throw new Exception("GUI copy did not honor the selected excluded folder.");
                if (!File.ReadAllText(Path.Combine(runFolder, "Exclusions.csv")).Contains("Folder excluded by user")) throw new Exception("Manual exclusions were not reported.");
                mode.SelectedIndex = 1;
                if (!form.Controls.Find("ModeHint", true)[0].Text.Contains("replaced") || !saveLogs.Checked) throw new Exception("Update mode hint or logging default did not change.");
                mode.SelectedIndex = 2;
                if (saveLogs.Checked || !form.Controls.Find("ModeHint", true)[0].Text.Contains("directly")) throw new Exception("Direct copy did not default to no saved logs.");
                oneDrive.SelectedIndex = 1;
                if (form.Controls.Find("OneDriveHint", true)[0].Text.Contains("Exclusions.csv")) throw new Exception("Logless OneDrive hint points to an unsaved report.");
                oneDrive.SelectedIndex = 0;
                string directTo = Path.Combine(fixture, "Direct destination");
                string anotherSource = Path.Combine(fixture, "Another source");
                Directory.CreateDirectory(directTo);
                Directory.CreateDirectory(anotherSource);
                File.WriteAllText(Path.Combine(anotherSource, "second.txt"), "Second selected folder");
                source.Items.Add(anotherSource);
                target.Text = directTo;
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), Path.GetFileNameWithoutExtension(args[0]) + "-direct.png"), ImageFormat.Png);
                }
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                if (saveLogs.Enabled || mode.Enabled) throw new Exception("Direct copy mode/log controls remained enabled during a copy.");
                WaitForCopy(start);
                string directFolder = Path.Combine(directTo, "Source");
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Complete with selected exclusions." || bar.Value != bar.Maximum) throw new Exception("Logless direct copy did not finish with estimated progress.");
                if (!File.Exists(Path.Combine(directFolder, "example.txt")) || !File.Exists(Path.Combine(directTo, "Another source", "second.txt")) || Directory.Exists(Path.Combine(directFolder, "Skip this folder"))) throw new Exception("Direct copy layout or exclusion filtering failed.");
                if (Directory.GetFileSystemEntries(directTo).Length != 2) throw new Exception("Logless direct copy created extra destination entries.");
                string loglessActivity = form.Controls.Find("Activity", true)[0].Text;
                if (!loglessActivity.Contains("were not saved") || loglessActivity.Contains("Reports:") || loglessActivity.Contains("Exclusions.csv")) throw new Exception("Logless results contain a misleading saved-report reference.");
                if (!saveLogs.Enabled || saveLogs.Checked || !form.Controls.Find("OpenResult", true)[0].Enabled) throw new Exception("Direct copy controls did not reset correctly.");
                File.WriteAllText(Path.Combine(directFolder, "keep-existing.txt"), "Destination-only file");
                saveLogs.Checked = true;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                WaitForCopy(start);
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Complete with selected exclusions." || !File.Exists(Path.Combine(directFolder, "keep-existing.txt"))) throw new Exception("Direct merge did not preserve destination-only files.");
                var reportFolders = Directory.GetDirectories(directTo, "Reports-*");
                if (reportFolders.Length != 1 || !File.Exists(Path.Combine(reportFolders[0], "Summary.csv")) || !form.Controls.Find("Activity", true)[0].Text.Contains("Reports:")) throw new Exception("Direct copy did not honor the enabled logs checkbox.");
                mode.SelectedIndex = 0;
                if (!saveLogs.Checked) throw new Exception("Dated-copy logging default did not restore.");
                mode.SelectedIndex = 2;
                if (saveLogs.Checked) throw new Exception("Reselecting Direct copy did not reset the logging default.");
                if (source.Items.Count != 2 || excluded.Items.Count != 1) throw new Exception("Unchecked auto-clear changed folder selections.");
                clearLists.Checked = true;
                var clearCompleted = typeof(MainForm).GetMethod("ClearFolderListsIfSuccessful", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                foreach (var incomplete in new CopyResult[] {
                    new CopyResult { Status = "Failed" },
                    new CopyResult { Status = "CompletedWithWarnings" },
                    new CopyResult { Status = "Cancelled", Cancelled = true },
                    new CopyResult { Status = "Completed", CleanupWarning = "Synthetic cleanup warning" }
                })
                {
                    clearCompleted.Invoke(form, new object[] { incomplete });
                    if (source.Items.Count != 2 || excluded.Items.Count != 1) throw new Exception("Auto-clear removed selections needed to retry a non-clean result.");
                }
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                typeof(MainForm).GetMethod("CancelCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
                WaitForCopy(start);
                if (source.Items.Count != 2 || excluded.Items.Count != 1) throw new Exception("Cancelled copy cleared folder lists.");
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                if (clearLists.Enabled) throw new Exception("Auto-clear checkbox stayed editable during a transfer.");
                WaitForCopy(start);
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Complete with selected exclusions." || source.Items.Count != 0 || excluded.Items.Count != 0 || !clearLists.Enabled) throw new Exception("Successful copy did not clear both folder lists.");
                if (!File.Exists(Path.Combine(from, "example.txt")) || !File.Exists(Path.Combine(omit, "private fixture.txt")) || target.Text != directTo) throw new Exception("Auto-clear changed source files or destination settings.");
                clearLists.Checked = false;
                string fileSourceFolder = Path.Combine(fixture, "Individual files \u00f1");
                Directory.CreateDirectory(fileSourceFolder);
                string selectedFile = Path.Combine(fileSourceFolder, ".settings.json");
                File.WriteAllText(selectedFile, "Synthetic configuration only");
                File.WriteAllText(Path.Combine(fileSourceFolder, "unselected.txt"), "Must not be copied");
                Drop(source, new[] { selectedFile, from, selectedFile });
                if (source.Items.Count != 2 || source.Items[0].ToolTipText != selectedFile) throw new Exception("Mixed file/folder source drop or deduplication failed.");
                Drop(excluded, new[] { selectedFile });
                if (excluded.Items.Count != 0) throw new Exception("Excluded folders accepted an individual file.");
                string mixedDestination = Path.Combine(fixture, "Mixed destination");
                Directory.CreateDirectory(mixedDestination);
                Drop(target, new[] { mixedDestination });
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                WaitForCopy(start);
                if (form.Controls.Find("CopyStatus", true)[0].Text != "Copy complete." || !File.Exists(Path.Combine(mixedDestination, ".settings.json")) || !File.Exists(Path.Combine(mixedDestination, "Source", "example.txt")) || File.Exists(Path.Combine(mixedDestination, "unselected.txt"))) throw new Exception("Mixed file/folder GUI copy did not honor exact selection.");
                if (bar.Value != bar.Maximum || tooltips.GetToolTip(percentage).Length != 0) throw new Exception("New run retained an earlier sizing failure.");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), Path.GetFileNameWithoutExtension(args[0]) + "-files.png"), ImageFormat.Png);
                }
                source.Items.Clear();
                string pauseSource = Path.Combine(fixture, "Pause UI source"), pauseDestination = Path.Combine(fixture, "Pause UI destination");
                Directory.CreateDirectory(pauseSource);
                Directory.CreateDirectory(pauseDestination);
                File.WriteAllText(Path.Combine(pauseSource, "resume.txt"), "Resume UI fixture");
                source.Items.Add(pauseSource);
                target.Text = pauseDestination;
                mode.SelectedIndex = 0;
                saveLogs.Checked = false;
                clearLists.Checked = true;
                var pauseButton = (Button)form.Controls.Find("PauseResume", true)[0];
                var cancelButton = (Button)form.Controls.Find("CancelCopy", true)[0];
                var copyStatus = form.Controls.Find("CopyStatus", true)[0];
                var pauseMethod = typeof(MainForm).GetMethod("PauseResumeCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                WaitFor(delegate { return pauseButton.Enabled; }, "Pause did not become available after volume verification.");
                pauseMethod.Invoke(form, new object[] { pauseButton, EventArgs.Empty });
                WaitFor(delegate { return pauseButton.Enabled && pauseButton.Text == "Resume"; }, "Copy did not reach confirmed Paused state.");
                if (!copyStatus.Text.StartsWith("Paused.") || start.Enabled || target.Enabled || source.Enabled || !cancelButton.Enabled || source.Items.Count != 1 || remaining.Text != "Remaining: —") throw new Exception("Paused controls/progress/selection state incorrect: " + copyStatus.Text);
                Drop(target, new[] { from }); Drop(source, new[] { selectedFile }); Drop(excluded, new[] { omit });
                if (target.Text != pauseDestination || source.Items.Count != 1 || excluded.Items.Count != 0) throw new Exception("Paused selections accepted dropped changes.");
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(args[0]), Path.GetFileNameWithoutExtension(args[0]) + "-paused.png"), ImageFormat.Png);
                }
                var sessionField = typeof(MainForm).GetField("resumableSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var session = (CopySession)sessionField.GetValue(form);
                uint savedSerial = session.Destination.SerialNumber;
                session.Destination.SerialNumber ^= 1;
                pauseMethod.Invoke(form, new object[] { pauseButton, EventArgs.Empty });
                WaitFor(delegate { return pauseButton.Enabled && pauseButton.Text == "Resume"; }, "Wrong identity did not leave transfer paused.");
                if (!copyStatus.Text.StartsWith("Still paused.") || start.Enabled || Directory.GetFileSystemEntries(pauseDestination).Length != 0) throw new Exception("Wrong volume identity wrote output or discarded the paused transfer: " + copyStatus.Text);
                session.Destination.SerialNumber = savedSerial;
                pauseMethod.Invoke(form, new object[] { pauseButton, EventArgs.Empty });
                WaitForCopy(start);
                if (copyStatus.Text != "Copy complete." || source.Items.Count != 0 || Directory.GetDirectories(pauseDestination).Length != 1) throw new Exception("Verified Resume failed or created extra dated folders: " + copyStatus.Text);
                string resumedRoot = Directory.GetDirectories(pauseDestination)[0];
                if (File.ReadAllText(Path.Combine(resumedRoot, pauseSource.Substring(0, 1), pauseSource.Substring(3), "resume.txt")) != "Resume UI fixture") throw new Exception("Resume did not preserve the original source layout.");
                source.Items.Add(pauseSource);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                WaitFor(delegate { return pauseButton.Enabled; }, "Second pause session was not ready.");
                pauseMethod.Invoke(form, new object[] { pauseButton, EventArgs.Empty });
                WaitFor(delegate { return pauseButton.Enabled && pauseButton.Text == "Resume"; }, "Second copy did not pause.");
                typeof(MainForm).GetMethod("CancelCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null);
                if (!start.Enabled || pauseButton.Enabled || source.Items.Count != 1 || !copyStatus.Text.StartsWith("Cancelled.")) throw new Exception("Cancel while paused failed to unlock controls and keep selections.");
                source.Items.Clear();
                target.Text = Path.Combine(fixture, "Missing destination");
                source.Items.Add(Path.Combine(fixture, "Missing source"));
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                typeof(MainForm).GetMethod("StartCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, new object[] { start, EventArgs.Empty });
                if (start.Enabled || !form.Controls.Find("CancelCopy", true)[0].Enabled) throw new Exception("Validation was not moved into the cancellable worker.");
                WaitForCopy(start);
                if (!form.Controls.Find("CopyStatus", true)[0].Text.StartsWith("Copy stopped.") || source.Items.Count != 1) throw new Exception("Invalid settings did not report a recoverable worker error.");
                Console.WriteLine("PASS: layout/rendering; Pause/Resume and verified identity rejection; paused selection lock/cancellation; same dated output; mixed file/folder sources; drops; sizing errors; status; cancellation; validation; clear lists; direct merge; logs/exclusions; progress; OneDrive modes; completion.");
                return 0;
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Environment.SetEnvironmentVariable("OneDrive", previousOneDrive); }
    }

    static CopyResult RunEngine(CopyRequest request, Action<string> messages, CancellationToken token, Action<double?> speed, Action<CopyProgress> progress)
    {
        bool hold = request.Session == null && request.Sources != null && request.Sources.Length == 1 && Path.GetFileName(request.Sources[0]) == "Pause UI source";
        if (!hold) return CopyEngine.Run(request, messages, token, speed, progress);
        Action<CopyResult> publish = CopyEngine.StateChanged;
        CopyResult checkpoint = null;
        CopyEngine.StateChanged = delegate(CopyResult state) {
            checkpoint = state;
            if (publish != null) publish(state);
            if (state.Session != null)
            {
                if (!token.WaitHandle.WaitOne(20000)) throw new Exception("UI did not request Pause at the test checkpoint.");
                token.ThrowIfCancellationRequested();
            }
        };
        try { return CopyEngine.Run(request, messages, token, speed, progress); }
        catch (OperationCanceledException)
        {
            if (checkpoint == null) throw;
            checkpoint.Cancelled = true; checkpoint.Status = "Cancelled";
            return checkpoint;
        }
        finally { CopyEngine.StateChanged = publish; }
    }

    static void WaitFor(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
        if (!condition()) throw new Exception(failure);
        Application.DoEvents();
    }

    static void WaitForCopy(Button start)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!start.Enabled && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(30); }
        if (!start.Enabled) throw new Exception("GUI copy did not finish.");
        Application.DoEvents();
    }

    static void Drop(Control target, string[] paths)
    {
        var data = new DataObject(DataFormats.FileDrop, paths);
        var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);
        typeof(Control).GetMethod("OnDragDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(target, new object[] { args });
    }

    static void CreateHandles(Control parent)
    {
        IntPtr handle = parent.Handle;
        foreach (Control child in parent.Controls) CreateHandles(child);
    }
}
