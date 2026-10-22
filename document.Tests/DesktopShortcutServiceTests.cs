using System.IO;
using System.Text;
using document.Services;

namespace document.Tests
{
    /// <summary>
    /// 桌面捷徑的自動測試。全部在暫存資料夾裡模擬「桌面」與「開始功能表」，不會動到真正的桌面。
    /// </summary>
    public sealed class DesktopShortcutServiceTests : IDisposable
    {
        private const string AppRefName = "文件整理.appref-ms";
        private const string AppRefContent =
            "file:///C:/Users/test/AppData/Local/DocumentKeeper-Setup/DocumentKeeper.application" +
            "#DocumentKeeper.application, Culture=neutral, PublicKeyToken=0000000000000000, processorArchitecture=amd64";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "DocumentKeeper-Tests-" + Guid.NewGuid().ToString("N"));
        private string Desktop => Path.Combine(_root, "Desktop");
        private string Programs => Path.Combine(_root, "Programs");
        private string DataDir => Path.Combine(_root, "Data");

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        /// <summary>模擬 ClickOnce 安裝後在開始功能表放的 .appref-ms（UTF-16 含 BOM，與實際相同）。</summary>
        private string CreateStartMenuShortcut()
        {
            var folder = Path.Combine(Programs, "asdZzz-coder");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, AppRefName);
            File.WriteAllText(path, AppRefContent, Encoding.Unicode);
            return path;
        }

        private string[] DesktopFiles() =>
            Directory.Exists(Desktop) ? Directory.GetFiles(Desktop).Select(f => Path.GetFileName(f)).ToArray() : [];

        // ---------- 按鈕：安裝版 ----------

        [Fact]
        public void Installed_CopiesStartMenuShortcutToDesktop()
        {
            var source = CreateStartMenuShortcut();

            var result = DesktopShortcutService.Create(true, Desktop, Programs, "");

            Assert.Equal(ShortcutResult.Created, result);
            Assert.Equal([AppRefName], DesktopFiles());
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(Desktop, AppRefName)));
        }

        [Fact]
        public void Installed_WithoutStartMenuShortcut_ReportsSourceNotFound()
        {
            var result = DesktopShortcutService.Create(true, Desktop, Programs, "");

            Assert.Equal(ShortcutResult.SourceNotFound, result);
            Assert.Empty(DesktopFiles());
        }

        [Fact]
        public void Installed_ClickTwice_DoesNotDuplicate()
        {
            CreateStartMenuShortcut();

            DesktopShortcutService.Create(true, Desktop, Programs, "");
            DesktopShortcutService.Create(true, Desktop, Programs, "");

            Assert.Single(DesktopFiles());
        }

        [Fact]
        public void Installed_RenamedOrBrokenDesktopShortcut_IsRepairedInPlace()
        {
            var source = CreateStartMenuShortcut();
            Directory.CreateDirectory(Desktop);
            // 使用者改過名字、內容是舊版安裝路徑
            var renamed = Path.Combine(Desktop, "我的文件.appref-ms");
            File.WriteAllText(renamed, "file:///D:/old/DocumentKeeper.application#DocumentKeeper.application", Encoding.Unicode);

            DesktopShortcutService.Create(true, Desktop, Programs, "");

            Assert.Equal(["我的文件.appref-ms"], DesktopFiles());
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(renamed));
        }

        [Fact]
        public void Installed_IgnoresOtherAppsShortcuts()
        {
            CreateStartMenuShortcut();
            Directory.CreateDirectory(Desktop);
            var other = Path.Combine(Desktop, "其他程式.appref-ms");
            File.WriteAllText(other, "file:///C:/x/OtherApp.application#OtherApp.application", Encoding.Unicode);

            DesktopShortcutService.Create(true, Desktop, Programs, "");

            Assert.Equal(new[] { "其他程式.appref-ms", AppRefName }.Order(StringComparer.Ordinal), DesktopFiles().Order(StringComparer.Ordinal));
            Assert.Contains("OtherApp.application", File.ReadAllText(other));
        }

        // ---------- 按鈕：開發版（直接執行 exe） ----------

        [Fact]
        public void DevBuild_CreatesLnkPointingToExe()
        {
            var exe = Path.Combine(_root, "bin", "DocumentKeeper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, [0x4D, 0x5A]); // 只需要檔案存在

            var result = DesktopShortcutService.Create(false, Desktop, Programs, exe);

            Assert.Equal(ShortcutResult.Created, result);
            var lnk = Path.Combine(Desktop, DesktopShortcutService.DevShortcutName);
            Assert.True(File.Exists(lnk));
            Assert.Equal(exe, DesktopShortcutService.ReadLink(lnk).Target, ignoreCase: true);
        }

        // ---------- 安裝版第一次開啟時自動建立（只做一次） ----------

        [Fact]
        public void EnsureOnce_FirstRun_CreatesShortcutAndMarker()
        {
            CreateStartMenuShortcut();

            DesktopShortcutService.EnsureOnce(true, DataDir, Desktop, Programs);

            Assert.Equal([AppRefName], DesktopFiles());
            Assert.True(File.Exists(Path.Combine(DataDir, "desktop-shortcut.done")));
        }

        [Fact]
        public void EnsureOnce_UserDeletedShortcut_IsNotRecreated()
        {
            CreateStartMenuShortcut();
            DesktopShortcutService.EnsureOnce(true, DataDir, Desktop, Programs);
            File.Delete(Path.Combine(Desktop, AppRefName)); // 使用者自己刪掉

            DesktopShortcutService.EnsureOnce(true, DataDir, Desktop, Programs);

            Assert.Empty(DesktopFiles());
        }

        [Fact]
        public void EnsureOnce_NoStartMenuShortcutYet_RetriesNextTime()
        {
            DesktopShortcutService.EnsureOnce(true, DataDir, Desktop, Programs);
            Assert.False(File.Exists(Path.Combine(DataDir, "desktop-shortcut.done")));

            CreateStartMenuShortcut();
            DesktopShortcutService.EnsureOnce(true, DataDir, Desktop, Programs);

            Assert.Equal([AppRefName], DesktopFiles());
        }

        // ---------- 更新後整理捷徑 ----------

        private string Taskbar => Path.Combine(_root, "TaskBar");

        /// <summary>在模擬的 ClickOnce 快取（Apps\2.0）放一個某版本的 exe。</summary>
        private string FakeInstalledExe(string versionFolder)
        {
            var exe = Path.Combine(_root, "Apps", "2.0", "AB12CD34.EFG", versionFolder, "DocumentKeeper.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, [0x4D, 0x5A]);
            return exe;
        }

        private static void WriteAppRef(string path, DateTime lastWrite, string content = AppRefContent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.Unicode);
            File.SetLastWriteTime(path, lastWrite);
        }

        [Fact]
        public void TidyUp_StartMenuDuplicate_KeepsOnlyNewest()
        {
            var folder = Path.Combine(Programs, "asdZzz-coder", "文件整理");
            var old = Path.Combine(folder, "文件整理.appref-ms");
            var added = Path.Combine(folder, "文件整理 - 1 .appref-ms"); // 更新時 ClickOnce 多放的
            WriteAppRef(old, DateTime.Now.AddDays(-1));
            WriteAppRef(added, DateTime.Now);
            var other = Path.Combine(Programs, "asdZzz-coder", "簡易記帳", "簡易記帳.appref-ms");
            WriteAppRef(other, DateTime.Now.AddDays(-2), "file:///C:/x/Ledger.application#Ledger.application");

            DesktopShortcutService.TidyUp(Desktop, Programs, Taskbar, FakeInstalledExe("v12"));

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(added));
            Assert.True(File.Exists(other)); // 別的程式不動
        }

        [Fact]
        public void TidyUp_DesktopDuplicate_KeepsOnlyNewest()
        {
            WriteAppRef(Path.Combine(Desktop, "文件整理.appref-ms"), DateTime.Now);
            WriteAppRef(Path.Combine(Desktop, "文件整理 - 1 .appref-ms"), DateTime.Now.AddHours(-3));
            WriteAppRef(Path.Combine(Desktop, "簡易記帳.appref-ms"), DateTime.Now.AddDays(-2), "file:///C:/x/Ledger.application#Ledger.application");

            DesktopShortcutService.TidyUp(Desktop, Programs, Taskbar, FakeInstalledExe("v12"));

            // 用 Ordinal 排序：依語系排序時，中文的先後在中文 Windows（筆畫）與 GitHub Actions（英文）上不同
            Assert.Equal(new[] { "文件整理.appref-ms", "簡易記帳.appref-ms" }.Order(StringComparer.Ordinal), DesktopFiles().Order(StringComparer.Ordinal));
        }

        [Fact]
        public void TidyUp_SingleShortcuts_AreLeftAlone()
        {
            var desktopShortcut = Path.Combine(Desktop, "我的文件.appref-ms");
            WriteAppRef(desktopShortcut, DateTime.Now.AddDays(-5));
            var source = CreateStartMenuShortcut();

            DesktopShortcutService.TidyUp(Desktop, Programs, Taskbar, FakeInstalledExe("v12"));

            Assert.True(File.Exists(desktopShortcut));
            Assert.True(File.Exists(source));
        }

        [Fact]
        public void TidyUp_TaskbarPinToOldVersion_IsRetargetedToCurrent()
        {
            var oldExe = FakeInstalledExe("v11");
            var currentExe = FakeInstalledExe("v12");
            Directory.CreateDirectory(Taskbar);
            var pin = Path.Combine(Taskbar, "文件整理.lnk");
            DesktopShortcutService.WriteLink(pin, oldExe, appId: null, existing: false);
            var otherExe = Path.Combine(_root, "Other", "Other.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!);
            File.WriteAllBytes(otherExe, [0x4D, 0x5A]);
            var otherPin = Path.Combine(Taskbar, "Other.lnk");
            DesktopShortcutService.WriteLink(otherPin, otherExe, appId: null, existing: false);

            DesktopShortcutService.TidyUp(Desktop, Programs, Taskbar, currentExe);

            var (target, appId) = DesktopShortcutService.ReadLink(pin);
            Assert.Equal(currentExe, target, ignoreCase: true);
            Assert.Equal(DesktopShortcutService.AppId, appId);
            Assert.Equal(["Other.lnk", "文件整理.lnk"], Directory.GetFiles(Taskbar).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray());
            var (otherTarget, otherAppId) = DesktopShortcutService.ReadLink(otherPin);
            Assert.Equal(otherExe, otherTarget, ignoreCase: true); // 別的程式的釘選不動
            Assert.Null(otherAppId);
        }

        [Fact]
        public void TidyUp_TaskbarPinAlreadyCurrent_IsNotRewritten()
        {
            var currentExe = FakeInstalledExe("v12");
            Directory.CreateDirectory(Taskbar);
            var pin = Path.Combine(Taskbar, "文件整理.lnk");
            DesktopShortcutService.WriteLink(pin, currentExe, DesktopShortcutService.AppId, existing: false);
            var stamp = DateTime.Now.AddDays(-1);
            File.SetLastWriteTime(pin, stamp);

            DesktopShortcutService.TidyUp(Desktop, Programs, Taskbar, currentExe);

            Assert.Equal(stamp, File.GetLastWriteTime(pin));
        }

        [Fact]
        public void TidyUp_DevBuildExe_DoesNotTouchPins()
        {
            var oldExe = FakeInstalledExe("v11");
            Directory.CreateDirectory(Taskbar);
            var pin = Path.Combine(Taskbar, "文件整理.lnk");
            DesktopShortcutService.WriteLink(pin, oldExe, appId: null, existing: false);
            var devExe = Path.Combine(_root, "bin", "DocumentKeeper.exe"); // 不在 Apps\2.0

            DesktopShortcutService.TidyUp(Desktop, Programs, Taskbar, devExe);

            Assert.Equal(oldExe, DesktopShortcutService.ReadLink(pin).Target, ignoreCase: true);
        }

        [Fact]
        public void EnsureOnce_DevBuild_DoesNothing()
        {
            CreateStartMenuShortcut();

            DesktopShortcutService.EnsureOnce(false, DataDir, Desktop, Programs);

            Assert.Empty(DesktopFiles());
            Assert.False(Directory.Exists(DataDir));
        }
    }
}
