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

        private readonly AcrylicTrace trace;

        public WindowAppearance(Window window, AcrylicTrace trace)
        {
            this.trace = trace;
            this.window = window;
            source = (HwndSource)PresentationSource.FromVisual(window);
            IntPtr hwnd = source.Handle;
            // Limit the change to shell switcher visibility on the top-level HWND.
            MarkToolWindow(hwnd);
            int rounded = 2; // DWMWCP_ROUND
            Set(CornerPreference, ref rounded);
            Conceal();
            Apply(true, false);
        }

        private int Set(int attr, ref int value)
        {
            int hr = DwmSetWindowAttribute(source.Handle, attr, ref value, sizeof(int));
            trace.Record($"DwmSet attr={attr} requested={value} hr={hr:X8}");
            return hr;
        }

        private int Extend(ref Margins margins)
        {
            int hr = DwmExtendFrameIntoClientArea(source.Handle, ref margins);
            trace.Record($"DwmExtend requested={margins.Left},{margins.Right},{margins.Top},{margins.Bottom} hr={hr:X8}");
            return hr;
        }

        public static void MarkToolWindow(IntPtr hwnd)
        {
            SetWindowLong(hwnd, ExtendedStyle, GetWindowLong(hwnd, ExtendedStyle) | ToolWindow);
        }

        public void Conceal()
        {
            int value = 1;
            Set(Cloak, ref value);
        }

        public void Present()
        {
            // WPF has submitted the staged layout. The HWND remains composed
            // while cloaked, so DWM can process it before making it visible.
            trace.Record("DwmFlush hr=" + DwmFlush().ToString("X8"));
            int value = 0;
            Set(Cloak, ref value);
        }

        public void Apply(bool enableGlass, bool useDarkText)
        {
            trace.Record($"Apply glass={enableGlass} darkText={useDarkText} cached={glass == enableGlass && darkInk == useDarkText}");
            if (glass == enableGlass && darkInk == useDarkText) return;
            glass = enableGlass;
            darkInk = useDarkText;
            int dark = useDarkText ? 0 : 1;
            Set(DarkMode, ref dark);
            if (enableGlass)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
                window.Background = Brushes.Transparent;
            }
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int material = DesktopAcrylic;
            AcrylicActive = enableGlass && Extend(ref margins) == 0
                && Set(SystemBackdrop, ref material) == 0;
            if (!AcrylicActive)
            {
                material = NoBackdrop;
                Set(SystemBackdrop, ref material);
                margins = new Margins();
                Extend(ref margins);
            }

            var color = AcrylicActive ? Colors.Transparent :
                (useDarkText ? Color.FromRgb(200, 210, 217) : Color.FromRgb(56, 69, 86));
            if (source.CompositionTarget.BackgroundColor != color)
                source.CompositionTarget.BackgroundColor = color;
            if (!(window.Background is SolidColorBrush brush) || brush.Color != color)
                window.Background = new SolidColorBrush(color);
            trace.Record("Apply complete accepted=" + AcrylicActive);
        }
    }
}
