using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SapienCopy
{
    internal static class DestinationPolicy
    {
        internal static bool IsPortable(string fileSystem)
        {
            return String.Equals(fileSystem, "exFAT", StringComparison.OrdinalIgnoreCase) || String.Equals(fileSystem, "FAT32", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ValidateFileSystem(string fileSystem, bool backupMode)
        {
            if (!IsPortable(fileSystem) && !String.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose an NTFS, exFAT or FAT32 destination. The selected drive uses " + fileSystem + ". This app will not format or change the drive.");
            if (IsPortable(fileSystem) && backupMode)
                throw new InvalidOperationException("Protected-file copying requires an NTFS destination because backup mode overrides the option to omit alternate data streams. Uncheck Include protected files or choose NTFS.");
        }

        internal static bool IsCloud(uint tag) { return (tag & 0xFFFF0FFFu) == 0x9000001Au; }

        internal static string UnsupportedReason(string fileSystem, uint attributes, uint tag, ulong size)
        {
            if (!IsPortable(fileSystem)) return null;
            if ((attributes & 0x400) != 0 && !IsCloud(tag)) return "Windows links or this reparse-point type cannot be preserved";
            if ((attributes & 0x4000) != 0) return "EFS-encrypted data cannot be preserved without losing its encryption";
            if (String.Equals(fileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) && (attributes & 16) == 0 && size > UInt32.MaxValue)
                return "The file exceeds FAT32's 4,294,967,295-byte limit";
            return null;
        }

        internal static List<ExcludedPath> Check(CopyRequest request, Dictionary<string, OneDrivePlan> plans, Action<string> progress, CancellationToken cancellation)
        {
            List<ExcludedPath> warnings = new List<ExcludedPath>();
            if (!IsPortable(request.DestinationFileSystem)) return warnings;
            CopyEngine.ScanReporter scan = new CopyEngine.ScanReporter(progress, "Destination compatibility");
            foreach (string source in request.Sources)
            {
                cancellation.ThrowIfCancellationRequested();
                OneDrivePlan plan = plans[source];
                if (plan.SkipSource) continue;
                if (progress != null) progress("Checking destination compatibility: " + source);
                cancellation.ThrowIfCancellationRequested();
                scan.Folder(source);
                OneDrivePolicy.Entry root = CopyEngine.IsVolumeRoot(source) ? new OneDrivePolicy.Entry { Attributes = (uint)File.GetAttributes(source) } : OneDrivePolicy.GetEntry(source, plan.IsCachedPath(source));
                CheckEntry(request.DestinationFileSystem, source, root, warnings);
                if ((root.Attributes & 16) == 0) continue;
                Stack<string> pending = new Stack<string>();
                pending.Push(source);
                while (pending.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    scan.Folder(directory);
                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (OneDrivePolicy.Entry item in OneDrivePolicy.Enumerate(directory, plan.IsCachedPath(directory)))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        scan.Entry();
                        string path = Path.Combine(directory, item.Name);
                        if (!plan.IsSelected(path)) continue;
                        CheckName(names, path);
                        CheckEntry(request.DestinationFileSystem, path, item, warnings);
                        if ((item.Attributes & 16) != 0) pending.Push(path);
                    }
                }
            }
            scan.Complete();
            if (progress != null && warnings.Count > 0)
            {
                progress("Destination compatibility: " + warnings.Count + " metadata item(s) cannot be preserved; primary file data remains eligible for copying.");
                for (int i = 0; i < Math.Min(10, warnings.Count); i++) progress("Metadata warning: " + warnings[i].Path + " — " + warnings[i].Reason);
                if (warnings.Count > 10) progress(request.SaveLogs ? "Additional metadata warnings will be listed in Exclusions.csv." : "Additional metadata warnings are present. Enable Save logs and reports to retain the full list.");
            }
            return warnings;
        }

        internal static void CheckName(HashSet<string> names, string path)
        {
            if (!names.Add(Path.GetFileName(path)))
                throw new InvalidOperationException("The destination cannot distinguish source names that differ only by letter case: " + path + ". Rename the conflicting items or choose a compatible destination. No source files have been copied.");
        }

        internal static void CheckEntry(string fileSystem, string path, OneDrivePolicy.Entry item, List<ExcludedPath> warnings)
        {
            string reason = UnsupportedReason(fileSystem, item.Attributes, item.Tag, ((ulong)item.SizeHigh << 32) | item.SizeLow);
            if (reason != null) throw new InvalidOperationException(reason + " on " + fileSystem + ": " + path + ". Choose an NTFS destination, or exclude this item's folder and try again. No source files have been copied.");
            bool cloud = (item.Attributes & 0x400) != 0 && IsCloud(item.Tag);
            if (cloud || OneDrivePolicy.NeedsDownload(item.Attributes))
                warnings.Add(new ExcludedPath { Path = path, Reason = "Alternate streams are not preserved; stream inspection was deferred to avoid recalling cloud/offline content. Primary file data follows the selected OneDrive policy." });
            else
                foreach (string stream in NamedStreams(path))
                    warnings.Add(new ExcludedPath { Path = path + stream, Reason = "Alternate data stream omitted on " + fileSystem + "; primary file data remains eligible for copying." });
            if ((item.Attributes & (0x200 | 0x800)) != 0)
                warnings.Add(new ExcludedPath { Path = path, Reason = "Sparse/compressed storage metadata is not preserved on " + fileSystem + "; primary file data remains eligible for copying and may require more disk space." });
        }

        internal static IEnumerable<string> NamedStreams(string path)
        {
            StreamData data;
            IntPtr handle = FindFirstStream(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path, 0, out data, 0);
            if (handle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 38 || error == 87) yield break; // No streams, or a source filesystem without streams.
                throw new Win32Exception(error, "Cannot inspect alternate data streams: " + path);
            }
            try
            {
                do { if (!String.Equals(data.Name, "::$DATA", StringComparison.OrdinalIgnoreCase)) yield return data.Name; } while (FindNextStream(handle, out data));
                int error = Marshal.GetLastWin32Error();
                if (error != 38) throw new Win32Exception(error, "Cannot finish inspecting alternate data streams: " + path);
            }
            finally { FindClose(handle); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StreamData
        {
            internal long Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string Name;
        }
        [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextStream(IntPtr handle, out StreamData data);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FindClose(IntPtr handle);
    }
}
