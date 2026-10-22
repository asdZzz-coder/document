using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace document.Services
{
    public record UpdateInfo(Version Version, string DownloadUrl, long Size);

    /// <summary>
    /// 線上更新：程式以 ClickOnce 安裝，但不使用 ClickOnce 內建的更新。
    /// 由本類別向 GitHub Releases 查詢最新版，使用者同意後下載 ClickOnce 安裝包（zip），
    /// 放回當初安裝的資料夾後開啟 DocumentKeeper.application，由 ClickOnce 升級成新版。
    /// 只有「安裝版」才能更新；直接從 Visual Studio / dotnet run 執行時 IsInstalled 為 false，會略過。
    /// </summary>
    public class UpdateService
    {
        private const string Owner = "asdZzz-coder";
        private const string Repo = "document";
        public const string PackageAssetName = "DocumentKeeper-ClickOnce.zip";

        /// <summary>下載與解壓更新包的資料夾（啟動時由 CleanupService 清掉）。</summary>
        public static readonly string DownloadFolder = Path.Combine(Path.GetTempPath(), "DocumentKeeper-Update");

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DocumentKeeper-Updater"); // GitHub API 要求有 User-Agent
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>
        /// 是否為 ClickOnce 安裝版。ClickOnce 啟動程式時會設定 ClickOnce_IsNetworkDeployed；
        /// 保險起見也檢查執行位置是否在 ClickOnce 的安裝快取（%LocalAppData%\Apps\2.0）。
        /// </summary>
        public bool IsInstalled =>
            string.Equals(Environment.GetEnvironmentVariable("ClickOnce_IsNetworkDeployed"), "true", StringComparison.OrdinalIgnoreCase)
            || AppContext.BaseDirectory.Contains(@"\Apps\2.0\", StringComparison.OrdinalIgnoreCase);

        /// <summary>程式本身的版本（建置時由 -p:Version 帶入，與 ClickOnce 的 ApplicationVersion 一致）。</summary>
        public Version Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            }
        }

        /// <summary>
        /// 是否由「APP 啟動器」下載執行（%LocalAppData%\AppLauncher\apps\{id}\{版本}\...）。
        /// 啟動器裝好一個版本會在版本資料夾放 .launcher.json；這種情況由啟動器負責更新，程式本身只檢查有沒有新版。
        /// </summary>
        public bool IsLauncherManaged { get; } = FindLauncherMarker(AppContext.BaseDirectory);

        /// <summary>正式版（安裝版或從啟動器開啟），不是直接從原始碼執行的開發版。</summary>
        public bool IsRelease => IsInstalled || IsLauncherManaged;

        public string CurrentVersion => IsRelease ? Version.ToString() : "開發版";

        private static bool FindLauncherMarker(string dir)
        {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, ".launcher.json"))) return true;
            return false;
        }

        /// <summary>檢查是否有新版；沒有、或非安裝版則回傳 null。</summary>
        public async Task<UpdateInfo?> CheckAsync()
        {
            if (!IsRelease) return null;

            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest <= Version) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == PackageAssetName)
                    return new UpdateInfo(latest, asset.GetProperty("browser_download_url").GetString()!, asset.GetProperty("size").GetInt64());
            }
            return null; // 該版本還沒有附上安裝包（打包尚未完成）
        }

        /// <summary>下載新版安裝包、解壓並啟動安裝；呼叫端應在這之後結束程式。</summary>
        public async Task DownloadAndLaunchAsync(UpdateInfo info, Action<int>? progress = null)
        {
            // 先清掉先前（例如失敗或中斷的更新）遺留的檔案
            TryDeleteDirectory(DownloadFolder);
            Directory.CreateDirectory(DownloadFolder);
            var zipPath = Path.Combine(DownloadFolder, $"DocumentKeeper-{info.Version}.zip");
            var extractDir = Path.Combine(DownloadFolder, info.Version.ToString());

            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? info.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                var buf = new byte[81920];
                long done = 0;
                int last = -1, n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != last) { last = pct; progress?.Invoke(pct); }
                }
            }

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));
            if (!File.Exists(Path.Combine(extractDir, ManifestName)))
                throw new FileNotFoundException("更新包內容不完整，請稍後再試。", ManifestName);

            // ClickOnce 會記住「從哪個資料夾安裝的」，從別的位置安裝同一個程式會被拒絕。
            // 所以把新版放回當初安裝的同一個資料夾，再從那裡執行，ClickOnce 就會當成一般升級。
            var installDir = FindInstallSourceFolder() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DocumentKeeper-Setup");
            await Task.Run(() => ReplaceInstallSource(extractDir, installDir));

            await EnsureClickOnceServiceRunningAsync();
            var manifest = Path.Combine(installDir, ManifestName);
            Process.Start(new ProcessStartInfo(manifest) { UseShellExecute = true, WorkingDirectory = installDir });
        }

        /// <summary>
        /// ClickOnce 的背景服務 dfsvc.exe 沒在執行時，開啟 .application 有時完全沒反應（不報錯、也沒有紀錄）。
        /// 程式開啟一陣子後 dfsvc 就會自己結束，所以按「更新」時通常已經不在了 → 先把它叫起來再開安裝檔。
        /// </summary>
        private static async Task EnsureClickOnceServiceRunningAsync()
        {
            if (Process.GetProcessesByName("dfsvc").Length > 0) return;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var dfsvc = new[] { @"Microsoft.NET\Framework64\v4.0.30319\dfsvc.exe", @"Microsoft.NET\Framework\v4.0.30319\dfsvc.exe" }
                .Select(p => Path.Combine(windows, p))
                .FirstOrDefault(File.Exists);
            if (dfsvc == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(dfsvc) { UseShellExecute = false });
                await Task.Delay(1000); // 等服務準備好
            }
            catch (System.ComponentModel.Win32Exception) { /* 叫不起來就照原本方式開安裝檔 */ }
        }

        private const string ManifestName = "DocumentKeeper.application";

        /// <summary>
        /// 找出當初安裝時用的 DocumentKeeper.application 所在資料夾：
        /// 先看 ClickOnce 提供的環境變數，再看開始功能表捷徑（.appref-ms 內記錄了安裝來源）。
        /// </summary>
        private static string? FindInstallSourceFolder()
        {
            foreach (var name in new[] { "ClickOnce_UpdateLocation", "ClickOnce_ActivationUri" })
            {
                var dir = FolderFromManifestUri(Environment.GetEnvironmentVariable(name));
                if (dir != null) return dir;
            }

            try
            {
                var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                foreach (var file in Directory.EnumerateFiles(programs, "*.appref-ms", SearchOption.AllDirectories))
                {
                    // 內容格式：file:///C:/.../DocumentKeeper.application#DocumentKeeper.application, Culture=...
                    var text = File.ReadAllText(file);
                    if (!text.Contains(ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = FolderFromManifestUri(text.Split('#')[0].Trim().Trim('\0', '\uFEFF'));
                    if (dir != null) return dir;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>只接受本機（或網路芳鄰）路徑的 .application；網址或不存在的磁碟回傳 null。</summary>
        private static string? FolderFromManifestUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            var dir = Path.GetDirectoryName(uri.LocalPath);
            if (dir == null || !Directory.Exists(Path.GetPathRoot(dir))) return null;
            return dir;
        }

        /// <summary>
        /// 用新版安裝包取代安裝來源資料夾的內容：只動 ClickOnce 自己的檔案
        /// （DocumentKeeper.application、安裝.cmd、Application Files\DocumentKeeper_*），其他檔案不碰。
        /// </summary>
        private static void ReplaceInstallSource(string from, string to)
        {
            Directory.CreateDirectory(to);
            var oldVersions = Path.Combine(to, "Application Files");
            if (Directory.Exists(oldVersions))
                foreach (var d in Directory.EnumerateDirectories(oldVersions, "DocumentKeeper_*"))
                    TryDeleteDirectory(d);

            foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(to, Path.GetRelativePath(from, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
        }

        internal static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 被占用，下次再清 */ }
        }
    }
}
