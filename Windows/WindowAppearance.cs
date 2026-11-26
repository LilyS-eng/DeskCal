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
        private const int DesktopAcrylic = 3, NoBackdrop = 1;
        private const int Cloak = 13, DarkMode = 20, CornerPreference = 33, SystemBackdrop = 38;
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

        public WindowAppearance(Window window)
        {
            this.window = window;
            source = (HwndSource)PresentationSource.FromVisual(window);
            IntPtr hwnd = source.Handle;
            // Limit the change to shell switcher visibility on the top-level HWND.
            SetWindowLong(hwnd, ExtendedStyle, GetWindowLong(hwnd, ExtendedStyle) | ToolWindow);
            int rounded = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, CornerPreference, ref rounded, sizeof(int));
            Conceal();
            Apply(true, false);
        }

        public void Conceal()
        {
            int value = 1;
            DwmSetWindowAttribute(source.Handle, Cloak, ref value, sizeof(int));
        }

        public void Present()
        {
            // WPF has submitted the staged layout. The HWND remains composed
            // while cloaked, so DWM can process it before making it visible.
            DwmFlush();
            int value = 0;
            DwmSetWindowAttribute(source.Handle, Cloak, ref value, sizeof(int));
        }

        public void Apply(bool enableGlass, bool useDarkText)
        {
            if (glass == enableGlass && darkInk == useDarkText) return;
            glass = enableGlass;
            darkInk = useDarkText;
            int dark = useDarkText ? 0 : 1;
            DwmSetWindowAttribute(source.Handle, DarkMode, ref dark, sizeof(int));
            if (enableGlass)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                window.Background = Brushes.Transparent;
            }
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int material = DesktopAcrylic;
            AcrylicActive = enableGlass && DwmExtendFrameIntoClientArea(source.Handle, ref margins) == 0
                && DwmSetWindowAttribute(source.Handle, SystemBackdrop, ref material, sizeof(int)) == 0;
            if (!AcrylicActive)
            {
                material = NoBackdrop;
                DwmSetWindowAttribute(source.Handle, SystemBackdrop, ref material, sizeof(int));
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
