using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskCal
{
    // This temporary layered surface owns no focus or mouse input. The
    // calendar and its WebView2 HWND remain ordinary, interactive windows.
    internal sealed class ResizeOutline : IDisposable
    {
        private readonly HwndSource source;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
            int width, int height, uint flags);

        public ResizeOutline()
        {
            var parameters = new HwndSourceParameters("DeskCal Resize Preview") {
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP, initially hidden
                ExtendedWindowStyle = 0x080000A0, // NOACTIVATE | TOOLWINDOW | TRANSPARENT
                UsesPerPixelOpacity = true,
                Width = 1,
                Height = 1
            };
            source = new HwndSource(parameters);
            source.RootVisual = new Border {
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(215, 162, 212, 255)),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false
            };
        }

        public void Show(int x, int y, int width, int height)
        {
            SetWindowPos(source.Handle, new IntPtr(-1), x, y, width, height, 0x50); // TOPMOST | SHOWWINDOW | NOACTIVATE
        }

        public void Dispose() => source.Dispose();
    }
}
