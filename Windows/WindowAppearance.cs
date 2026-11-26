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
            Apply(glass.GetValueOrDefault(true), darkInk.GetValueOrDefault(), force: true);
            // SWP_FRAMECHANGED | NOACTIVATE | NOZORDER | NOMOVE | NOSIZE
            SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37);
            // RDW_INVALIDATE | RDW_FRAME | RDW_ALLCHILDREN
            RedrawWindow(source.Handle, IntPtr.Zero, IntPtr.Zero, 0x481);
        }

        public void Apply(bool enableGlass, bool useDarkText, bool force = false)
        {
            if (!force && glass == enableGlass && darkInk == useDarkText) return;
            glass = enableGlass;
            darkInk = useDarkText;
            int dark = useDarkText ? 0 : 1;
            DwmSetWindowAttribute(source.Handle, DarkMode, ref dark, sizeof(int));
            int material = enableGlass ? 3 : 1; // DWMSBT_TRANSIENTWINDOW / DWMSBT_NONE
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
            source.CompositionTarget.BackgroundColor = color;
            window.Background = new SolidColorBrush(color);
        }
    }
}
