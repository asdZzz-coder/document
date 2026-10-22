using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace document.Services
{
    public enum AppTheme { System, Light, Dark }

    /// <summary>
    /// 淺色 / 深色 / 跟隨系統 主題。XAML 一律用 {DynamicResource XxxBrush} 取色，
    /// 切換時把 Application.Resources 裡的筆刷換成另一組顏色，畫面即時更新。
    /// 選擇存在資料資料夾的 theme.txt（不含任何文件）。
    /// 「跟隨系統」會讀 Windows 的「應用程式模式」，系統切換深淺色時跟著變。
    /// </summary>
    public static class ThemeService
    {
        private static string SettingFile => Path.Combine(DataStore.DataDirectory, "theme.txt");

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static AppTheme Mode { get; private set; } = AppTheme.System;

        /// <summary>目前畫面實際是否為深色（跟隨系統時依 Windows 設定）。</summary>
        public static bool IsDark { get; private set; }

        /// <summary>主題套用後觸發，讓視窗更新標題列顏色、按鈕圖示等。</summary>
        public static event Action? ThemeChanged;

        /// <summary>
        /// 兩組配色：key 是 App.xaml 裡的筆刷名稱。淺色 = App.xaml 的預設值（測試會檢查兩邊一致）。
        /// </summary>
        internal static readonly (string Key, string Light, string Dark)[] Palette =
        {
            ("AppBgBrush",              "#F4F5F9", "#111318"),
            ("CardBrush",               "#FFFFFF", "#1B1E25"),
            ("CardBorderBrush",         "#E6E8F0", "#2A2E38"),
            ("AccentBrush",             "#4F46E5", "#6366F1"),
            ("AccentHoverBrush",        "#4338CA", "#7477F4"),
            ("AccentPressedBrush",      "#3730A3", "#5155E8"),
            ("AccentSoftBrush",         "#EEF0FF", "#272B4A"),
            ("TextBrush",               "#111827", "#E6E8EE"),
            ("MutedBrush",              "#6B7280", "#A0A6B4"),
            ("SubtleBrush",             "#9CA3AF", "#717887"),
            ("LineBrush",               "#E2E5EC", "#30343F"),
            ("InputBgBrush",            "#F8F9FC", "#15181E"),
            ("DangerBrush",             "#C81E1E", "#F87171"),
            ("DangerSoftBrush",         "#FEF2F2", "#2C1B1F"),
            ("DangerHoverBrush",        "#FEE2E2", "#3A2026"),
            ("DangerPressedBrush",      "#FECACA", "#4A252C"),
            ("HoverBrush",              "#F3F4F8", "#242831"),
            ("PressedBrush",            "#E9EBF1", "#2C313B"),
            ("BorderHoverBrush",        "#CDD1DC", "#444A57"),
            ("GhostHoverBrush",         "#E9EBF2", "#262A33"),
            ("GhostPressedBrush",       "#DFE2EB", "#30353F"),
            ("ScrollThumbBrush",        "#CBD0DB", "#3A3F4B"),
            ("ScrollThumbHoverBrush",   "#9CA3AF", "#525967"),
            ("ScrollThumbPressedBrush", "#6B7280", "#6B7280"),
        };

        /// <summary>標題圖示的漸層（左上 → 右下）。</summary>
        internal static readonly (string Light1, string Light2, string Dark1, string Dark2) Gradient =
            ("#4F46E5", "#7C3AED", "#6366F1", "#8B5CF6");

        // ---------- 讀取 / 儲存 ----------

        /// <summary>讀取使用者上次的選擇；沒有或看不懂時為「跟隨系統」。</summary>
        public static void Load()
        {
            string? saved = null;
            try { if (File.Exists(SettingFile)) saved = File.ReadAllText(SettingFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Mode = Parse(saved);
        }

        internal static AppTheme Parse(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };

        internal static string ToSettingText(AppTheme mode) => mode.ToString().ToLowerInvariant();

        /// <summary>按鈕的切換順序：跟隨系統 → 淺色 → 深色 → 跟隨系統…</summary>
        internal static AppTheme Next(AppTheme mode) => mode switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System,
        };

        internal static bool ResolveIsDark(AppTheme mode, bool systemIsDark) => mode switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => systemIsDark,
        };

        public static void Cycle() => SetMode(Next(Mode));

        public static void SetMode(AppTheme mode)
        {
            Mode = mode;
            try
            {
                Directory.CreateDirectory(DataStore.DataDirectory);
                File.WriteAllText(SettingFile, ToSettingText(mode));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Apply();
        }

        // ---------- 套用 ----------

        /// <summary>把目前主題的顏色寫進 Application.Resources（App.xaml 載入後呼叫）。</summary>
        public static void Apply()
        {
            IsDark = ResolveIsDark(Mode, SystemUsesDarkTheme());
            var res = Application.Current.Resources;
            foreach (var (key, light, dark) in Palette)
                res[key] = Frozen(new SolidColorBrush(ParseColor(IsDark ? dark : light)));

            var g = Gradient;
            var gradient = new LinearGradientBrush(
                ParseColor(IsDark ? g.Dark1 : g.Light1), ParseColor(IsDark ? g.Dark2 : g.Light2), new Point(0, 0), new Point(1, 1));
            res["AccentGradient"] = Frozen(gradient);

            ThemeChanged?.Invoke();
        }

        private static T Frozen<T>(T brush) where T : Freezable { brush.Freeze(); return brush; }

        internal static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        /// <summary>Windows「選擇預設應用程式模式」是否為深色（讀不到時當作淺色）。</summary>
        public static bool SystemUsesDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ---------- 跟隨系統 ----------

        /// <summary>開始監看 Windows 深淺色設定；「跟隨系統」時系統一切換就跟著換。</summary>
        public static void WatchSystemTheme()
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Application.Current.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (Mode != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
            if (SystemUsesDarkTheme() == IsDark) return; // 其他設定變更，深淺色沒變
            Application.Current.Dispatcher.BeginInvoke(Apply);
        }
    }
}
