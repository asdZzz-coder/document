using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml.Linq;
using document.Services;

namespace document.Tests
{
    /// <summary>
    /// 主題（跟隨系統 / 淺色 / 深色）的自動測試：切換邏輯、設定檔，以及 XAML 與配色表是否一致。
    /// </summary>
    public class ThemeServiceTests
    {
        private static string SourceDir([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here)!, "..", "document");

        private static string ReadXaml(string name) => File.ReadAllText(Path.Combine(SourceDir(), name));

        // ---------- 切換邏輯 ----------

        [Fact]
        public void Next_CyclesSystemLightDarkAndBack()
        {
            Assert.Equal(AppTheme.Light, ThemeService.Next(AppTheme.System));
            Assert.Equal(AppTheme.Dark, ThemeService.Next(AppTheme.Light));
            Assert.Equal(AppTheme.System, ThemeService.Next(AppTheme.Dark));
        }

        [Theory]
        [InlineData(AppTheme.Light, false, false)]
        [InlineData(AppTheme.Light, true, false)]
        [InlineData(AppTheme.Dark, false, true)]
        [InlineData(AppTheme.Dark, true, true)]
        [InlineData(AppTheme.System, false, false)]
        [InlineData(AppTheme.System, true, true)]
        public void ResolveIsDark_FollowsChoiceOrSystem(AppTheme mode, bool systemIsDark, bool expected) =>
            Assert.Equal(expected, ThemeService.ResolveIsDark(mode, systemIsDark));

        // ---------- 設定檔 theme.txt ----------

        [Theory]
        [InlineData("light", AppTheme.Light)]
        [InlineData("dark", AppTheme.Dark)]
        [InlineData("system", AppTheme.System)]
        [InlineData(" Dark\r\n", AppTheme.Dark)]
        [InlineData("", AppTheme.System)]
        [InlineData(null, AppTheme.System)]
        [InlineData("purple", AppTheme.System)]
        public void Parse_ReadsSettingOrFallsBackToSystem(string? text, AppTheme expected) =>
            Assert.Equal(expected, ThemeService.Parse(text));

        [Theory]
        [InlineData(AppTheme.System)]
        [InlineData(AppTheme.Light)]
        [InlineData(AppTheme.Dark)]
        public void SettingText_RoundTrips(AppTheme mode) =>
            Assert.Equal(mode, ThemeService.Parse(ThemeService.ToSettingText(mode)));

        // ---------- 配色表 ----------

        [Fact]
        public void Palette_KeysAreUniqueAndColorsValid()
        {
            var keys = ThemeService.Palette.Select(p => p.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            foreach (var (_, light, dark) in ThemeService.Palette)
            {
                ThemeService.ParseColor(light);
                ThemeService.ParseColor(dark);
            }
        }

        [Fact]
        public void Palette_LightColorsMatchAppXamlDefaults()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var xamlBrushes = XDocument.Parse(ReadXaml("App.xaml")).Descendants()
                .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(x + "Key") != null)
                .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Attribute("Color")!.Value.ToUpperInvariant());

            var palette = ThemeService.Palette.ToDictionary(p => p.Key, p => p.Light.ToUpperInvariant());
            Assert.Equal(xamlBrushes.Keys.Order(), palette.Keys.Order());
            foreach (var (key, color) in xamlBrushes)
                Assert.True(palette[key] == color, $"{key}: App.xaml 是 {color}，Palette 是 {palette[key]}");
        }

        [Theory]
        [InlineData("App.xaml")]
        [InlineData("MainWindow.xaml")]
        [InlineData("InputDialog.xaml")]
        public void Xaml_UsesOnlyDynamicPaletteBrushes(string file)
        {
            var xaml = ReadXaml(file);
            var known = ThemeService.Palette.Select(p => p.Key).Append("AccentGradient").ToHashSet();

            // 顏色一律用 DynamicResource，否則切換主題時不會更新
            Assert.DoesNotMatch(@"StaticResource \w*(Brush|Gradient)\}", xaml);
            foreach (Match m in Regex.Matches(xaml, @"DynamicResource (\w*(?:Brush|Gradient))\}"))
                Assert.True(known.Contains(m.Groups[1].Value), $"{file} 用到 {m.Groups[1].Value}，但 Palette 沒有深色版本");
        }

        [Fact]
        public void MainWindowXaml_HasNoHardcodedColorsExceptKnownOnes()
        {
            // 允許的固定顏色：卡片陰影、標題圖示陰影、狀態列綠點（深淺色都適用）
            var allowed = new[] { "#1E1B4B", "#4F46E5", "#10B981" };
            var found = Regex.Matches(ReadXaml("MainWindow.xaml"), "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant());
            Assert.All(found, c => Assert.Contains(c, allowed));
        }

        // ---------- 可讀性：文字與背景的對比（WCAG） ----------

        [Theory]
        [InlineData("TextBrush", "CardBrush", 7.0)]
        [InlineData("TextBrush", "AppBgBrush", 7.0)]
        [InlineData("TextBrush", "InputBgBrush", 7.0)]
        [InlineData("MutedBrush", "CardBrush", 4.5)]
        [InlineData("DangerBrush", "DangerSoftBrush", 4.5)]
        [InlineData("AccentBrush", "CardBrush", 3.0)]
        public void Palette_TextIsReadableInBothThemes(string fg, string bg, double minRatio)
        {
            var p = ThemeService.Palette.ToDictionary(e => e.Key);
            foreach (var dark in new[] { false, true })
            {
                var f = ThemeService.ParseColor(dark ? p[fg].Dark : p[fg].Light);
                var b = ThemeService.ParseColor(dark ? p[bg].Dark : p[bg].Light);
                var ratio = Contrast(f, b);
                Assert.True(ratio >= minRatio, $"{(dark ? "深色" : "淺色")} {fg} / {bg} 對比 {ratio:F2} < {minRatio}");
            }
        }

        private static double Contrast(Color a, Color b)
        {
            static double Lum(Color c)
            {
                static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
                return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
            }
            var (l1, l2) = (Lum(a), Lum(b));
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }
    }
}
