using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SapienCopy
{
    public enum OneDriveMode { All, DownloadedOnly, Exclude }

    internal sealed class ExcludedPath
    {
        internal string Path, Reason;
        internal bool Directory;
    }

    internal sealed class OneDrivePlan
    {
        internal readonly List<ExcludedPath> Paths = new List<ExcludedPath>();
        internal readonly List<string> CachedScopes = new List<string>();
        internal readonly List<CachedDirectory> CachedDirectories = new List<CachedDirectory>();
        internal bool SkipSource;
        internal bool OneDriveOnly;
        internal string Arguments = "";
        private readonly HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int indexedCount = -1;
        private HashSet<string> cachedEntries;

        internal bool IsCachedPath(string path)
        {
            foreach (string scope in CachedScopes) if (CopyEngine.IsSameOrChild(path, scope)) return true;
            return false;
        }

        internal bool IsSelected(string path)
        {
            if (IsExcluded(path)) return false;
            if (!IsCachedPath(path)) return true;
            if (cachedEntries == null)
            {
                cachedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CachedDirectory directory in CachedDirectories)
                {
                    cachedEntries.Add(directory.Path);
                    foreach (string file in directory.Files) cachedEntries.Add(Path.Combine(directory.Path, file));
                }
            }
            return cachedEntries.Contains(path);
        }

        internal void AddExcluded(ExcludedPath path)
        {
            if (IsExcluded(path.Path)) return;
            if (path.Directory && Paths.RemoveAll(delegate(ExcludedPath existing) { return CopyEngine.IsSameOrChild(existing.Path, path.Path); }) > 0)
            {
                indexedCount = -1;
                IsExcluded(path.Path);
            }
            Paths.Add(path);
            (path.Directory ? directories : files).Add(path.Path.TrimEnd('\\'));
            indexedCount = Paths.Count;
        }

        internal bool IsExcluded(string path)
        {
            if (indexedCount != Paths.Count)
            {
                files.Clear(); directories.Clear();
                foreach (ExcludedPath excluded in Paths) (excluded.Directory ? directories : files).Add(excluded.Path.TrimEnd('\\'));
                indexedCount = Paths.Count;
            }
            if (files.Contains(path)) return true;
            string ancestor = path.TrimEnd('\\');
            while (ancestor.Length >= 2)
            {
                if (directories.Contains(ancestor)) return true;
                int slash = ancestor.LastIndexOf('\\');
                if (slash < 0) break;
                ancestor = ancestor.Substring(0, slash);
            }
            return false;
        }

        internal void PrepareArguments(string jobPath, Func<string, string> robocopyPath = null)
        {
            if (SkipSource || Paths.Count == 0) return;
            StringBuilder arguments = new StringBuilder();
            StringBuilder job = new StringBuilder();
            foreach (bool directories in new bool[] { true, false })
            {
                bool heading = false;
                foreach (ExcludedPath path in Paths)
                {
                    if (path.Directory != directories) continue;
                    if (!heading)
                    {
                        arguments.Append(directories ? " /XD" : " /XF");
                        job.AppendLine(directories ? "/XD" : "/XF");
                        heading = true;
                    }
                    arguments.Append(" ").Append(CopyEngine.Quote(robocopyPath == null ? path.Path : robocopyPath(path.Path)));
                    job.AppendLine(robocopyPath == null ? path.Path : robocopyPath(path.Path));
                }
            }
            if (arguments.Length < 24000) { Arguments = arguments.ToString(); return; }
            // shortcut: Robocopy jobs use OEM text and unquoted whole lines; huge unencodable lists require smaller source selections.
            Encoding encoding = Encoding.GetEncoding((int)GetOEMCP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            CopyEngine.CheckHelperFile(jobPath);
            try { File.WriteAllText(jobPath, job.ToString(), encoding); }
            catch (EncoderFallbackException) { throw new InvalidOperationException("Too many exclusions contain characters unsupported by Robocopy job files. Select smaller source folders so all exclusions can be passed safely."); }
            Arguments = " /JOB:" + CopyEngine.Quote(jobPath);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }

    internal sealed class CachedDirectory
    {
        internal string Path;
        internal readonly List<string> Files = new List<string>();
    }

    internal static class OneDrivePolicy
    {
        internal const uint Offline = 0x1000, RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;

        internal static bool NeedsDownload(uint attributes)
        {
            return (attributes & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
        }

        internal static bool IsNameSurrogate(uint tag) { return (tag & 0x20000000) != 0; }

        internal static List<string> DiscoverRoots(Action<string> progress = null, CancellationToken cancellation = default(CancellationToken))
        {
            CopyEngine.ReportScan(progress, "OneDrive discovery", 0, "Registered accounts");
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string variable in new string[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }) AddRoot(roots, Environment.GetEnvironmentVariable(variable), progress, cancellation);
            using (RegistryKey accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
                if (accounts != null)
                    foreach (string name in accounts.GetSubKeyNames())
                        using (RegistryKey account = accounts.OpenSubKey(name))
                        {
                            AddRoot(roots, account.GetValue("UserFolder") as string, progress, cancellation);
                            using (RegistryKey cache = account.OpenSubKey("ScopeIdToMountPointPathCache"))
                                if (cache != null) foreach (string value in cache.GetValueNames()) AddRoot(roots, cache.GetValue(value) as string, progress, cancellation);
                        }
            string sid;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            foreach (RegistryKey hive in new RegistryKey[] { Registry.CurrentUser, Registry.LocalMachine })
                using (RegistryKey manager = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"))
                    if (manager != null)
                        foreach (string name in manager.GetSubKeyNames())
                            if (name.StartsWith("OneDrive!", StringComparison.OrdinalIgnoreCase))
                                using (RegistryKey users = manager.OpenSubKey(name + @"\UserSyncRoots"))
                                    if (users != null) AddRoot(roots, users.GetValue(sid) as string, progress, cancellation);
            using (RegistryKey providers = Registry.CurrentUser.OpenSubKey(@"Software\SyncEngines\Providers\OneDrive"))
                if (providers != null)
                    foreach (string name in providers.GetSubKeyNames())
                        using (RegistryKey provider = providers.OpenSubKey(name)) AddRoot(roots, provider.GetValue("MountPoint") as string, progress, cancellation);
            return new List<string>(roots);
        }

        private static void AddRoot(HashSet<string> roots, string path, Action<string> progress, CancellationToken cancellation)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (roots.Contains(full)) return;
            cancellation.ThrowIfCancellationRequested();
            CopyEngine.ReportScan(progress, "Check OneDrive root", roots.Count, full);
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.Directory) != 0) roots.Add(full);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        internal static Dictionary<string, OneDrivePlan> Build(CopyRequest request, IEnumerable<string> roots, Action<string> progress, CancellationToken cancellation)
        {
            Dictionary<string, string> manual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string excluded in request.ExcludedFolders ?? new string[0])
            {
                cancellation.ThrowIfCancellationRequested();
                CopyEngine.ReportScan(progress, "Resolve excluded folder", 0, excluded);
                manual[excluded] = CopyEngine.CanonicalDirectory(excluded);
            }
            HashSet<string> appliedManual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> targets = new List<string>();
            if (request.OneDriveMode != OneDriveMode.All)
                foreach (string root in roots)
                {
                    cancellation.ThrowIfCancellationRequested();
                    CopyEngine.ReportScan(progress, "Resolve OneDrive root", 0, root);
                    string canonical = CopyEngine.CanonicalDirectory(root);
                    if (targets.Exists(delegate(string existing) { return CopyEngine.IsSameOrChild(canonical, existing); })) continue;
                    targets.RemoveAll(delegate(string existing) { return CopyEngine.IsSameOrChild(existing, canonical); });
                    targets.Add(canonical);
                }
            Dictionary<string, OneDrivePlan> plans = new Dictionary<string, OneDrivePlan>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in request.Sources)
            {
                cancellation.ThrowIfCancellationRequested();
                OneDrivePlan plan = new OneDrivePlan();
                plans.Add(source, plan);
                CopyEngine.ReportScan(progress, "Resolve source", 0, source);
                string sourceTarget = CopyEngine.CanonicalDirectory(source);
                bool sourceDirectory = CopyEngine.IsDirectory(source);
                foreach (KeyValuePair<string, string> excluded in manual)
                {
                    bool applies = false;
                    if (CopyEngine.IsSameOrChild(source, excluded.Key) || CopyEngine.IsSameOrChild(sourceTarget, excluded.Value))
                    {
                        plan.AddExcluded(new ExcludedPath { Path = source, Directory = sourceDirectory, Reason = "Folder excluded by user" });
                        applies = true;
                    }
                    else
                    {
                        if (CopyEngine.IsSameOrChild(excluded.Key, source))
                        {
                            plan.AddExcluded(new ExcludedPath { Path = excluded.Key, Directory = true, Reason = "Folder excluded by user" });
                            applies = true;
                        }
                        if (CopyEngine.IsSameOrChild(excluded.Value, sourceTarget))
                        {
                            string mapped = Path.Combine(source, excluded.Value.Substring(sourceTarget.Length).TrimStart('\\'));
                            plan.AddExcluded(new ExcludedPath { Path = mapped, Directory = true, Reason = "Folder excluded by user" });
                            applies = true;
                        }
                    }
                    if (applies) appliedManual.Add(excluded.Key);
                }
                if (plan.IsExcluded(source)) plan.SkipSource = true;
                if (plan.SkipSource || request.OneDriveMode == OneDriveMode.All) continue;
                foreach (string rootTarget in targets)
                {
                    string scan = null;
                    if (CopyEngine.IsSameOrChild(sourceTarget, rootTarget)) { scan = source; plan.OneDriveOnly = true; }
                    else if (CopyEngine.IsSameOrChild(rootTarget, sourceTarget))
                    {
                        string relative = rootTarget.Substring(sourceTarget.Length).TrimStart('\\');
                        scan = Path.Combine(source, relative);
                    }
                    if (scan == null || plan.IsExcluded(scan)) continue;
                    if (request.OneDriveMode == OneDriveMode.Exclude)
                        plan.AddExcluded(new ExcludedPath { Path = scan, Directory = sourceDirectory, Reason = "OneDrive item excluded by selection" });
                    else
                    {
                        if (progress != null) progress("Checking downloaded OneDrive files: " + scan);
                        plan.CachedScopes.Add(scan);
                        ScanDownloaded(scan, plan, progress, cancellation);
                    }
                    if (plan.IsExcluded(source)) { plan.SkipSource = true; break; }
                }
            }
            if (progress != null)
                foreach (string excluded in manual.Keys)
                    if (!appliedManual.Contains(excluded)) progress("Excluded folder is outside selected sources: " + excluded);
            return plans;
        }

        private static void ScanDownloaded(string path, OneDrivePlan plan, Action<string> progress, CancellationToken cancellation)
        {
            CopyEngine.ScanReporter scan = new CopyEngine.ScanReporter(progress, "Downloaded OneDrive files");
            scan.Folder(path);
            Entry root;
            try { root = GetEntry(path, true); }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != 2) throw;
                plan.AddExcluded(new ExcludedPath { Path = path, Directory = CopyEngine.IsDirectory(path), Reason = "OneDrive item has no locally cached entry" });
                scan.Complete();
                return;
            }
            if ((root.Attributes & 16) == 0)
            {
                if (NeedsDownload(root.Attributes) && !IsNameSurrogate(root.Tag))
                    plan.AddExcluded(new ExcludedPath { Path = path, Reason = "OneDrive file is not fully downloaded" });
                else
                {
                    CachedDirectory parent = new CachedDirectory { Path = Path.GetDirectoryName(path) };
                    parent.Files.Add(Path.GetFileName(path));
                    plan.CachedDirectories.Add(parent);
                }
                scan.Entry(); scan.Complete();
                return;
            }
            if ((root.Attributes & 0x400) != 0 && IsNameSurrogate(root.Tag))
            {
                plan.CachedDirectories.Add(new CachedDirectory { Path = path });
                return;
            }
            Stack<string> directories = new Stack<string>();
            directories.Push(path);
            while (directories.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory = directories.Pop();
                if (plan.IsExcluded(directory)) continue;
                scan.Folder(directory);
                CachedDirectory snapshot = new CachedDirectory { Path = directory };
                plan.CachedDirectories.Add(snapshot);
                foreach (Entry item in Enumerate(directory, true))
                {
                    cancellation.ThrowIfCancellationRequested();
                    scan.Entry();
                    string child = Path.Combine(directory, item.Name);
                    if (plan.IsExcluded(child)) continue;
                    bool isDirectory = (item.Attributes & 16) != 0;
                    if ((item.Attributes & 0x400) != 0 && IsNameSurrogate(item.Tag))
                    {
                        if (isDirectory) plan.CachedDirectories.Add(new CachedDirectory { Path = child });
                        else snapshot.Files.Add(item.Name);
                        continue;
                    }
                    // Only cached directory entries are requested; cached files inside partly virtual directories remain eligible.
                    if (isDirectory) directories.Push(child);
                    else if (NeedsDownload(item.Attributes))
                        plan.AddExcluded(new ExcludedPath { Path = child, Directory = false, Reason = "OneDrive file is not fully downloaded" });
                    else snapshot.Files.Add(item.Name);
                }
            }
            scan.Complete();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Entry
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            internal uint SizeHigh, SizeLow, Tag, Reserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string AlternateName;
        }

        internal static Entry GetEntry(string path, bool onDiskOnly = false)
        {
            Entry entry;
            IntPtr handle = FindFirstMetadata(path, onDiskOnly, out entry);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect file metadata: " + path);
            FindClose(handle);
            return entry;
        }

        internal static IEnumerable<Entry> Enumerate(string directory, bool onDiskOnly = false)
        {
            Entry entry;
            IntPtr handle = FindFirstMetadata(directory.TrimEnd('\\') + "\\*", onDiskOnly, out entry);
            if (handle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2) yield break;
                if (onDiskOnly && (error == 50 || error == 87))
                    throw new InvalidOperationException("Windows could not enumerate cached entries safely: " + directory + ". Choose Exclude OneDrive completely or copy after making the files available locally. A cloud-fetching scan was not substituted.");
                throw new Win32Exception(error, "Cannot inspect folder metadata: " + directory);
            }
            try
            {
                do { if (entry.Name != "." && entry.Name != "..") yield return entry; } while (FindNextFile(handle, out entry));
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error, "Cannot finish inspecting folder metadata: " + directory);
            }
            finally { FindClose(handle); }
        }

        private static string Extended(string path) { return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path; }

        private static IntPtr FindFirstMetadata(string path, bool onDiskOnly, out Entry entry)
        {
            return onDiskOnly ? FindFirstFileEx(Extended(path), 0, out entry, 0, IntPtr.Zero, 4) : FindFirstFile(Extended(path), out entry);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string path, out Entry entry);
        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(string path, int information, out Entry entry, int search, IntPtr filter, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextFile(IntPtr handle, out Entry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);
    }
}
