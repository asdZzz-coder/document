using System.Windows;
using document.Services;

namespace document
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            ThemeService.Load(); // 讀取使用者選的主題（跟隨系統 / 淺色 / 深色）

            // 只允許開一個視窗，避免兩個視窗互相覆蓋對方存的資料
            using var mutex = new Mutex(true, SingleInstanceName(), out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("文件整理已經開啟了。", "文件整理");
                return;
            }

            // 安裝版使用固定的工作列身分，更新後新版視窗才會跟工作列釘選合併
            if (new UpdateService().IsInstalled) DesktopShortcutService.ApplyAppId();

            var app = new App();
            app.InitializeComponent();
            ThemeService.Apply(); // 必須在 App.xaml 載入之後，顏色才不會被覆蓋
            ThemeService.WatchSystemTheme();
            app.Run();
        }

        /// <summary>
        /// 同一個資料資料夾只能開一個視窗。用 DOCUMENTKEEPER_DATA_DIR 指定測試資料夾時另外算一個，
        /// 這樣使用者開著正式版時，測試仍能用測試資料啟動，兩邊也不會寫到同一份資料。
        /// </summary>
        private static string SingleInstanceName()
        {
            const string name = @"Local\DocumentKeeper.SingleInstance";
            var custom = Environment.GetEnvironmentVariable("DOCUMENTKEEPER_DATA_DIR");
            if (string.IsNullOrEmpty(custom)) return name;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(custom).ToUpperInvariant())))[..16];
            return name + "." + hash;
        }
    }
}
