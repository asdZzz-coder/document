using System.IO;
using document.Services;

namespace document.Tests
{
    /// <summary>資料夾與檔案操作的自動測試：都在暫存資料夾裡進行，測完刪掉。</summary>
    public class FolderServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DocumentKeeper-Tests-" + Guid.NewGuid().ToString("N"));

        public FolderServiceTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string Dir(params string[] parts)
        {
            var path = Path.Combine([_root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        private string FileAt(string content, params string[] parts)
        {
            var path = Path.Combine([_root, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        // ---------- 名稱檢查 ----------

        [Theory]
        [InlineData("", NameError.Empty)]
        [InlineData("   ", NameError.Empty)]
        [InlineData("a/b", NameError.InvalidChars)]
        [InlineData("報告?", NameError.InvalidChars)]
        [InlineData("a:b", NameError.InvalidChars)]
        [InlineData("結尾句點.", NameError.EndsWithDot)]
        [InlineData("CON", NameError.Reserved)]
        [InlineData("con.txt", NameError.Reserved)]
        [InlineData("LPT1", NameError.Reserved)]
        [InlineData("工作", NameError.None)]
        [InlineData("2026 報價.pdf", NameError.None)]
        [InlineData("CONTRACT", NameError.None)]
        public void Validate_ChecksWindowsRules(string name, NameError expected) =>
            Assert.Equal(expected, FolderService.Validate(name, _root));

        [Fact]
        public void Validate_RejectsTooLongNames() =>
            Assert.Equal(NameError.TooLong, FolderService.Validate(new string('字', FolderService.MaxNameLength + 1), _root));

        [Fact]
        public void Validate_RejectsExistingFolderOrFile_IgnoringCase()
        {
            Dir("工作");
            FileAt("x", "Report.pdf");
            Assert.Equal(NameError.Duplicate, FolderService.Validate("工作", _root));
            Assert.Equal(NameError.Duplicate, FolderService.Validate("report.PDF", _root));
        }

        [Fact]
        public void Validate_RenamingToOwnNameOrOnlyCaseIsAllowed()
        {
            var path = Dir("Work");
            Assert.Equal(NameError.None, FolderService.Validate("Work", _root, path));
            Assert.Equal(NameError.None, FolderService.Validate("WORK", _root, path));
        }

        [Fact]
        public void EveryErrorHasAMessage()
        {
            foreach (var error in Enum.GetValues<NameError>().Where(e => e != NameError.None))
                Assert.False(string.IsNullOrWhiteSpace(FolderService.Message(error)));
            Assert.Null(FolderService.Message(NameError.None));
        }

        // ---------- 撞名時自動改名 ----------

        [Fact]
        public void UniquePath_AddsNumberBeforeExtension()
        {
            Assert.Equal(Path.Combine(_root, "報告.pdf"), FolderService.UniquePath(_root, "報告.pdf"));
            FileAt("1", "報告.pdf");
            Assert.Equal(Path.Combine(_root, "報告 (2).pdf"), FolderService.UniquePath(_root, "報告.pdf"));
            FileAt("2", "報告 (2).pdf");
            Assert.Equal(Path.Combine(_root, "報告 (3).pdf"), FolderService.UniquePath(_root, "報告.pdf"));
        }

        [Fact]
        public void UniquePath_FolderNamesWithDotsKeepWholeName()
        {
            Dir("v1.2");
            Assert.Equal(Path.Combine(_root, "v1.2 (2)"), FolderService.UniquePath(_root, "v1.2", isFolder: true));
        }

        // ---------- 路徑 ----------

        [Fact]
        public void IsSameOrInside_DoesNotMatchSiblingWithSamePrefix()
        {
            var a = Path.Combine(_root, "工作");
            Assert.True(FolderService.IsSameOrInside(a, a));
            Assert.True(FolderService.IsSameOrInside(Path.Combine(a, "2026"), a));
            Assert.True(FolderService.IsSameOrInside(a.ToUpperInvariant() + "\\", a));
            Assert.False(FolderService.IsSameOrInside(Path.Combine(_root, "工作備份"), a));
            Assert.False(FolderService.IsSameOrInside(_root, a));
        }

        [Fact]
        public void RelativeDisplay_JoinsFolderNames()
        {
            Assert.Equal("", FolderService.RelativeDisplay(_root, _root));
            Assert.Equal("工作 › 2026 › 報價", FolderService.RelativeDisplay(_root, Path.Combine(_root, "工作", "2026", "報價")));
        }

        [Fact]
        public void Rebase_MovesPathsUnderTheOldFolder()
        {
            var oldDir = Path.Combine(_root, "舊");
            var newDir = Path.Combine(_root, "新");
            Assert.Equal(newDir, FolderService.Rebase(oldDir, oldDir, newDir));
            Assert.Equal(Path.Combine(newDir, "a", "b"), FolderService.Rebase(Path.Combine(oldDir, "a", "b"), oldDir, newDir));
            var other = Path.Combine(_root, "舊的");
            Assert.Equal(other, FolderService.Rebase(other, oldDir, newDir));
        }

        // ---------- 列出 ----------

        [Fact]
        public void SubFoldersAndFiles_SkipHiddenAndSortNaturally()
        {
            Dir("10 月");
            Dir("2 月");
            var hidden = Dir("隱藏");
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
            FileAt("a", "說明.txt");
            var ini = FileAt("x", "desktop.ini");
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

            Assert.Equal(["2 月", "10 月"], FolderService.SubFolders(_root).Select(d => d.Name));
            Assert.Equal(["說明.txt"], FolderService.Files(_root, recursive: false).Select(f => f.Name));
        }

        [Fact]
        public void Files_RecursiveIncludesSubFolders()
        {
            FileAt("a", "a.txt");
            FileAt("b", "工作", "b.txt");
            FileAt("c", "工作", "2026", "c.txt");
            Assert.Single(FolderService.Files(_root, recursive: false));
            Assert.Equal(["a.txt", "b.txt", "c.txt"], FolderService.Files(_root, recursive: true).Select(f => f.Name).Order());
        }

        // ---------- 新增 / 改名 ----------

        [Fact]
        public void CreateFolder_CanNest()
        {
            var work = FolderService.CreateFolder(_root, "工作");
            var year = FolderService.CreateFolder(work, "2026");
            var month = FolderService.CreateFolder(year, "10 月");
            Assert.True(Directory.Exists(month));
            Assert.Equal(Path.Combine(_root, "工作", "2026", "10 月"), month);
        }

        [Fact]
        public void Rename_FileAndFolder()
        {
            var file = FileAt("內容", "草稿.txt");
            var renamed = FolderService.Rename(file, "定稿.txt");
            Assert.False(File.Exists(file));
            Assert.Equal("內容", File.ReadAllText(renamed));

            var dir = Dir("舊名稱");
            FileAt("x", "舊名稱", "裡面.txt");
            var newDir = FolderService.Rename(dir, "新名稱");
            Assert.True(File.Exists(Path.Combine(newDir, "裡面.txt")));
            Assert.False(Directory.Exists(dir));
        }

        [Fact]
        public void Rename_OnlyChangingCaseWorks()
        {
            var dir = Dir("work");
            var renamed = FolderService.Rename(dir, "Work");
            Assert.Equal(["Work"], new DirectoryInfo(_root).GetDirectories().Select(d => d.Name));
            Assert.Equal(Path.Combine(_root, "Work"), renamed);
        }

        // ---------- 搬移 / 複製 ----------

        [Fact]
        public void MoveInto_RenamesWhenNameIsTaken()
        {
            var dest = Dir("工作");
            FileAt("舊的", "工作", "報告.pdf");
            var file = FileAt("新的", "報告.pdf");

            var moved = FolderService.MoveInto(file, dest);
            Assert.Equal(Path.Combine(dest, "報告 (2).pdf"), moved);
            Assert.Equal("新的", File.ReadAllText(moved));
            Assert.Equal("舊的", File.ReadAllText(Path.Combine(dest, "報告.pdf")));
            Assert.False(File.Exists(file));
        }

        [Fact]
        public void MoveInto_SameFolderDoesNothing()
        {
            var file = FileAt("x", "工作", "a.txt");
            Assert.Equal(file, FolderService.MoveInto(file, Path.Combine(_root, "工作")));
            Assert.True(File.Exists(file));
        }

        [Fact]
        public void MoveInto_MovesWholeFolder()
        {
            var src = Dir("專案");
            FileAt("x", "專案", "子", "a.txt");
            var dest = Dir("封存");
            var moved = FolderService.MoveInto(src, dest);
            Assert.True(File.Exists(Path.Combine(moved, "子", "a.txt")));
            Assert.False(Directory.Exists(src));
        }

        [Fact]
        public void MoveInto_RefusesToMoveFolderIntoItself()
        {
            var src = Dir("專案");
            var inner = Dir("專案", "子");
            Assert.Throws<InvalidOperationException>(() => FolderService.MoveInto(src, src));
            Assert.Throws<InvalidOperationException>(() => FolderService.MoveInto(src, inner));
            Assert.True(Directory.Exists(inner));
        }

        [Fact]
        public void CopyInto_KeepsOriginalAndCopiesFolders()
        {
            var outside = Path.Combine(_root, "外面");
            FileAt("1", "外面", "相片", "a.jpg");
            FileAt("2", "外面", "相片", "內層", "b.jpg");
            var dest = Dir("整理");

            var copied = FolderService.CopyInto(Path.Combine(outside, "相片"), dest);
            Assert.True(File.Exists(Path.Combine(copied, "內層", "b.jpg")));
            Assert.True(File.Exists(Path.Combine(outside, "相片", "a.jpg")));

            var again = FolderService.CopyInto(Path.Combine(outside, "相片"), dest);
            Assert.Equal(Path.Combine(dest, "相片 (2)"), again);
        }

        [Fact]
        public void MoveContents_MovesEverythingToNewRoot()
        {
            var oldRoot = Dir("舊位置");
            FileAt("a", "舊位置", "a.txt");
            FileAt("b", "舊位置", "工作", "b.txt");
            var newRoot = Path.Combine(_root, "新位置");

            Assert.Equal(2, FolderService.MoveContents(oldRoot, newRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(oldRoot));
            Assert.True(File.Exists(Path.Combine(newRoot, "a.txt")));
            Assert.True(File.Exists(Path.Combine(newRoot, "工作", "b.txt")));
        }
    }
}
