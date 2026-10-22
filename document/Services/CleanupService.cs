using System.IO;

namespace document.Services
{
    /// <summary>
    /// 啟動時清掉更新後不再需要的舊檔案，只刪除下列明確指定的檔案，絕不碰使用者的文件與設定（settings.json）：
    ///   1. %TEMP%\DocumentKeeper-Update（下載並解壓的更新包）
    ///   2. 資料夾內寫入中斷留下的 settings.json.tmp
    /// 任何一項刪除失敗（例如檔案還被安裝程式占用）都直接略過，下次啟動再試。
    /// </summary>
    public static class CleanupService
    {
        public static void RunInBackground() => Task.Run(Run);

        public static void Run()
        {
            UpdateService.TryDeleteDirectory(UpdateService.DownloadFolder);
            DeleteMatching(DataStore.DataDirectory, "settings.json.tmp");
        }

        /// <summary>刪除 folder 內符合 pattern 的檔案（不遞迴、不刪資料夾）。</summary>
        public static void DeleteMatching(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 被占用，下次再清 */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
