using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace document.Services
{
    public enum ShortcutResult { Created, SourceNotFound }

    /// <summary>
    /// 管理「文件整理」的捷徑。
    /// - 桌面捷徑按鈕：安裝版（ClickOnce）複製開始功能表的 .appref-ms 到桌面；開發版建立指向目前 exe 的 .lnk。
    /// - 安裝版第一次開啟時自動補一次桌面捷徑（ClickOnce 在桌面被 OneDrive 接管時常常建不出來）；
    ///   只做一次（在資料夾記一個標記檔），之後使用者自己刪掉就不會再被加回來，要的話可按按鈕重建。
    /// - 安裝版每次開啟時整理捷徑：更新後 ClickOnce 可能另外多放一個「文件整理 - 1」，只留最新的那個；
    ///   釘選在工作列的捷徑會指向某一版的 exe，更新後改指向目前這一版。
    /// </summary>
    public static class DesktopShortcutService
    {
        private const string ManifestName = "DocumentKeeper.application";
        private const string MarkerName = "desktop-shortcut.done";
        private const string ExeName = "DocumentKeeper.exe";
        internal const string DevShortcutName = "文件整理 (開發版).lnk";

        /// <summary>
        /// 固定的工作列身分（AppUserModelID）。各版本 exe 路徑不同，有了固定身分，
        /// 更新後的新版視窗仍會和工作列上的釘選合併成同一個圖示，而不是旁邊多一個。
        /// </summary>
        internal const string AppId = "asdZzz-coder.DocumentKeeper";

        // 環境變數 PASSWORDKEEPER_DESKTOP_DIR 可指定其他資料夾當作桌面（測試用）；
        // 平常不設定，桌面可能在 OneDrive 底下，一定要用系統回報的實際路徑
        private static string DesktopDirectory =>
            Environment.GetEnvironmentVariable("PASSWORDKEEPER_DESKTOP_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        private static string ProgramsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

        private static string TaskbarPinnedDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        // ---------- 桌面捷徑按鈕 ----------

        /// <summary>按下「桌面捷徑」按鈕：建立或重建桌面捷徑。</summary>
        public static ShortcutResult Create(bool isInstalled) =>
            Create(isInstalled, DesktopDirectory, ProgramsDirectory, Environment.ProcessPath ?? "");

        internal static ShortcutResult Create(bool isInstalled, string desktop, string programs, string exePath)
        {
            Directory.CreateDirectory(desktop);
            if (!isInstalled)
            {
                WriteLink(Path.Combine(desktop, DevShortcutName), exePath, appId: null, existing: false);
                return ShortcutResult.Created;
            }

            var source = Newest(FindShortcuts(programs, SearchOption.AllDirectories));
            if (source == null) return ShortcutResult.SourceNotFound;

            // 桌面上已經有（可能是舊的或壞掉的）就用開始功能表的內容覆蓋，不另外多放一個
            var target = Newest(FindShortcuts(desktop, SearchOption.TopDirectoryOnly)) ?? Path.Combine(desktop, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            return ShortcutResult.Created;
        }

        // ---------- 安裝版第一次開啟 ----------

        /// <summary>安裝版第一次開啟時補上桌面捷徑（只做一次）。</summary>
        public static void EnsureOnce(bool isInstalled) =>
            EnsureOnce(isInstalled, DataStore.DataDirectory, DesktopDirectory, ProgramsDirectory);

        internal static void EnsureOnce(bool isInstalled, string markerDir, string desktop, string programs)
        {
            var marker = Path.Combine(markerDir, MarkerName);
            if (!isInstalled || File.Exists(marker)) return;
            try
            {
                if (!FindShortcuts(desktop, SearchOption.TopDirectoryOnly).Any() &&
                    Create(true, desktop, programs, "") == ShortcutResult.SourceNotFound)
                    return; // 找不到開始功能表捷徑就下次再試，不寫標記
                Directory.CreateDirectory(markerDir);
                File.WriteAllText(marker, DateTime.Now.ToString("s"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 下次啟動再試 */ }
        }

        // ---------- 更新後整理捷徑 ----------

        /// <summary>
        /// 安裝版每次開啟時呼叫：桌面與開始功能表的重複捷徑只留最新的，工作列釘選改指向目前這一版。
        /// </summary>
        public static void TidyUp(bool isInstalled)
        {
            if (!isInstalled) return;
            TidyUp(DesktopDirectory, ProgramsDirectory, TaskbarPinnedDirectory, Environment.ProcessPath ?? "");
        }

        internal static void TidyUp(string desktop, string programs, string taskbar, string currentExe)
        {
            try
            {
                KeepNewestOnly(FindShortcuts(desktop, SearchOption.TopDirectoryOnly));
                // 開始功能表：每個資料夾各自只留一個（通常是 asdZzz-coder\文件整理）
                foreach (var group in FindShortcuts(programs, SearchOption.AllDirectories).GroupBy(Path.GetDirectoryName))
                    KeepNewestOnly(group);
                RetargetPinnedLinks(taskbar, currentExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { /* 下次啟動再試 */ }
        }

        /// <summary>
        /// 同一個資料夾裡有好幾個本程式的 .appref-ms 時，只留最新寫入的那個（ClickOnce 更新時寫的就是它）。
        /// </summary>
        private static void KeepNewestOnly(IEnumerable<string> shortcuts)
        {
            var list = shortcuts.ToList();
            var keep = Newest(list);
            foreach (var path in list.Where(p => p != keep))
                File.Delete(path);
        }

        /// <summary>
        /// 工作列釘選的捷徑（.lnk）會指向某一版的 exe（Apps\2.0\…\DocumentKeeper.exe）。
        /// 更新後舊版會被 ClickOnce 清掉，釘選就壞了，所以改指向目前這一版，並設定固定的工作列身分。
        /// </summary>
        private static void RetargetPinnedLinks(string taskbar, string currentExe)
        {
            if (!Directory.Exists(taskbar) || !IsClickOnceExe(currentExe)) return;
            foreach (var lnk in Directory.EnumerateFiles(taskbar, "*.lnk"))
            {
                var (target, appId) = ReadLink(lnk);
                if (!IsClickOnceExe(target)) continue;
                if (string.Equals(target, currentExe, StringComparison.OrdinalIgnoreCase) && appId == AppId) continue;
                WriteLink(lnk, currentExe, AppId, existing: true);
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, lnk, IntPtr.Zero); // 通知檔案總管重新讀取
            }
        }

        private static bool IsClickOnceExe(string path) =>
            Path.GetFileName(path).Equals(ExeName, StringComparison.OrdinalIgnoreCase) &&
            path.Contains(@"\Apps\2.0\", StringComparison.OrdinalIgnoreCase);

        // ---------- 共用 ----------

        /// <summary>找本程式的 ClickOnce 捷徑（.appref-ms，內容記錄安裝來源與 DocumentKeeper.application）。</summary>
        private static IEnumerable<string> FindShortcuts(string folder, SearchOption option) =>
            Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.appref-ms", option).Where(IsOurShortcut).ToList()
                : [];

        private static string? Newest(IEnumerable<string> paths) =>
            paths.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

        private static bool IsOurShortcut(string path)
        {
            try { return File.ReadAllText(path).Contains(ManifestName, StringComparison.OrdinalIgnoreCase); }
            catch (IOException) { return false; }
        }

        /// <summary>讓這個程式的視窗使用固定的工作列身分（必須在建立視窗之前呼叫）。</summary>
        public static void ApplyAppId() => SetCurrentProcessExplicitAppUserModelID(AppId);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr unused);

        private const int SHCNE_UPDATEITEM = 0x2000;
        private const uint SHCNF_PATHW = 0x0005;

        // ---------- .lnk（Windows Shell 的 IShellLink / IPropertyStore） ----------

        private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        private const ushort VT_LPWSTR = 31;
        private const int STGM_READWRITE = 2;

        /// <summary>建立或修改 .lnk：指向 targetPath，並可設定工作列身分。</summary>
        internal static void WriteLink(string linkPath, string targetPath, string? appId, bool existing)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                if (existing) ((IPersistFile)link).Load(linkPath, STGM_READWRITE);
                else link.SetDescription("文件整理");
                link.SetPath(targetPath);
                link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
                link.SetIconLocation(targetPath, 0);
                if (appId != null)
                {
                    var store = (IPropertyStore)link;
                    var key = AppUserModelIdKey;
                    var value = new PropVariant { VarType = VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(appId) };
                    try { store.SetValue(ref key, ref value); store.Commit(); }
                    finally { Marshal.FreeCoTaskMem(value.Pointer); }
                }
                ((IPersistFile)link).Save(linkPath, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        /// <summary>讀出 .lnk 指向的檔案與工作列身分（沒有設定時為 null）。</summary>
        internal static (string Target, string? AppId) ReadLink(string linkPath)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(linkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);

                var key = AppUserModelIdKey;
                ((IPropertyStore)link).GetValue(ref key, out var value);
                try { return (sb.ToString(), value.VarType == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null); }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

        // PROPVARIANT：64 位元下是 24 bytes，這裡只用到字串（VT_LPWSTR）
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort VarType;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
