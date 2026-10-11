using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SapienCopy;

static class FolderDropTests
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "send")
            {
                IntPtr window = new IntPtr(Int64.Parse(File.ReadAllText(args[1] + ".handle")));
                IntPtr drop = MakeDrop(new string[] { "C:\\Synthetic folder", "C:\\Unicode \u00e1 folder" });
                try
                {
                    if (!PostMessage(window, 0x0233, drop, IntPtr.Zero)) throw new Exception("Cross-process drop failed: " + Marshal.GetLastWin32Error());
                    Console.WriteLine("PASS: lower-integrity sender delivered native drop.");
                }
                finally { GlobalFree(drop); }
                return 0;
            }
            Application.EnableVisualStyles();
            foreach (Control list in new Control[] { new ListView(), new TextBox() })
            using (var form = new Form())
            using (list)
            {
                form.Controls.Add(list);
                string[] received = null;
                string error = null;
                new FolderDrop(list, delegate(string[] paths) { received = paths; }, delegate(string message) { error = message; });
                form.CreateControl();
                IntPtr handle = list.Handle;
                if (error != null) throw new Exception(error);
                if (list.AllowDrop) throw new Exception("OLE was not disabled for native drops.");
                if ((GetWindowLong(handle, -20) & 0x10) == 0) throw new Exception("Native drop target was not registered.");
                if (args.Length > 0 && args[0] == "receive")
                {
                    File.WriteAllText(args[1] + ".handle", handle.ToInt64().ToString());
                    var watch = Stopwatch.StartNew();
                    while (received == null && watch.ElapsedMilliseconds < 30000) { Application.DoEvents(); Thread.Sleep(20); }
                    CheckPaths(received);
                    File.WriteAllText(args[1] + ".result", "PASS: cross-integrity HDROP received and decoded.");
                    return 0;
                }
                SendMessage(handle, 0x0233, MakeDrop(new string[] { "C:\\Synthetic folder", "C:\\Unicode \u00e1 folder" }), IntPtr.Zero);
                CheckPaths(received);
                received = null;
                list.Enabled = false;
                SendMessage(handle, 0x0233, MakeDrop(new string[] { "C:\\Busy folder" }), IntPtr.Zero);
                if (received != null) throw new Exception("Disabled drop target accepted files.");
                list.Enabled = true;
                typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, null);
                if (error != null) throw new Exception(error);
                SendMessage(list.Handle, 0x0233, MakeDrop(new string[] { "C:\\Synthetic folder", "C:\\Unicode \u00e1 folder" }), IntPtr.Zero);
                CheckPaths(received);
                received = null;
                SendMessage(list.Handle, 0x0233, IntPtr.Zero, IntPtr.Zero);
                if (received != null) throw new Exception("Empty drop was accepted.");
            }
            Console.WriteLine("PASS: list and destination text box native drops, Unicode/multiple paths, busy lock, handle recreation and empty payload.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void CheckPaths(string[] received)
    {
        if (received == null || received.Length != 2 || received[0] != "C:\\Synthetic folder" || received[1] != "C:\\Unicode \u00e1 folder")
            throw new Exception("Dropped Unicode folder paths did not survive HDROP.");
    }

    static IntPtr MakeDrop(string[] paths)
    {
        byte[] names = Encoding.Unicode.GetBytes(String.Join("\0", paths) + "\0\0");
        IntPtr memory = GlobalAlloc(0x42, new UIntPtr((uint)(20 + names.Length)));
        IntPtr data = GlobalLock(memory);
        Marshal.WriteInt32(data, 0, 20);
        Marshal.WriteInt32(data, 16, 1);
        Marshal.Copy(names, 0, IntPtr.Add(data, 20), names.Length);
        GlobalUnlock(memory);
        return memory;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr window, int index);
}
