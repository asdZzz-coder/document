using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using document.Services;

namespace document.Models
{
    /// <summary>左側資料夾清單的一列（最上面是存放位置本身「全部文件」），只供畫面使用。</summary>
    public class FolderNode : INotifyPropertyChanged
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        /// <summary>0 = 存放位置本身（全部文件），1 = 第一層資料夾，以此類推。</summary>
        public int Level { get; init; }
        /// <summary>這個資料夾（含所有子資料夾）裡的文件數。</summary>
        public int Count { get; init; }
        public bool HasChildren { get; init; }
        public bool IsExpanded { get; init; }

        public bool IsRoot => Level == 0;
        public bool IsFolder => !IsRoot;
        public Thickness Indent => new(IsRoot ? 0 : (Level - 1) * 16, 0, 0, 0);
        public string Tooltip => IsRoot ? $"存放位置裡的所有文件\n{Path}" : Path;

        // Segoe Fluent Icons：箭頭（右 / 下）、全部文件（文件庫）、資料夾（開 / 關）
        public string Arrow => IsExpanded ? "\uE70D" : "\uE76C";
        public string Icon => IsRoot ? "\uE8F1" : IsExpanded && HasChildren ? "\uE838" : "\uE8B7";

        private bool _isDropTarget;
        /// <summary>拖曳檔案經過時亮起，表示放開就會放進這個資料夾。</summary>
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                if (_isDropTarget == value) return;
                _isDropTarget = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString() => Name;
    }

    /// <summary>中間清單的一列：子資料夾或文件，只供畫面使用。</summary>
    public class FileRow
    {
        public required string Path { get; init; }
        public required string Name { get; init; }
        public bool IsFolder { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public DateTime Created { get; init; }
        /// <summary>資料夾裡的文件數（只有資料夾列用到）。</summary>
        public int FileCount { get; init; }
        /// <summary>不在目前資料夾裡時（全部文件 / 搜尋結果）顯示它在哪個資料夾，否則是空字串。</summary>
        public string FolderText { get; init; } = "";

        public FileType Type => IsFolder ? FolderType : FileTypes.Of(Name);
        private static readonly FileType FolderType = new("資料夾", "\uE8B7", "#4F46E5");

        public string Glyph => Type.Glyph;
        public string TypeLabel => Type.Label;
        public string SizeText => IsFolder ? $"{FileCount} 份文件" : FileTypes.FormatSize(Size);
        public string ModifiedText => Modified.ToString("yyyy/MM/dd HH:mm");
        public string CreatedText => Created.ToString("yyyy/MM/dd HH:mm");

        /// <summary>第二行：類型 · 大小 · 修改日期。</summary>
        public string Detail => IsFolder
            ? $"資料夾  ·  {SizeText}"
            : $"{TypeLabel}  ·  {SizeText}  ·  {ModifiedText}";

        public bool HasFolderText => FolderText.Length > 0;

        /// <summary>類型圖示的底色：同一色系由亮到暗的斜向漸層。</summary>
        public Brush TileBrush
        {
            get
            {
                var c = (Color)ColorConverter.ConvertFromString(Type.Color);
                var dark = Color.FromRgb((byte)(c.R * 0.8), (byte)(c.G * 0.8), (byte)(c.B * 0.8));
                var brush = new LinearGradientBrush(c, dark, new Point(0, 0), new Point(1, 1));
                brush.Freeze();
                return brush;
            }
        }

        public override string ToString() => Name;
    }
}
