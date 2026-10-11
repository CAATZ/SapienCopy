using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SapienCopy
{
    // Explorer cannot use OLE drag/drop across the administrator boundary.
    sealed class FolderDrop : NativeWindow
    {
        const uint DropFiles = 0x0233, CopyGlobalData = 0x0049, AllowMessage = 1;
        readonly Control control;
        readonly Action<string[]> dropped;
        readonly Action<string> failed;

        public FolderDrop(Control control, Action<string[]> dropped, Action<string> failed)
        {
            this.control = control;
            this.dropped = dropped;
            this.failed = failed;
            control.AllowDrop = false;
            control.HandleCreated += Attach;
            control.HandleDestroyed += delegate { ReleaseHandle(); };
            if (control.IsHandleCreated) Attach(control, EventArgs.Empty);
        }

        void Attach(object sender, EventArgs args)
        {
            AssignHandle(control.Handle);
            // shortcut: 0x49 is legacy Shell HDROP marshaling; use the picker buttons if a future Windows version changes it.
            if (!ChangeWindowMessageFilterEx(Handle, DropFiles, AllowMessage, IntPtr.Zero) ||
                !ChangeWindowMessageFilterEx(Handle, CopyGlobalData, AllowMessage, IntPtr.Zero))
            {
                failed("Administrator drag-and-drop is unavailable. Use the picker buttons instead. (Windows error " + Marshal.GetLastWin32Error() + ")");
                return;
            }
            DragAcceptFiles(Handle, true);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg != DropFiles) { base.WndProc(ref message); return; }
            var paths = new List<string>();
            try
            {
                if (control.Enabled)
                {
                    uint count = DragQueryFile(message.WParam, UInt32.MaxValue, null, 0);
                    for (uint index = 0; index < count; index++)
                    {
                        uint length = DragQueryFile(message.WParam, index, null, 0);
                        if (length == 0 || length > 32767) continue;
                        var path = new StringBuilder((int)length + 1);
                        if (DragQueryFile(message.WParam, index, path, (uint)path.Capacity) == length)
                            paths.Add(path.ToString());
                    }
                }
            }
            finally { DragFinish(message.WParam); }
            if (paths.Count > 0) dropped(paths.ToArray());
            message.Result = IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr change);
        [DllImport("shell32.dll")]
        static extern void DragAcceptFiles(IntPtr window, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder file, uint length);
        [DllImport("shell32.dll")]
        static extern void DragFinish(IntPtr drop);
    }
}
