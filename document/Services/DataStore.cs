using System.IO;
using System.Text.Json;

namespace document.Services
{
    /// <summary>程式設定：文件存放位置（根資料夾）。文件本身就是存放位置裡的一般檔案與資料夾。</summary>
    public class AppSettings
    {
        public string? RootPath { get; set; }
    }

    /// <summary>
    /// 設定存在 %AppData%\DocumentKeeper\settings.json（主題在同一個資料夾的 theme.txt）。
    /// 文件不放在這裡，而是放在使用者設定的存放位置（預設「文件\文件整理」）。
    /// </summary>
    public static class DataStore
    {
        // 環境變數 DOCUMENTKEEPER_DATA_DIR 可指定其他資料夾（測試用）；平常不設定，存在 %AppData%\DocumentKeeper
        public static readonly string DataDirectory =
            Environment.GetEnvironmentVariable("DOCUMENTKEEPER_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocumentKeeper");

        private static string FilePath => Path.Combine(DataDirectory, "settings.json");

        /// <summary>沒有設定過存放位置時用的預設位置：「文件」資料夾底下的「文件整理」。</summary>
        public static string DefaultRootPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "文件整理");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文路徑直接存成中文，方便閱讀
        };

        public static AppSettings Load() => Load(FilePath);

        /// <summary>讀取設定；沒有檔案或檔案壞掉時用預設值（檔案壞掉會先備份）。</summary>
        internal static AppSettings Load(string path)
        {
            if (!File.Exists(path)) return new();
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
            }
            catch (JsonException)
            {
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                return new();
            }
        }

        public static void Save(AppSettings settings) => Save(FilePath, settings);

        internal static void Save(string path, AppSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, overwrite: true);
        }

        /// <summary>設定裡的存放位置；沒設定過就是預設位置。</summary>
        public static string RootPathOf(AppSettings settings) =>
            string.IsNullOrWhiteSpace(settings.RootPath) ? DefaultRootPath : settings.RootPath;
    }
}
