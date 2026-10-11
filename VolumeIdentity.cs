using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SapienCopy
{
    public sealed class VolumePath
    {
        public string VolumeRoot, FileSystem, RelativePath, OriginalPath, LinkTargetPath;
        public uint SerialNumber;
        public uint FileIdHigh, FileIdLow;
        public bool IsDirectory, HasFileId;
    }

    public static class VolumeIdentity
    {
        public static VolumePath Capture(string path, bool rejectReparsePoints = false)
        {
            string canonical = Resolve(path, true);
            string root = RootOf(canonical);
            VolumeDetails volume = ReadVolume(root);
            FileAttributes attributes = Attributes(canonical).Value;
            VolumePath saved = new VolumePath {
                VolumeRoot = root, SerialNumber = volume.SerialNumber, FileSystem = volume.FileSystem,
                RelativePath = canonical.Substring(root.Length).TrimEnd('\\'), OriginalPath = path,
                IsDirectory = (attributes & FileAttributes.Directory) != 0
            };
            FileInformation identity = FileIdentity(canonical);
            saved.FileIdHigh = identity.FileIndexHigh; saved.FileIdLow = identity.FileIndexLow; saved.HasFileId = true;
            if ((attributes & FileAttributes.ReparsePoint) != 0 && !rejectReparsePoints && IsNameSurrogateTag(ReparseTag(canonical)))
            {
                string target = Resolve(canonical, false);
                if (!SamePath(target, canonical)) saved.LinkTargetPath = target;
            }
            Verify(saved, true, rejectReparsePoints);
            return saved;
        }

        public static VolumePath Child(VolumePath parent, string relativePath, bool isDirectory)
        {
            BoundPath(parent);
            ValidateRelative(relativePath);
            if (!parent.IsDirectory) throw new InvalidOperationException("A file cannot contain a copy folder.");
            return new VolumePath {
                VolumeRoot = parent.VolumeRoot, SerialNumber = parent.SerialNumber, FileSystem = parent.FileSystem,
                RelativePath = parent.RelativePath.Length == 0 ? relativePath : parent.RelativePath + (relativePath.Length == 0 ? "" : "\\" + relativePath),
                OriginalPath = relativePath.Length == 0 ? parent.OriginalPath : Path.Combine(parent.OriginalPath, relativePath), IsDirectory = isDirectory,
                HasFileId = relativePath.Length == 0 && parent.HasFileId, FileIdHigh = relativePath.Length == 0 ? parent.FileIdHigh : 0, FileIdLow = relativePath.Length == 0 ? parent.FileIdLow : 0
            };
        }

        public static string BoundPath(VolumePath saved)
        {
            if (saved == null) throw new InvalidOperationException("The saved drive identity is missing.");
            if (!String.Equals(RootOf(saved.VolumeRoot), saved.VolumeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The saved volume root is invalid.");
            ValidateRelative(saved.RelativePath);
            if (String.IsNullOrWhiteSpace(saved.FileSystem)) throw new InvalidOperationException("The saved filesystem identity is missing.");
            return saved.VolumeRoot + saved.RelativePath;
        }

        public static string Verify(VolumePath saved, bool requireExists = true, bool rejectReparsePoints = false)
        {
            string path = VerifyVolume(saved, ReadVolume);
            FileAttributes? attributes = Attributes(path);
            if (requireExists && !attributes.HasValue)
                throw new InvalidOperationException("The saved file or folder is missing. Reconnect the original drive and restore the expected path: " + saved.OriginalPath);
            if (attributes.HasValue && ((attributes.Value & FileAttributes.Directory) != 0) != saved.IsDirectory)
                throw new InvalidOperationException("The saved path has changed between a file and a folder: " + saved.OriginalPath);

            string current = path;
            while (true)
            {
                FileAttributes? entry = Attributes(current);
                if (entry.HasValue)
                {
                    if (rejectReparsePoints && (entry.Value & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException("The destination contains a link or redirected folder. Resume is blocked: " + current);
                    if (!SamePath(Resolve(current, true), current))
                        throw new InvalidOperationException("The saved path now points somewhere else. Resume is blocked: " + saved.OriginalPath);
                    if (!rejectReparsePoints) break;
                }
                if (SamePath(current, saved.VolumeRoot)) break;
                int separator = current.TrimEnd('\\').LastIndexOf('\\');
                current = separator < saved.VolumeRoot.Length ? saved.VolumeRoot : current.Substring(0, separator);
            }
            if (!String.IsNullOrEmpty(saved.LinkTargetPath) && !SamePath(Resolve(path, false), saved.LinkTargetPath))
                throw new InvalidOperationException("A selected link now points somewhere else. Resume is blocked: " + saved.OriginalPath);
            if (attributes.HasValue && rejectReparsePoints && saved.HasFileId)
            {
                FileInformation identity = FileIdentity(path);
                if (identity.FileIndexHigh != saved.FileIdHigh || identity.FileIndexLow != saved.FileIdLow)
                    throw new InvalidOperationException("The original destination folder was replaced. Resume is blocked: " + saved.OriginalPath);
            }
            return path;
        }

        public static string FriendlyPath(VolumePath saved)
        {
            string bound = BoundPath(saved);
            uint required;
            char[] buffer = new char[32768];
            if (!GetVolumePathNamesForVolumeName(saved.VolumeRoot, buffer, (uint)buffer.Length, out required)) return bound;
            return FriendlyPath(saved, new string(buffer).Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static string FriendlyPath(VolumePath saved, string[] mountPaths)
        {
            string bound = BoundPath(saved), mount = null;
            foreach (string candidate in mountPaths)
                if (candidate.Length >= 3 && Char.IsLetter(candidate[0]) && candidate[1] == ':' && candidate[2] == '\\' &&
                    (mount == null || candidate.Length < mount.Length)) mount = candidate;
            return mount == null ? bound : mount.TrimEnd('\\') + "\\" + saved.RelativePath;
        }

        internal sealed class VolumeDetails
        {
            internal string Root, FileSystem;
            internal uint SerialNumber;
        }

        internal static string VerifyVolume(VolumePath saved, Func<string, VolumeDetails> lookup)
        {
            string path = BoundPath(saved);
            VolumeDetails actual = lookup(saved.VolumeRoot);
            if (actual == null || !String.Equals(actual.Root, saved.VolumeRoot, StringComparison.OrdinalIgnoreCase) ||
                actual.SerialNumber != saved.SerialNumber || !String.Equals(actual.FileSystem, saved.FileSystem, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The original drive is missing or its identity has changed. Resume is blocked: " + saved.OriginalPath);
            return path;
        }

        internal static string NormalizeScratchLocation(string path)
        {
            string full = Path.GetFullPath(path), ancestor = full;
            while (!Attributes(ancestor).HasValue)
            {
                ancestor = Path.GetDirectoryName(ancestor);
                if (String.IsNullOrEmpty(ancestor)) throw new InvalidOperationException("The temporary folder has no accessible local parent.");
            }
            string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            VolumePath scratch = Capture(ancestor, true), windows = Capture(windowsDirectory, true);
            VolumePath windowsRoot = Capture(Path.GetPathRoot(windowsDirectory), true);
            if (!scratch.IsDirectory || !String.Equals(scratch.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(scratch.VolumeRoot, windows.VolumeRoot, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(windowsRoot.VolumeRoot, windows.VolumeRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pause/resume requires a temporary folder on the Windows NTFS volume, so USB reconnection cannot redirect its private drive bindings.");
            VolumePath planned = Child(scratch, full.Substring(ancestor.TrimEnd('\\').Length).TrimStart('\\'), true);
            return ScratchPath(planned, windowsDirectory);
        }

        internal static string ScratchPath(VolumePath saved, string windowsDirectory)
        {
            BoundPath(saved);
            string root = Path.GetPathRoot(windowsDirectory);
            if (root.Length != 3 || !Char.IsLetter(root[0]) || root[1] != ':' || root[2] != '\\')
                throw new InvalidOperationException("The Windows temporary volume needs its Windows drive-letter path.");
            return root + saved.RelativePath;
        }

        private static VolumeDetails ReadVolume(string root)
        {
            StringBuilder name = new StringBuilder(64), fileSystem = new StringBuilder(64);
            uint serial, maximumName, flags;
            if (!GetVolumeNameForVolumeMountPoint(root, name, (uint)name.Capacity) ||
                !GetVolumeInformation(root, null, 0, out serial, out maximumName, out flags, fileSystem, (uint)fileSystem.Capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the original drive. Reconnect it and try Resume: " + root);
            return new VolumeDetails { Root = name.ToString(), SerialNumber = serial, FileSystem = fileSystem.ToString() };
        }

        private static string Resolve(string path, bool preserveFinalLink)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000u | (preserveFinalLink ? 0x00200000u : 0u), IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify file or folder: " + path);
                StringBuilder resolved = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, resolved, (uint)resolved.Capacity, 1);
                if (length == 0 || length >= resolved.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot obtain the volume identity: " + path);
                string value = resolved.ToString(), root = RootOf(value);
                return value.Length == root.Length ? root : value.TrimEnd('\\');
            }
        }

        private static string RootOf(string path)
        {
            const string prefix = @"\\?\Volume{";
            int end = path == null ? -1 : path.IndexOf("}\\", StringComparison.Ordinal);
            Guid guid;
            if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || end != prefix.Length + 36 ||
                !Guid.TryParse(path.Substring(prefix.Length, 36), out guid))
                throw new InvalidOperationException("A local volume GUID is required to identify this drive safely.");
            return path.Substring(0, end + 2);
        }

        private static void ValidateRelative(string relative)
        {
            if (relative == null || relative.Length > 32700) throw new InvalidOperationException("The saved relative path is invalid.");
            if (relative.Length == 0) return;
            foreach (string part in relative.Split('\\'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidOperationException("The saved path must stay inside its original volume.");
        }

        private static FileAttributes? Attributes(string path)
        {
            uint value = GetFileAttributes(path);
            if (value != UInt32.MaxValue) return (FileAttributes)value;
            int error = Marshal.GetLastWin32Error();
            if (error == 2 || error == 3) return null;
            throw new Win32Exception(error, "Cannot verify the saved path: " + path);
        }

        private static bool SamePath(string left, string right) { return String.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        internal static bool IsNameSurrogateTag(uint tag) { return (tag & 0x20000000u) != 0; }

        internal static uint HardLinkCount(string path) { return FileIdentity(path).Links; }

        private static uint ReparseTag(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                AttributeTag information;
                if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out information, (uint)Marshal.SizeOf(typeof(AttributeTag))))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the selected link's type: " + path);
                return information.Tag;
            }
        }

        private static FileInformation FileIdentity(string path)
        {
            using (SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                FileInformation identity;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out identity))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the original folder: " + path);
                return identity;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Serial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { internal uint Attributes, Tag; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder volumeName, uint length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder volumeName, uint volumeNameSize, out uint serial, out uint maximumNameLength, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathNamesForVolumeName(string volumeName, [Out] char[] paths, uint length, out uint required);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string path);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
    }

    internal sealed class RobocopyBindings : IDisposable
    {
        // Robocopy treats GUID paths as UNC paths; its private junctions still target the exact saved volumes.
        private readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> helperAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string DirectoryPath { get; private set; }

        internal RobocopyBindings(IEnumerable<VolumePath> paths, string parent = null)
        {
            string scratch = VolumeIdentity.NormalizeScratchLocation(parent ?? Path.GetTempPath());
            VolumeIdentity.Capture(scratch, true);
            DirectoryPath = Path.Combine(scratch, "SapienCopyBindings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            try
            {
                foreach (VolumePath path in paths)
                {
                    VolumeIdentity.BoundPath(path);
                    if (aliases.ContainsKey(path.VolumeRoot)) continue;
                    string alias = Path.Combine(DirectoryPath, "volume-" + (aliases.Count + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(alias);
                    aliases.Add(path.VolumeRoot, alias);
                    SetJunction(alias, path.VolumeRoot);
                }
            }
            catch { Dispose(); throw; }
        }

        internal string ToRobocopy(string text)
        {
            if (text == null) return null;
            foreach (KeyValuePair<string, string> pair in aliases) text = Replace(text, pair.Key, pair.Value + "\\");
            return text;
        }

        internal string ToHelperPath(string path)
        {
            string parent = Path.GetDirectoryName(path).TrimEnd('\\') + "\\", alias;
            if (!helperAliases.TryGetValue(parent, out alias))
            {
                string target = VolumeIdentity.BoundPath(VolumeIdentity.Capture(parent, true));
                alias = Path.Combine(DirectoryPath, "volume-" + (aliases.Count + helperAliases.Count + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(alias);
                helperAliases.Add(parent, alias);
                // Robocopy's log/job switches reject long paths even when its source/destination support them.
                SetJunction(alias, target);
            }
            return Path.Combine(alias, Path.GetFileName(path));
        }

        internal string ToVolumePath(string text)
        {
            if (text == null) return null;
            foreach (Dictionary<string, string> mapping in new[] { helperAliases, aliases })
            foreach (KeyValuePair<string, string> pair in mapping)
            {
                if (String.Equals(text.TrimEnd('\\'), pair.Value, StringComparison.OrdinalIgnoreCase) || String.Equals(text.TrimEnd('\\'), @"\\?\" + pair.Value, StringComparison.OrdinalIgnoreCase)) return pair.Key;
                text = Replace(text, @"\\?\" + pair.Value + "\\", pair.Key);
                text = Replace(text, pair.Value + "\\", pair.Key);
            }
            return text;
        }

        public void Dispose()
        {
            foreach (Dictionary<string, string> mapping in new[] { helperAliases, aliases })
            foreach (string alias in mapping.Values)
                try { Directory.Delete(alias, false); } catch (DirectoryNotFoundException) { }
            if (DirectoryPath != null)
                try { Directory.Delete(DirectoryPath, false); } catch (DirectoryNotFoundException) { }
        }

        private static string Replace(string text, string oldValue, string newValue)
        {
            int offset = 0, found;
            StringBuilder result = new StringBuilder();
            while ((found = text.IndexOf(oldValue, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                result.Append(text, offset, found - offset).Append(newValue);
                offset = found + oldValue.Length;
            }
            return result.Append(text, offset, text.Length - offset).ToString();
        }

        private static void SetJunction(string path, string volumeRoot)
        {
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + volumeRoot.Substring(4));
            byte[] print = Encoding.Unicode.GetBytes(volumeRoot);
            byte[] data = new byte[16 + substitute.Length + 2 + print.Length + 2];
            Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4);
            Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
            Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
            Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
            Array.Copy(BitConverter.GetBytes((ushort)print.Length), 0, data, 14, 2);
            Array.Copy(substitute, 0, data, 16, substitute.Length);
            Array.Copy(print, 0, data, 18 + substitute.Length, print.Length);
            using (SafeFileHandle handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                uint returned;
                if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create a private binding to the verified drive.");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
    }
}
