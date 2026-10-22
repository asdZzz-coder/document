using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace document.Services
{
    /// <summary>刪除檔案或資料夾時一律丟到資源回收筒，刪錯了還能從回收筒還原。</summary>
    public static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        /// <summary>丟到資源回收筒（確認已由程式自己問過，這裡不再跳 Windows 的確認視窗）。</summary>
        public static void Send(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = Path.GetFullPath(path) + "\0\0", // 清單以兩個 \0 結尾
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            int result = SHFileOperation(ref op);
            if (result != 0) throw new IOException($"無法刪除（錯誤碼 {result}），檔案可能正在被其他程式使用。", new Win32Exception(result));
            if (op.fAnyOperationsAborted) throw new OperationCanceledException();
        }
    }
}
