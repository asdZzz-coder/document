using System.IO;
using document.Services;

namespace document.Tests
{
    /// <summary>設定檔（存放位置）與檔案類型的自動測試。</summary>
    public class StorageTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "DocumentKeeper-Settings-" + Guid.NewGuid().ToString("N"));

        public StorageTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private string SettingsFile => Path.Combine(_dir, "settings.json");

        [Fact]
        public void Load_WithoutFileUsesDefaultRoot()
        {
            var settings = DataStore.Load(SettingsFile);
            Assert.Null(settings.RootPath);
            Assert.Equal(DataStore.DefaultRootPath, DataStore.RootPathOf(settings));
            Assert.EndsWith("文件整理", DataStore.DefaultRootPath);
        }

        [Fact]
        public void SaveAndLoad_KeepsChineseRootPath()
        {
            DataStore.Save(SettingsFile, new AppSettings { RootPath = @"D:\資料\公司文件" });
            Assert.Contains("公司文件", File.ReadAllText(SettingsFile)); // 中文直接存成中文
            Assert.Equal(@"D:\資料\公司文件", DataStore.RootPathOf(DataStore.Load(SettingsFile)));
            Assert.False(File.Exists(SettingsFile + ".tmp"));
        }

        [Fact]
        public void Load_BrokenFileIsBackedUpAndDefaultsUsed()
        {
            File.WriteAllText(SettingsFile, "{ 壞掉");
            var settings = DataStore.Load(SettingsFile);
            Assert.Null(settings.RootPath);
            Assert.False(File.Exists(SettingsFile));
            Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        }

        // ---------- 檔案類型 ----------

        [Theory]
        [InlineData("報告.pdf", "PDF")]
        [InlineData("合約.DOCX", "Word")]
        [InlineData("預算.xlsx", "Excel")]
        [InlineData("簡報.pptx", "PowerPoint")]
        [InlineData("相片.JPG", "圖片")]
        [InlineData("備份.zip", "壓縮檔")]
        [InlineData("圖面.dwg", "DWG")]
        [InlineData("README", "檔案")]
        public void FileTypes_LabelsByExtension(string name, string label) =>
            Assert.Equal(label, FileTypes.Of(name).Label);

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(1023, "1023 B")]
        [InlineData(1024, "1 KB")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(5 * 1024 * 1024, "5 MB")]
        [InlineData(150L * 1024 * 1024, "150 MB")]
        [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
        public void FormatSize_UsesReadableUnits(long bytes, string expected) =>
            Assert.Equal(expected, FileTypes.FormatSize(bytes));
    }
}
