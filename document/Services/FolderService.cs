using System.IO;
using System.Runtime.InteropServices;

namespace document.Services
{
    public enum NameError { None, Empty, InvalidChars, Reserved, EndsWithDot, TooLong, Duplicate }

    /// <summary>
    /// 存放位置裡的資料夾與檔案操作：名稱檢查、新增、改名、搬移、複製。
    /// 資料夾就是硬碟上真正的資料夾，所以在檔案總管裡看到的結構和程式裡一樣。
    /// 不負責刪除（刪除一律丟到資源回收筒，見 RecycleBin）。
    /// </summary>
    public static class FolderService
    {
        public const int MaxNameLength = 120;

        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

        // ---------- 名稱 ----------

        /// <summary>
        /// 檢查要放進 parentDir 的名稱能不能用。改名時 currentPath 是原本的路徑：
        /// 名稱跟自己一樣（只改大小寫也算）不算重複。
        /// </summary>
        public static NameError Validate(string name, string parentDir, string? currentPath = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return NameError.Empty;
            if (name.IndexOfAny(InvalidChars) >= 0) return NameError.InvalidChars;
            if (name.EndsWith('.') || name.EndsWith(' ')) return NameError.EndsWithDot;
            if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name).TrimEnd())) return NameError.Reserved;
            if (name.Length > MaxNameLength) return NameError.TooLong;

            var target = Path.Combine(parentDir, name);
            bool isSelf = currentPath != null && SamePath(target, currentPath);
            if (!isSelf && (File.Exists(target) || Directory.Exists(target))) return NameError.Duplicate;
            return NameError.None;
        }

        public static string? Message(NameError error) => error switch
        {
            NameError.Empty => "請輸入名稱。",
            NameError.InvalidChars => "名稱不能包含下列符號：\\ / : * ? \" < > |",
            NameError.Reserved => "這個名稱是 Windows 保留的，請換一個。",
            NameError.EndsWithDot => "名稱的最後不能是句點或空格。",
            NameError.TooLong => $"名稱最多 {MaxNameLength} 個字。",
            NameError.Duplicate => "這裡已經有同名的資料夾或檔案。",
            _ => null,
        };

        /// <summary>
        /// 在 dir 裡找一個不會撞名的路徑：報告.pdf → 報告 (2).pdf → 報告 (3).pdf…
        /// isFolder 時整個名稱都當主檔名（資料夾名稱裡的句點不是副檔名）。
        /// </summary>
        public static string UniquePath(string dir, string name, bool isFolder = false)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
            var ext = isFolder ? "" : Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                path = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
        }

        // ---------- 路徑 ----------

        private static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        public static bool SamePath(string a, string b) =>
            string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>path 是 root 本身，或在 root 底下（任何一層）。</summary>
        public static bool IsSameOrInside(string path, string root)
        {
            var p = Normalize(path);
            var r = Normalize(root);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>顯示用的相對路徑：「工作 › 2026 › 報價」；根資料夾本身回傳空字串。</summary>
        public static string RelativeDisplay(string root, string path)
        {
            var rel = Path.GetRelativePath(root, path);
            return rel == "." ? "" : string.Join(" › ", rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>把 oldPrefix 底下的路徑換成 newPrefix 底下的同一個位置（資料夾改名或搬移後更新記住的路徑）。</summary>
        public static string Rebase(string path, string oldPrefix, string newPrefix)
        {
            if (!IsSameOrInside(path, oldPrefix)) return path;
            var rel = Path.GetRelativePath(oldPrefix, path);
            return rel == "." ? newPrefix : Path.Combine(newPrefix, rel);
        }

        // ---------- 列出 ----------

        /// <summary>隱藏或系統檔（desktop.ini、Thumbs.db…）不顯示。</summary>
        public static bool IsVisible(FileSystemInfo info) =>
            (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

        public static List<DirectoryInfo> SubFolders(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateDirectories()
                    .Where(IsVisible)
                    .OrderBy(d => d.Name, NaturalComparer.Instance)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
        }

        /// <summary>資料夾裡的檔案；recursive 時連子資料夾一起找（讀不到的子資料夾略過）。</summary>
        public static List<FileInfo> Files(string dir, bool recursive)
        {
            var result = new List<FileInfo>();
            try
            {
                var info = new DirectoryInfo(dir);
                result.AddRange(info.EnumerateFiles().Where(IsVisible));
                if (recursive)
                    foreach (var sub in SubFolders(dir))
                        result.AddRange(Files(sub.FullName, recursive: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }

        // ---------- 新增 / 改名 ----------

        public static string CreateFolder(string parentDir, string name)
        {
            var path = Path.Combine(parentDir, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>檔案或資料夾改名，回傳新路徑。只改大小寫時 Windows 不允許直接改，先改成暫時名稱再改回來。</summary>
        public static string Rename(string path, string newName)
        {
            var parent = Path.GetDirectoryName(Normalize(path))!;
            var target = Path.Combine(parent, newName);
            bool isDir = Directory.Exists(path);
            if (string.Equals(Path.GetFileName(Normalize(path)), newName, StringComparison.Ordinal)) return target;

            if (SamePath(path, target))
            {
                var temp = Path.Combine(parent, "~rename-" + Guid.NewGuid().ToString("N")[..8]);
                MoveRaw(path, temp, isDir);
                path = temp;
            }
            MoveRaw(path, target, isDir);
            return target;
        }

        // ---------- 搬移 / 複製 ----------

        /// <summary>
        /// 把檔案或資料夾搬進 destDir，撞名時自動加上 (2)、(3)…，回傳新路徑。
        /// 已經在 destDir 裡就不動；不能把資料夾搬進它自己或它底下的資料夾。
        /// </summary>
        public static string MoveInto(string path, string destDir)
        {
            path = Normalize(path);
            bool isDir = Directory.Exists(path);
            if (SamePath(Path.GetDirectoryName(path)!, destDir)) return path;
            if (isDir && IsSameOrInside(destDir, path))
                throw new InvalidOperationException("不能把資料夾搬進它自己或它底下的資料夾。");

            var target = UniquePath(destDir, Path.GetFileName(path), isDir);
            MoveRaw(path, target, isDir);
            return target;
        }

        /// <summary>把檔案或資料夾（含底下所有內容）複製進 destDir，撞名時自動改名，回傳新路徑。</summary>
        public static string CopyInto(string path, string destDir)
        {
            path = Normalize(path);
            bool isDir = Directory.Exists(path);
            if (isDir && IsSameOrInside(destDir, path))
                throw new InvalidOperationException("不能把資料夾複製到它自己底下。");

            var target = UniquePath(destDir, Path.GetFileName(path), isDir);
            if (isDir) CopyDirectory(path, target);
            else File.Copy(path, target);
            return target;
        }

        /// <summary>把 fromDir 裡的所有東西搬到 toDir（變更存放位置時用），回傳搬了幾項。</summary>
        public static int MoveContents(string fromDir, string toDir)
        {
            Directory.CreateDirectory(toDir);
            int count = 0;
            foreach (var entry in new DirectoryInfo(fromDir).EnumerateFileSystemInfos().ToList())
            {
                MoveInto(entry.FullName, toDir);
                count++;
            }
            return count;
        }

        /// <summary>搬移：同一個磁碟直接改路徑；不同磁碟的資料夾 Windows 不能直接搬，改成複製後刪除原本的。</summary>
        private static void MoveRaw(string from, string to, bool isDir)
        {
            if (!isDir)
            {
                File.Move(from, to);
                return;
            }
            if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(from, to);
                return;
            }
            CopyDirectory(from, to);
            Directory.Delete(from, recursive: true);
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.EnumerateDirectories(from))
                CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    /// <summary>和檔案總管一樣的排序：「2」排在「10」前面。</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);

        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}
