using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskCal
{
    // All DWM calls affect drawing only. The WebView2 child owns its native input surface.
    internal sealed class WindowAppearance
    {
        private const int ExtendedStyle = -20, ToolWindow = 0x80;
        private const uint RefreshFrame = 0x37; // FRAMECHANGED | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
        private const uint PaintFrameAndChildrenNow = 0x581; // INVALIDATE | ALLCHILDREN | UPDATENOW | FRAME
        private const int DesktopAcrylic = 3, NoBackdrop = 1;
        private const int DarkMode = 20, CornerPreference = 33, SystemBackdrop = 38;
        private readonly Window window;
        private readonly HwndSource source;
        private bool? glass;
        private bool? darkInk;
        public bool AcrylicActive { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        public WindowAppearance(Window window)
        {
            this.window = window;
            source = (HwndSource)PresentationSource.FromVisual(window);
            IntPtr hwnd = source.Handle;
            // Limit the change to shell switcher visibility on the top-level HWND.
            SetWindowLong(hwnd, ExtendedStyle, GetWindowLong(hwnd, ExtendedStyle) | ToolWindow);
            int rounded = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, CornerPreference, ref rounded, sizeof(int));
            Apply(true, false);
        }

        public void Refresh()
        {
            if (!glass.GetValueOrDefault(true)) return;
            // Let DWM consume the newly visible surface first. Rebuild a backdrop
            // that may still report Acrylic while drawing the previous surface.
            DwmFlush();
            var hwnd = source.Handle;
            var noMargins = new Margins();
            int noBackdrop = NoBackdrop;
            DwmSetWindowAttribute(hwnd, SystemBackdrop, ref noBackdrop, sizeof(int));
            DwmExtendFrameIntoClientArea(hwnd, ref noMargins);
            Apply(true, darkInk.GetValueOrDefault(), force: true);

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, RefreshFrame);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, PaintFrameAndChildrenNow);
        }

        public void Apply(bool enableGlass, bool useDarkText, bool force = false)
        {
            if (!force && glass == enableGlass && darkInk == useDarkText) return;
            glass = enableGlass;
            darkInk = useDarkText;
            int dark = useDarkText ? 0 : 1;
            DwmSetWindowAttribute(source.Handle, DarkMode, ref dark, sizeof(int));
            int material = enableGlass ? DesktopAcrylic : NoBackdrop;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            AcrylicActive = DwmSetWindowAttribute(source.Handle, SystemBackdrop, ref material, sizeof(int)) == 0
                && enableGlass && DwmExtendFrameIntoClientArea(source.Handle, ref margins) == 0;
            if (!AcrylicActive)
            {
                margins = new Margins();
                DwmExtendFrameIntoClientArea(source.Handle, ref margins);
            }

            var color = AcrylicActive ? Colors.Transparent :
                (useDarkText ? Color.FromRgb(200, 210, 217) : Color.FromRgb(56, 69, 86));
            if (source.CompositionTarget.BackgroundColor != color)
                source.CompositionTarget.BackgroundColor = color;
            if (!(window.Background is SolidColorBrush brush) || brush.Color != color)
                window.Background = new SolidColorBrush(color);
        }
    }
}
