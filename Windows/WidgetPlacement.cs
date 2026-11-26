using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DeskCal
{
    internal sealed class WidgetPlacement : IDisposable
    {
        private enum DisplayState { Normal, EdgeDockedVisible, EdgeDockedHidden, TrayHidden }
        private enum Edge { Left, Right }

        private readonly Window main;
        private readonly Button collapse;
        private readonly Action expand;
        private DisplayState state = DisplayState.Normal;
        private Edge? edge;
        private Window tab;
        private const int SnapDistance = 24;
        private const int TabWidth = 16;
        private const int TabHeight = 96;
        private const uint PreserveStackAndFocus = 0x14; // NOZORDER | NOACTIVATE
        private IntPtr MainHandle => new WindowInteropHelper(main).Handle;

        public bool MainShown => state == DisplayState.Normal || state == DisplayState.EdgeDockedVisible;
        public bool TrayHidden => state == DisplayState.TrayHidden;

        [StructLayout(LayoutKind.Sequential)]
        private struct Bounds { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Bounds bounds);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
            int width, int height, uint flags);

        public WidgetPlacement(Window main, Grid header, ControlTemplate buttonTemplate,
            Action collapseWindow, Action expandWindow)
        {
            this.main = main;
            expand = expandWindow;
            collapse = new Button {
                Template = buttonTemplate, Content = "›", ToolTip = "收起到屏幕边缘",
                Foreground = Brushes.WhiteSmoke, FontSize = 20, Width = 32, Height = 26,
                Margin = new Thickness(0, 0, 40, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            System.Windows.Automation.AutomationProperties.SetName(collapse, "收起到屏幕边缘");
            collapse.Click += (_, __) => collapseWindow();
            header.Children.Add(collapse);
        }

        public bool AfterDrag()
        {
            if (!GetWindowRect(MainHandle, out Bounds bounds)) return false;
            var work = Forms.Screen.FromHandle(MainHandle).WorkingArea;
            int threshold = Pixels(SnapDistance, VisualTreeHelper.GetDpi(main).DpiScaleX);
            int leftDistance = Math.Abs(bounds.Left - work.Left);
            int rightDistance = Math.Abs(bounds.Right - work.Right);
            edge = Math.Min(leftDistance, rightDistance) > threshold ? (Edge?)null :
                leftDistance <= rightDistance ? Edge.Left : Edge.Right;
            state = edge.HasValue ? DisplayState.EdgeDockedVisible : DisplayState.Normal;
            UpdateCollapseButton();
            if (edge.HasValue)
                Resize((int)Math.Round(main.Width), (int)Math.Round(main.Height));
            return edge.HasValue;
        }

        public void Resize(int width, int height, bool initial = false)
        {
            if (MainHandle == IntPtr.Zero) return;
            var dpi = VisualTreeHelper.GetDpi(main);
            var work = Forms.Screen.FromHandle(MainHandle).WorkingArea;
            int maxWidth = Math.Max(1, (int)Math.Floor(work.Width / dpi.DpiScaleX));
            int maxHeight = Math.Max(1, (int)Math.Floor(work.Height / dpi.DpiScaleY));
            int w = Clamp(width, Math.Min(380, maxWidth), maxWidth);
            int h = Clamp(height, Math.Min(300, maxHeight), maxHeight);
            MoveIntoWorkArea(Pixels(w, dpi.DpiScaleX), Pixels(h, dpi.DpiScaleY), initial);
        }

        private void MoveIntoWorkArea(int width, int height, bool initial)
        {
            if (!GetWindowRect(MainHandle, out Bounds bounds)) return;
            var work = Forms.Screen.FromHandle(MainHandle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(main);
            int right = Math.Max(work.Left, work.Right - width);
            int bottom = Math.Max(work.Top, work.Bottom - height);
            int x = edge == Edge.Left ? work.Left : edge == Edge.Right ? right :
                Clamp(initial ? right - Pixels(32, dpi.DpiScaleX) : bounds.Left, work.Left, right);
            int y = Clamp(initial ? bottom - Pixels(32, dpi.DpiScaleY) : bounds.Top, work.Top, bottom);
            if (x != bounds.Left || y != bounds.Top || width != bounds.Right - bounds.Left ||
                height != bounds.Bottom - bounds.Top)
            {
                // A single position/size change produces one WM_SIZE for WPF to
                // arrange the browser child. It never changes the backdrop.
                SetWindowPos(MainHandle, IntPtr.Zero, x, y, width, height, PreserveStackAndFocus);
            }
        }

        public void PrepareShow()
        {
            tab?.Hide();
            state = edge.HasValue ? DisplayState.EdgeDockedVisible : DisplayState.Normal;
            UpdateCollapseButton();
        }

        public void HideToTray()
        {
            tab?.Hide();
            state = DisplayState.TrayHidden;
        }

        public void HideToEdge()
        {
            if (!edge.HasValue || !GetWindowRect(MainHandle, out Bounds bounds)) return;
            state = DisplayState.EdgeDockedHidden;
            if (tab == null) tab = CreateTab();
            var work = Forms.Screen.FromHandle(MainHandle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(main);
            int width = Pixels(TabWidth, dpi.DpiScaleX);
            int height = Math.Min(work.Height, Pixels(TabHeight, dpi.DpiScaleY));
            int x = edge == Edge.Left ? work.Left : work.Right - width;
            int y = Clamp(bounds.Top + (bounds.Bottom - bounds.Top - height) / 2,
                work.Top, work.Bottom - height);
            ((Button)tab.Content).Content = edge == Edge.Left ? "›" : "‹";
            var hwnd = new WindowInteropHelper(tab).EnsureHandle();
            // Use monitor pixel coordinates, including negative origins on secondary screens.
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, PreserveStackAndFocus);
            tab.Show();
        }

        private Window CreateTab()
        {
            var button = new Button {
                Background = new SolidColorBrush(Color.FromRgb(54, 65, 81)),
                Foreground = Brushes.WhiteSmoke, BorderThickness = new Thickness(0),
                FontSize = 17, Padding = new Thickness(0), ToolTip = "展开 DeskCal"
            };
            System.Windows.Automation.AutomationProperties.SetName(button, "展开 DeskCal");
            button.Click += (_, __) => expand();
            var window = new Window {
                Title = "DeskCal 边缘把手", Width = TabWidth, Height = TabHeight, Content = button,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.SourceInitialized += (_, __) =>
                WindowAppearance.MarkToolWindow(new WindowInteropHelper(window).Handle);
            return window;
        }

        private void UpdateCollapseButton()
        {
            collapse.Visibility = edge.HasValue ? Visibility.Visible : Visibility.Collapsed;
            collapse.Content = edge == Edge.Left ? "‹" : "›";
        }

        public void Dispose() => tab?.Close();

        private static int Pixels(double value, double dpi) => (int)Math.Round(value * dpi);
        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }
}
