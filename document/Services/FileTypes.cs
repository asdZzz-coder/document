using System.IO;

namespace document.Services
{
    /// <summary>檔案類型：清單上顯示的類型名稱、圖示（Segoe Fluent Icons）與圖示底色。</summary>
    public record FileType(string Label, string Glyph, string Color);

    public static class FileTypes
    {
        private static readonly FileType Pdf = new("PDF", "\uEA90", "#DC2626");
        private static readonly FileType Word = new("Word", "\uE8A5", "#2563EB");
        private static readonly FileType Excel = new("Excel", "\uE9D2", "#16A34A");
        private static readonly FileType PowerPoint = new("PowerPoint", "\uE7F4", "#EA580C");
        private static readonly FileType Text = new("文字", "\uE70B", "#64748B");
        private static readonly FileType Image = new("圖片", "\uEB9F", "#7C3AED");
        private static readonly FileType Video = new("影片", "\uE714", "#DB2777");
        private static readonly FileType Audio = new("音訊", "\uE8D6", "#0891B2");
        private static readonly FileType Archive = new("壓縮檔", "\uF012", "#D97706");
        private static readonly FileType Program = new("程式", "\uE756", "#475569");
        private static readonly FileType Shortcut = new("捷徑", "\uE71B", "#0D9488");

        private static readonly Dictionary<string, FileType> ByExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = Pdf,
            [".doc"] = Word, [".docx"] = Word, [".odt"] = Word, [".rtf"] = Word,
            [".xls"] = Excel, [".xlsx"] = Excel, [".xlsm"] = Excel, [".csv"] = Excel, [".ods"] = Excel,
            [".ppt"] = PowerPoint, [".pptx"] = PowerPoint, [".odp"] = PowerPoint,
            [".txt"] = Text, [".md"] = Text, [".log"] = Text, [".json"] = Text, [".xml"] = Text,
            [".jpg"] = Image, [".jpeg"] = Image, [".png"] = Image, [".gif"] = Image, [".bmp"] = Image,
            [".webp"] = Image, [".heic"] = Image, [".tif"] = Image, [".tiff"] = Image, [".svg"] = Image,
            [".mp4"] = Video, [".mov"] = Video, [".avi"] = Video, [".mkv"] = Video, [".wmv"] = Video,
            [".mp3"] = Audio, [".wav"] = Audio, [".m4a"] = Audio, [".flac"] = Audio,
            [".zip"] = Archive, [".rar"] = Archive, [".7z"] = Archive,
            [".exe"] = Program, [".msi"] = Program, [".bat"] = Program, [".cmd"] = Program,
            [".lnk"] = Shortcut, [".url"] = Shortcut,
        };

        /// <summary>依副檔名判斷類型；不認得的顯示副檔名本身（沒有副檔名就是「檔案」）。</summary>
        public static FileType Of(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            if (ByExtension.TryGetValue(ext, out var type)) return type;
            var label = ext.Length > 1 ? ext[1..].ToUpperInvariant() : "檔案";
            return new FileType(label, "\uE7C3", "#6B7280");
        }

        /// <summary>檔案大小：1023 B、12.3 KB、4.5 MB、1.2 GB。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double value = bytes;
            int i = -1;
            do { value /= 1024; i++; } while (value >= 1024 && i < units.Length - 1);
            return value >= 100 ? $"{value:0} {units[i]}" : $"{value:0.#} {units[i]}";
        }
    }
}
