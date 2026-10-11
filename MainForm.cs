using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SapienCopy
{
    public sealed class MainForm : Form
    {
        readonly ListView sources = new ListView(), excluded = new ListView();
        readonly TextBox destination = new TextBox();
        readonly ComboBox mode = new ComboBox(), oneDrive = new ComboBox();
        readonly NumericUpDown threads = new NumericUpDown();
        readonly CheckBox protectedFiles = new CheckBox(), estimateProgress = new CheckBox(), saveLogs = new CheckBox(), clearLists = new CheckBox();
        readonly Button add = new Button(), addFiles = new Button(), remove = new Button(), browse = new Button();
        readonly Button addExcluded = new Button(), removeExcluded = new Button();
        readonly Button start = new Button(), pause = new Button(), cancel = new Button(), open = new Button();
        readonly TextBox activity = new TextBox();
        readonly Label status = new Label(), elapsed = new Label(), modeHint = new Label();
        readonly Label speed = new Label(), oneDriveHint = new Label();
        readonly Label percentage = new Label(), remaining = new Label(), currentFolder = new Label();
        readonly ProgressBar progress = new ProgressBar();
        readonly Stopwatch clock = new Stopwatch();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolTip tips = new ToolTip();
        CancellationTokenSource cancellation;
        bool running, paused, pauseRequested, stopUnconfirmed, closeWhenFinished;
        CopyRequest activeRequest;
        CopySession resumableSession, resultSession, displaySession;
        int runGeneration;
        string resultFolder, estimateFailure;
        readonly bool administrator;

        public MainForm()
        {
            Text = "SapienCopy 1.10.0";
            Name = "SapienCopy";
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(246, 248, 251);
            ForeColor = Color.FromArgb(28, 40, 55);
            ClientSize = new Size(960, 870);
            MinimumSize = new Size(850, 840);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 17, Padding = new Padding(24) };
            int[] heights = { 72, 27, -1, 46, 27, 43, 67, 28, 60, 41, 42, 24, 27, 24, 16, -2, 25 };
            foreach (int height in heights)
                root.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, height == -1 ? 64 : 36) : new RowStyle(SizeType.Absolute, height));
            Controls.Add(root);

            var heading = new Panel { Dock = DockStyle.Fill };
            var title = new Label { Text = "SapienCopy", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) };
            title.Font = new Font("Segoe UI", 23F, FontStyle.Bold);
            heading.Controls.Add(title);
            heading.Controls.Add(new Label { Text = "Add or drag files and folders below. Copies run with Windows Robocopy.", AutoSize = true, Location = new Point(2, 44), ForeColor = Color.FromArgb(84, 99, 117) });
            root.Controls.Add(heading, 0, 0);

            var folders = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0) };
            folders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            folders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            folders.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            folders.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            folders.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.Controls.Add(folders, 0, 1);
            root.SetRowSpan(folders, 3);
            folders.Controls.Add(Section("1  Source files and folders"), 0, 0);
            folders.Controls.Add(Section("2  Excluded folders (optional)"), 1, 0);
            SetupFolderList(sources, "SourceFolders", "Source files and folders");
            SetupFolderList(excluded, "ExcludedFolders", "Excluded folders");
            sources.Margin = new Padding(0, 3, 10, 3);
            excluded.Margin = new Padding(10, 3, 0, 3);
            tips.SetToolTip(excluded, "Skip these folders and everything inside them, including in size estimates. Existing files in a previous copy are kept. Hover over an entry to read its full path.");
            folders.Controls.Add(sources, 0, 1);
            folders.Controls.Add(excluded, 1, 1);

            var sourceButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), WrapContents = false };
            SetupButton(add, "Add folder...", "AddFolder", 112);
            SetupButton(addFiles, "Add files...", "AddFiles", 104);
            SetupButton(remove, "Remove", "RemoveFolder", 88);
            add.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { Description = "Select a source folder", ShowNewFolderButton = false })
                    if (picker.ShowDialog(this) == DialogResult.OK) AddFolder(sources, picker.SelectedPath);
            };
            addFiles.Click += delegate
            {
                using (var picker = new OpenFileDialog { Title = "Select source files", Filter = "All files (*.*)|*.*", Multiselect = true, CheckFileExists = true })
                    if (picker.ShowDialog(this) == DialogResult.OK)
                        foreach (string path in picker.FileNames) AddFolder(sources, path);
            };
            remove.Click += delegate { RemoveFolders(sources); };
            sourceButtons.Controls.Add(add);
            sourceButtons.Controls.Add(addFiles);
            sourceButtons.Controls.Add(remove);
            folders.Controls.Add(sourceButtons, 0, 2);
            var excludedButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 0, 0), WrapContents = false };
            SetupButton(addExcluded, "Add folder...", "AddExcludedFolder", 128);
            SetupButton(removeExcluded, "Remove", "RemoveExcludedFolder", 100);
            addExcluded.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { Description = "Select a folder to exclude, including all its contents", ShowNewFolderButton = false })
                    if (picker.ShowDialog(this) == DialogResult.OK) AddFolder(excluded, picker.SelectedPath);
            };
            removeExcluded.Click += delegate { RemoveFolders(excluded); };
            excludedButtons.Controls.Add(addExcluded);
            excludedButtons.Controls.Add(removeExcluded);
            folders.Controls.Add(excludedButtons, 1, 2);
            root.Controls.Add(Section("3  Destination folder"), 0, 4);

            var destinationRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            destinationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            destinationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114));
            destination.Name = "Destination";
            destination.AccessibleName = "Destination folder";
            destination.Dock = DockStyle.Fill;
            destination.Margin = new Padding(0, 3, 10, 0);
            destination.AllowDrop = true;
            tips.SetToolTip(destination, "Choose a destination, type its path, or drop one folder here.");
            destination.DragEnter += delegate(object sender, DragEventArgs args) {
                string[] paths = args.Data.GetData(DataFormats.FileDrop) as string[];
                args.Effect = !running && paths != null && paths.Length == 1 && Directory.Exists(paths[0]) ? DragDropEffects.Copy : DragDropEffects.None;
            };
            destination.DragDrop += delegate(object sender, DragEventArgs args) { SetDestinationFromDrop(args.Data.GetData(DataFormats.FileDrop) as string[]); };
            if (administrator) new FolderDrop(destination, SetDestinationFromDrop, delegate(string error) { activity.AppendText(Environment.NewLine + error + Environment.NewLine); });
            SetupButton(browse, "Browse...", "BrowseDestination", 108);
            browse.Click += delegate
            {
                using (var picker = new FolderBrowserDialog { Description = "Choose a folder on the destination drive", ShowNewFolderButton = true })
                {
                    if (Directory.Exists(destination.Text)) picker.SelectedPath = destination.Text;
                    if (picker.ShowDialog(this) == DialogResult.OK) destination.Text = picker.SelectedPath;
                }
            };
            destinationRow.Controls.Add(destination, 0, 0);
            destinationRow.Controls.Add(browse, 1, 0);
            root.Controls.Add(destinationRow, 0, 5);

            var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            options.Controls.Add(Section("Copy mode"), 0, 0);
            options.Controls.Add(Section("Threads"), 1, 0);
            options.Controls.Add(Section("OneDrive files"), 2, 0);
            mode.Name = "CopyMode";
            mode.AccessibleName = "Copy mode";
            mode.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Items.AddRange(new object[] { "New dated copy", "Update existing copy", "Direct copy" });
            mode.SelectedIndex = 0;
            mode.Dock = DockStyle.Fill;
            mode.Margin = new Padding(0, 0, 18, 0);
            mode.SelectedIndexChanged += delegate { saveLogs.Checked = mode.SelectedIndex != 2; UpdateModeHint(); };
            threads.Name = "Threads";
            threads.AccessibleName = "Copy threads";
            threads.Minimum = 1;
            threads.Maximum = 128;
            threads.Value = 32;
            threads.Width = 88;
            threads.Margin = new Padding(0);
            tips.SetToolTip(threads, "32 for SSDs; try 8 for a hard drive. More threads are not always faster.");
            oneDrive.Name = "OneDriveMode";
            oneDrive.AccessibleName = "OneDrive files";
            oneDrive.DropDownStyle = ComboBoxStyle.DropDownList;
            oneDrive.Items.AddRange(new object[] { "All files (download online-only files)", "Downloaded files only", "Exclude OneDrive completely" });
            oneDrive.Dock = DockStyle.Fill;
            oneDrive.Margin = new Padding(0);
            oneDrive.SelectedIndexChanged += delegate { UpdateOneDriveHint(); };
            oneDrive.SelectedIndex = 0;
            options.Controls.Add(mode, 0, 1);
            options.Controls.Add(threads, 1, 1);
            options.Controls.Add(oneDrive, 2, 1);
            root.Controls.Add(options, 0, 6);
            oneDriveHint.Name = "OneDriveHint";
            oneDriveHint.Dock = DockStyle.Fill;
            oneDriveHint.Font = new Font("Segoe UI", 9F);
            oneDriveHint.ForeColor = Color.FromArgb(84, 99, 117);
            root.Controls.Add(oneDriveHint, 0, 7);

            protectedFiles.Name = "ProtectedFiles";
            protectedFiles.Text = "Include protected files (administrator)";
            protectedFiles.AutoSize = true;
            protectedFiles.Enabled = administrator;
            protectedFiles.Margin = new Padding(0, 3, 0, 0);
            tips.SetToolTip(protectedFiles, administrator ? "Uses Robocopy backup mode to read protected folders." : "To include protected folders, close this app and use Run as administrator.");
            var checks = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            checks.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            checks.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            estimateProgress.Name = "EstimateProgress";
            estimateProgress.Text = "Calculate percentage and ETA (scan first)";
            estimateProgress.AutoSize = true;
            estimateProgress.Checked = true;
            estimateProgress.Margin = new Padding(0, 3, 0, 0);
            tips.SetToolTip(estimateProgress, "Scan first to estimate the total data to copy. Uncheck to skip this sizing scan; speed and elapsed time remain available. Filesystem compatibility and OneDrive availability checks still apply.");
            estimateProgress.CheckedChanged += delegate { if (!running) ResetProgressDisplay(); };
            checks.Controls.Add(protectedFiles, 0, 0);
            checks.Controls.Add(estimateProgress, 1, 0);
            saveLogs.Name = "SaveLogs";
            saveLogs.Text = "Save logs and reports";
            saveLogs.AutoSize = true;
            saveLogs.Checked = true;
            saveLogs.Margin = new Padding(0, 3, 0, 0);
            tips.SetToolTip(saveLogs, "Save Robocopy logs, CSV reports and status files with the copy. Uncheck to keep the destination free of these extra files. Speed, percentage/ETA and on-screen results still work.");
            saveLogs.CheckedChanged += delegate { UpdateOneDriveHint(); };
            checks.Controls.Add(saveLogs, 0, 1);
            clearLists.Name = "ClearFolderLists";
            clearLists.Text = "Clear lists after successful copy";
            clearLists.AutoSize = true;
            clearLists.Margin = new Padding(0, 3, 0, 0);
            tips.SetToolTip(clearLists, "Clear the Source files and folders and Excluded folders lists after a successful copy. This never deletes files. Lists are kept after warnings, failures or cancellation so you can retry.");
            checks.Controls.Add(clearLists, 1, 1);
            root.Controls.Add(checks, 0, 8);
            modeHint.Name = "ModeHint";
            modeHint.Dock = DockStyle.Fill;
            modeHint.ForeColor = Color.FromArgb(84, 99, 117);
            modeHint.Font = new Font("Segoe UI", 9F);
            UpdateModeHint();
            root.Controls.Add(modeHint, 0, 9);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            SetupButton(start, "Start copy", "StartCopy", 144);
            start.BackColor = Color.FromArgb(34, 92, 190);
            start.ForeColor = Color.White;
            start.FlatStyle = FlatStyle.Flat;
            start.FlatAppearance.BorderSize = 0;
            SetupButton(pause, "Pause", "PauseResume", 100);
            pause.Enabled = false;
            tips.SetToolTip(pause, "Pause stops this copy and keeps its settings. It does not eject the drive. Use Windows Eject before unplugging. Resume verifies the original volumes.");
            SetupButton(cancel, "Cancel", "CancelCopy", 100);
            cancel.Enabled = false;
            SetupButton(open, "Open copy folder", "OpenResult", 155);
            open.Enabled = false;
            start.Click += StartCopy;
            pause.Click += PauseResumeCopy;
            cancel.Click += delegate { CancelCopy(); };
            open.Click += delegate { OpenResult(); };
            actions.Controls.Add(start);
            actions.Controls.Add(pause);
            actions.Controls.Add(cancel);
            actions.Controls.Add(open);
            root.Controls.Add(actions, 0, 10);

            var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            status.Name = "CopyStatus";
            status.Text = "Ready. Add files or folders and choose a destination.";
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            elapsed.Dock = DockStyle.Fill;
            elapsed.TextAlign = ContentAlignment.TopRight;
            elapsed.Text = "00:00:00";
            speed.Name = "CopySpeed";
            speed.AccessibleName = "Approximate current copy speed";
            speed.Dock = DockStyle.Fill;
            speed.TextAlign = ContentAlignment.TopRight;
            speed.Text = "Speed: — MB/s";
            tips.SetToolTip(speed, "Approximate recent Robocopy write rate, including buffering and log writes. It is not a measurement of the drive's physical write speed.");
            statusRow.Controls.Add(status, 0, 0);
            statusRow.Controls.Add(speed, 1, 0);
            statusRow.Controls.Add(elapsed, 2, 0);
            root.Controls.Add(statusRow, 0, 11);
            currentFolder.Name = "CurrentFolder";
            currentFolder.AccessibleName = "Current scan or copy path";
            currentFolder.Dock = DockStyle.Fill;
            currentFolder.AutoEllipsis = true;
            currentFolder.Text = "Path: —";
            currentFolder.Font = new Font("Segoe UI", 9F);
            root.Controls.Add(currentFolder, 0, 12);
            var estimateRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            estimateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
            estimateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            percentage.Name = "PercentageProgress";
            percentage.Dock = DockStyle.Fill;
            percentage.AutoEllipsis = true;
            remaining.Name = "TimeRemaining";
            remaining.Dock = DockStyle.Fill;
            remaining.TextAlign = ContentAlignment.TopRight;
            estimateRow.Controls.Add(percentage, 0, 0);
            estimateRow.Controls.Add(remaining, 1, 0);
            root.Controls.Add(estimateRow, 0, 13);
            progress.Name = "CopyProgress";
            progress.Maximum = 1000;
            progress.Dock = DockStyle.Fill;
            progress.Margin = new Padding(0, 0, 0, 5);
            root.Controls.Add(progress, 0, 14);
            activity.Name = "Activity";
            activity.AccessibleName = "Copy activity and results";
            activity.Dock = DockStyle.Fill;
            activity.Multiline = true;
            activity.ReadOnly = true;
            activity.ScrollBars = ScrollBars.Both;
            activity.WordWrap = false;
            activity.BackColor = Color.White;
            activity.BorderStyle = BorderStyle.FixedSingle;
            activity.Font = new Font("Consolas", 9F);
            activity.Text = "Folder results and log locations will appear here.";
            root.Controls.Add(activity, 0, 15);
            root.Controls.Add(new Label { Text = "Destinations: NTFS, exFAT or FAT32. Close apps that are changing the files you want to copy.", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(84, 99, 117), Font = new Font("Segoe UI", 9F), Padding = new Padding(0, 6, 0, 0) }, 0, 16);
            ResetProgressDisplay();

            AcceptButton = start;
            timer.Interval = 1000;
            timer.Tick += delegate { elapsed.Text = FormatElapsed(clock.Elapsed); };
            FormClosing += ClosingForm;
            FormClosed += delegate { timer.Dispose(); tips.Dispose(); };
        }

        static Label Section(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
        }

        static void SetupButton(Button button, string text, string name, int width)
        {
            button.Name = name;
            button.Text = text;
            button.Width = width;
            button.Height = 33;
            button.Margin = new Padding(0, 0, 8, 0);
            button.UseVisualStyleBackColor = true;
        }

        void UpdateModeHint()
        {
            modeHint.Text = mode.SelectedIndex == 2
                ? "Copies each selected file or folder directly into the destination.\r\nMerges existing folders: changed files are replaced; destination-only files are kept."
                : mode.SelectedIndex == 1
                ? "Choose an existing copy folder. Changed files are replaced; destination-only files are kept.\r\nOriginal drive paths are preserved, for example C\\Projects\\MyFolder."
                : "Creates a new dated folder at the destination and preserves original drive paths.\r\nLinks are copied as links; targets outside your selected folders are not included.";
        }

        void UpdateOneDriveHint()
        {
            oneDriveHint.Text = oneDrive.SelectedIndex == 1
                ? "Copies locally cached OneDrive files; " + (saveLogs.Checked ? "known skipped paths are listed in Exclusions.csv." : "known skipped paths are counted in the results.")
                : oneDrive.SelectedIndex == 2
                ? "Skips detected OneDrive folders, including Desktop/Documents stored inside OneDrive."
                : "Online-only OneDrive files may download first, requiring internet access and local free space.";
        }

        void ResetProgressDisplay()
        {
            estimateFailure = null;
            tips.SetToolTip(percentage, "");
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = 0;
            percentage.Text = estimateProgress.Checked ? "Percentage and ETA enabled — scans before copying." : "Percentage and ETA off — no sizing scan.";
            remaining.Visible = estimateProgress.Checked;
            remaining.Text = "Remaining: —";
        }

        void UpdateProgressDisplay(CopyProgress update)
        {
            if (!running || !estimateProgress.Checked || cancellation.IsCancellationRequested) return;
            if (update.IsScanning)
            {
                progress.Style = ProgressBarStyle.Marquee;
                percentage.Text = "Calculating total data to copy...";
                remaining.Text = "Remaining: calculating...";
                if (!cancellation.IsCancellationRequested) status.Text = "Calculating transfer size...";
                return;
            }
            if (!cancellation.IsCancellationRequested && !update.IsComplete) status.Text = "Copying...";
            if (!update.Percent.HasValue)
            {
                progress.Style = ProgressBarStyle.Marquee;
                percentage.Text = estimateFailure == null ? "Percentage unavailable — see activity below." : "Percentage unavailable — hover here for the reason.";
                remaining.Text = "Remaining: —";
                return;
            }
            // Keep display rounding from claiming completion before Robocopy finishes.
            double percent = update.IsComplete ? 100 : Math.Floor(Math.Min(99.9, Math.Max(0, update.Percent.Value)) * 10) / 10;
            progress.Style = ProgressBarStyle.Blocks;
            progress.Value = (int)(percent * 10);
            percentage.Text = update.IsComplete ? "100% complete" : update.TotalBytes == 0
                ? "No file data to transfer; checking folders..."
                : String.Format("Estimated: {0:N1}% · {1} / {2}", percent, FormatBytes(update.EstimatedBytesCopied), FormatBytes(update.TotalBytes));
            remaining.Text = "Remaining: " + (update.IsComplete ? "0s" : update.Remaining.HasValue ? FormatRemaining(update.Remaining.Value) : "calculating...");
        }

        static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (bytes >= 1000 && unit < units.Length - 1) { bytes /= 1000; unit++; }
            return bytes.ToString(unit == 0 ? "N0" : "N1") + " " + units[unit];
        }

        static string FormatElapsed(TimeSpan value)
        {
            return ((long)value.TotalHours).ToString("00") + value.ToString(@"\:mm\:ss");
        }

        static string FormatRemaining(TimeSpan value)
        {
            if (value.TotalSeconds < 60) return "about " + Math.Max(1, Math.Ceiling(value.TotalSeconds)) + "s";
            if (value.TotalMinutes < 60) return "about " + Math.Ceiling(value.TotalMinutes) + " min";
            if (value.TotalHours < 24) return String.Format("about {0}h {1}m", (int)value.TotalHours, value.Minutes);
            return String.Format("about {0}d {1}h", value.Days, value.Hours);
        }

        void SetupFolderList(ListView list, string name, string label)
        {
            list.Name = name;
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.HeaderStyle = ColumnHeaderStyle.None;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;
            list.ShowItemToolTips = true;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.Columns.Add(label, 400);
            list.AccessibleName = label;
            list.AllowDrop = true;
            list.Resize += delegate { list.Columns[0].Width = Math.Max(200, list.ClientSize.Width - 5); };
            Action<string[]> acceptDrop = delegate(string[] paths) {
                if (!running && !paused && list.Enabled && paths != null) foreach (string path in paths) AddFolder(list, path);
            };
            list.DragEnter += delegate(object sender, DragEventArgs args) { if (!running && !paused && list.Enabled && args.Data.GetDataPresent(DataFormats.FileDrop)) args.Effect = DragDropEffects.Copy; };
            list.DragDrop += delegate(object sender, DragEventArgs args) {
                acceptDrop(args.Data.GetData(DataFormats.FileDrop) as string[]);
            };
            if (administrator) new FolderDrop(list, acceptDrop, delegate(string error) { activity.AppendText(Environment.NewLine + error + Environment.NewLine); });
            list.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Delete && !running && !paused && list.Enabled) RemoveFolders(list); };
        }

        void AddFolder(ListView list, string path)
        {
            if (!Directory.Exists(path) && !(list == sources && File.Exists(path)))
            {
                activity.AppendText(Environment.NewLine + (list == sources ? "Choose an existing file or folder: " : "Exclusions require an existing folder: ") + path + Environment.NewLine);
                return;
            }
            path = Path.GetFullPath(path);
            foreach (ListViewItem item in list.Items)
                if (String.Equals(item.Text.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            list.Items.Add(new ListViewItem(path) { ToolTipText = path });
        }

        void SetDestinationFromDrop(string[] paths)
        {
            if (running || paused || !destination.Enabled || paths == null) return;
            if (paths.Length != 1 || !Directory.Exists(paths[0]))
            {
                activity.AppendText(Environment.NewLine + "Drop one existing folder into the destination field." + Environment.NewLine);
                return;
            }
            destination.Text = Path.GetFullPath(paths[0]);
        }

        void RemoveFolders(ListView list)
        {
            foreach (ListViewItem item in list.SelectedItems) list.Items.Remove(item);
        }

        async void StartCopy(object sender, EventArgs args)
        {
            if (running || paused || stopUnconfirmed) return;
            var paths = new string[sources.Items.Count];
            for (int i = 0; i < paths.Length; i++) paths[i] = sources.Items[i].Text;
            var exclusions = new string[excluded.Items.Count];
            for (int i = 0; i < exclusions.Length; i++) exclusions[i] = excluded.Items[i].Text;
            var request = new CopyRequest { Sources = paths, ExcludedFolders = exclusions, Destination = destination.Text.Trim(), Threads = (int)threads.Value, BackupMode = protectedFiles.Checked, UpdateExisting = mode.SelectedIndex == 1, DirectCopy = mode.SelectedIndex == 2, SaveLogs = saveLogs.Checked, OneDriveMode = (OneDriveMode)oneDrive.SelectedIndex, EstimateProgress = estimateProgress.Checked };
            activeRequest = request;
            resumableSession = resultSession = displaySession = null;
            resultFolder = null;
            activity.Clear();
            await RunCopy(request, false);
        }

        async void PauseResumeCopy(object sender, EventArgs args)
        {
            if (running)
            {
                if (cancellation == null || cancellation.IsCancellationRequested || resumableSession == null) return;
                pauseRequested = true;
                pause.Enabled = false;
                status.Text = "Pausing... stopping this copy's worker.";
                cancellation.Cancel();
                return;
            }
            if (!paused || activeRequest == null || resumableSession == null) return;
            activeRequest.Session = resumableSession;
            await RunCopy(activeRequest, true);
        }

        async Task RunCopy(CopyRequest request, bool resuming)
        {
            paused = false;
            pauseRequested = false;
            int generation = ++runGeneration;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            SetRunning(true);
            status.Text = resuming ? "Verifying drives before resuming..." : "Preparing copy...";
            status.ForeColor = ForeColor;
            if (resuming) clock.Start(); else clock.Restart();
            timer.Start();
            var messages = new Progress<string>(delegate(string message) { if (generation == runGeneration) ShowActivityMessage(message); });
            var speeds = new Progress<double?>(delegate(double? bytesPerSecond) {
                if (running && generation == runGeneration) speed.Text = bytesPerSecond.HasValue
                    ? String.Format("Speed: {0:N1} MB/s (approx.)", bytesPerSecond.Value / 1000000.0)
                    : "Speed: — MB/s";
            });
            var details = new Progress<CopyProgress>(delegate(CopyProgress update) { if (generation == runGeneration) UpdateProgressDisplay(update); });
            var states = new Progress<CopyResult>(delegate(CopyResult state) {
                if (!running || generation != runGeneration || state == null || state.Session == null) return;
                UseSessionDisplay(state.Session);
                resumableSession = state.Session;
                pause.Enabled = !token.IsCancellationRequested;
            });
            try
            {
                var result = await Task.Run(() => CopyWorker.Run(request, line => ((IProgress<string>)messages).Report(line), token, rate => ((IProgress<double?>)speeds).Report(rate), update => ((IProgress<CopyProgress>)details).Report(update), state => ((IProgress<CopyResult>)states).Report(state)));
                if (!result.WorkerStopped) throw new CopyWorkerException(new IOException("The copy worker did not confirm that all its processes exited."), false);
                resultFolder = result.RunFolder;
                resultSession = result.Session;
                if (result.Session != null) UseSessionDisplay(result.Session);
                if (pauseRequested && result.Cancelled && result.WorkerStopped && result.Session != null)
                {
                    resumableSession = result.Session;
                    paused = true;
                    status.Text = "Paused. Drive has not been ejected.";
                    status.ForeColor = Color.FromArgb(151, 80, 20);
                    percentage.Text = "Paused — progress is recalculated on resume.";
                    remaining.Text = "Remaining: —";
                    activity.AppendText("Paused. This copy's processes have stopped. Use Windows Eject before unplugging the drive.\r\nResume keeps the same copy folder and verifies the original volumes.\r\n");
                    if (!String.IsNullOrEmpty(result.CleanupWarning)) activity.AppendText("Attention: " + result.CleanupWarning + Environment.NewLine);
                    return;
                }
                resumableSession = null;
                status.Text = result.Cancelled ? "Cancelled. Partial files were kept." :
                    result.Status == "Completed" ? (result.ExcludedCount > 0 ? "Complete with selected exclusions." : "Copy complete.") :
                    result.Status == "CompletedWithWarnings" ? "Finished with warnings. Check activity below." : "Copy incomplete. Check activity below.";
                if (result.Status == "Completed" && !String.IsNullOrEmpty(result.CleanupWarning)) status.Text = "Copy complete. Temporary cleanup needs attention.";
                status.ForeColor = result.Status == "Completed" && String.IsNullOrEmpty(result.CleanupWarning) ? Color.FromArgb(22, 112, 65) : Color.FromArgb(151, 80, 20);
                activity.AppendText(String.Format("\r\nCompleted: {0}   Warnings: {1}   Failed: {2}\r\nCopy folder: {3}\r\n", result.CopiedCount, result.WarningCount, result.FailedCount, DisplayText(result.RunFolder)));
                if (result.ExcludedCount > 0) activity.AppendText(String.Format("Intentionally excluded paths: {0} (including {1} selected sources).{2}\r\n", result.ExcludedCount, result.SkippedCount, String.IsNullOrEmpty(result.ReportFolder) ? "" : " See Exclusions.csv."));
                if (result.CompatibilityWarningCount > 0) activity.AppendText(String.Format("Filesystem compatibility warnings: {0}. Check the details above.\r\n", result.CompatibilityWarningCount));
                activity.AppendText(String.IsNullOrEmpty(result.ReportFolder) ? "Logs and reports were not saved. Results are shown here only.\r\n" : "Reports: " + DisplayText(result.ReportFolder) + Environment.NewLine);
                if (!String.IsNullOrEmpty(result.CleanupWarning)) activity.AppendText("Attention: " + result.CleanupWarning + Environment.NewLine);
                if (estimateProgress.Checked && result.Status != "Completed")
                {
                    if (progress.Value == progress.Maximum) progress.Value = progress.Maximum - 1;
                    remaining.Text = "Remaining: —";
                    percentage.Text = "Progress stopped.";
                }
                ClearFolderListsIfSuccessful(result);
            }
            catch (Exception error)
            {
                CopyWorkerException workerError = error as CopyWorkerException;
                stopUnconfirmed = workerError != null && !workerError.WorkerStopped;
                paused = (resuming || pauseRequested) && !(token.IsCancellationRequested && !pauseRequested) && resumableSession != null && workerError != null && workerError.WorkerStopped;
                if (!paused) resumableSession = null;
                status.Text = (stopUnconfirmed ? "Worker exit unconfirmed. Do not disconnect the drive. " : paused ? "Still paused. " : "Copy stopped. ") + DisplayText(error.Message);
                status.ForeColor = Color.FromArgb(164, 38, 38);
                activity.AppendText("ERROR: " + DisplayText(error.Message) + Environment.NewLine);
                if (estimateProgress.Checked)
                {
                    if (progress.Value == progress.Maximum) progress.Value = progress.Maximum - 1;
                    remaining.Text = "Remaining: —";
                    percentage.Text = paused ? "Paused — resume checks did not complete." : "Progress stopped.";
                }
            }
            finally
            {
                clock.Stop();
                timer.Stop();
                elapsed.Text = FormatElapsed(clock.Elapsed);
                cancellation.Dispose();
                cancellation = null;
                SetRunning(false);
                if (closeWhenFinished) Close();
            }
        }

        void ShowActivityMessage(string message)
        {
            if (!running) return;
            message = DisplayText(message);
            if (pauseRequested && message == "Cancelling the copy worker...") message = "Pausing the copy worker...";
            const string failurePrefix = "Progress estimate unavailable: ";
            if (message.StartsWith(failurePrefix, StringComparison.Ordinal))
            {
                estimateFailure = message.Substring(failurePrefix.Length);
                tips.SetToolTip(percentage, estimateFailure);
                if (estimateProgress.Checked && !cancellation.IsCancellationRequested)
                    percentage.Text = "Percentage unavailable — hover here for the reason.";
            }
            if (message.StartsWith("Scan: ", StringComparison.Ordinal))
            {
                string[] parts = message.Substring(6).Split(new char[] { '|' }, 3);
                if (parts.Length == 3)
                {
                    string folder = parts[2].Trim();
                    currentFolder.Text = "Path: " + folder;
                    tips.SetToolTip(currentFolder, folder);
                    long count;
                    string scanned = Int64.TryParse(parts[1].Trim(), out count) && count > 0 ? String.Format(" · {0:N0} entries", count) : "";
                    if (!cancellation.IsCancellationRequested) status.Text = parts[0].Trim() + scanned;
                    return;
                }
            }
            foreach (string prefix in new string[] { "Estimating copy size:", "Copying:", "Robocopy running:" })
            {
                if (!message.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string folder = message.Substring(prefix.Length).Trim();
                currentFolder.Text = "Path: " + folder;
                tips.SetToolTip(currentFolder, folder);
                if (!cancellation.IsCancellationRequested)
                    status.Text = prefix == "Estimating copy size:" ? "Calculating transfer size..." : "Copying...";
                break;
            }
            if (message != "Scan complete.") activity.AppendText(message + Environment.NewLine);
        }

        string DisplayText(string text) { return DisplayText(text, displaySession); }

        static string DisplayText(string text, CopySession session)
        {
            if (text == null || session == null || session.DisplayRoots == null) return text;
            foreach (var mapping in session.DisplayRoots)
            {
                int offset = 0, found;
                while ((found = text.IndexOf(mapping.Key, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    text = text.Substring(0, found) + mapping.Value + text.Substring(found + mapping.Key.Length);
                    offset = found + mapping.Value.Length;
                }
            }
            return text;
        }

        void UseSessionDisplay(CopySession session)
        {
            foreach (ListView list in new[] { sources, excluded })
            {
                VolumePath[] paths = list == sources ? session.Sources : session.ExcludedFolders;
                if (paths == null) continue;
                foreach (VolumePath path in paths)
                    foreach (ListViewItem item in list.Items)
                        if (String.Equals(item.Text, path.OriginalPath, StringComparison.OrdinalIgnoreCase) || String.Equals(item.Text, DisplayText(VolumeIdentity.BoundPath(path)), StringComparison.OrdinalIgnoreCase))
                        {
                            item.Text = DisplayText(VolumeIdentity.BoundPath(path), session);
                            item.ToolTipText = item.Text;
                        }
            }
            displaySession = session;
            if (!String.IsNullOrEmpty(session.DisplayDestination) && !String.Equals(destination.Text, session.DisplayDestination, StringComparison.OrdinalIgnoreCase))
            {
                activity.AppendText("Verified destination: " + session.DisplayDestination + Environment.NewLine);
                destination.Text = session.DisplayDestination;
            }
            currentFolder.Text = DisplayText(currentFolder.Text);
            tips.SetToolTip(currentFolder, DisplayText(tips.GetToolTip(currentFolder)));
        }

        void SetRunning(bool value)
        {
            running = value;
            bool locked = value || paused || stopUnconfirmed;
            foreach (Control control in new Control[] { sources, add, addFiles, remove, excluded, addExcluded, removeExcluded, destination, browse, mode, threads, oneDrive, estimateProgress, saveLogs, clearLists, start }) control.Enabled = !locked;
            speed.Text = "Speed: — MB/s";
            protectedFiles.Enabled = !locked && administrator;
            cancel.Enabled = value || paused;
            pause.Text = paused ? "Resume" : "Pause";
            pause.Enabled = paused && resumableSession != null;
            open.Enabled = !locked && !String.IsNullOrEmpty(resultFolder);
            if (value)
            {
                ResetProgressDisplay();
                currentFolder.Text = "Path: —";
                tips.SetToolTip(currentFolder, "");
                progress.Style = ProgressBarStyle.Marquee;
                progress.MarqueeAnimationSpeed = 30;
                if (estimateProgress.Checked) percentage.Text = "Preparing to calculate transfer size...";
            }
            else
            {
                progress.MarqueeAnimationSpeed = 0;
                progress.Style = ProgressBarStyle.Blocks;
            }
        }

        void ClearFolderListsIfSuccessful(CopyResult result)
        {
            if (!clearLists.Checked || result.Cancelled || result.Status != "Completed" || !String.IsNullOrEmpty(result.CleanupWarning)) return;
            sources.Items.Clear();
            excluded.Items.Clear();
            activity.AppendText("Source and excluded lists cleared. No source files were deleted.\r\n");
        }

        void CancelCopy()
        {
            pauseRequested = false;
            pause.Enabled = false;
            if (paused && !running)
            {
                paused = false;
                resumableSession = null;
                activeRequest = null;
                status.Text = "Cancelled. Partial files were kept.";
                percentage.Text = "Progress stopped.";
                remaining.Text = "Remaining: —";
                activity.AppendText("Paused transfer cancelled. Copied files were kept.\r\n");
                SetRunning(false);
                return;
            }
            if (cancellation == null) return;
            status.Text = "Cancelling... stopping this copy's worker.";
            cancel.Enabled = false;
            cancellation.Cancel();
        }

        void ClosingForm(object sender, FormClosingEventArgs args)
        {
            if (paused && !running)
            {
                args.Cancel = MessageBox.Show(this, "Close and discard this paused transfer's Resume state? Copied files will stay on disk.", "Transfer paused", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes;
                return;
            }
            if (!running) return;
            args.Cancel = true;
            if (closeWhenFinished) return;
            if (MessageBox.Show(this, "Cancel the current copy and close? Files already copied will stay on disk.", "Copy in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (!running) { args.Cancel = false; return; }
                closeWhenFinished = true;
                CancelCopy();
            }
        }

        async void OpenResult()
        {
            open.Enabled = false;
            try
            {
                string path = resultFolder;
                CopySession openingSession = resultSession;
                if (openingSession != null)
                    path = await Task.Run(delegate {
                        VolumeIdentity.Verify(openingSession.RunFolder, true, true);
                        return VolumeIdentity.FriendlyPath(openingSession.RunFolder);
                    });
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Cannot open copy folder", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { open.Enabled = !running && !paused && !stopUnconfirmed && !String.IsNullOrEmpty(resultFolder); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            if (CopyWorker.TryRunWorker(args)) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
