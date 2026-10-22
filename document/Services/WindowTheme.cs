using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace document.Services
{
    /// <summary>Windows 11：標題列底色與視窗背景同色，深色主題時標題文字與按鈕改成淺色。</summary>
    public static class WindowTheme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;

        /// <param name="backgroundKey">視窗背景用的顏色資源，標題列會套同一色。</param>
        public static void ApplyTitleBar(Window window, string backgroundKey = "AppBgBrush")
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return; // 視窗還沒建立，OnSourceInitialized 時會再套用
            int dark = ThemeService.IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var bg = ((SolidColorBrush)window.FindResource(backgroundKey)).Color;
            int colorRef = bg.R | (bg.G << 8) | (bg.B << 16); // COLORREF = 0x00BBGGRR
            // Windows 10 不支援這些屬性，呼叫會回傳錯誤碼，直接忽略即可
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
        }
    }
}
