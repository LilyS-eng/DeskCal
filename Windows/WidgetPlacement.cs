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
        private ResizeOutline outline;
        private const int SnapDistance = 24;
        private const int TabWidth = 16;
        private const int TabHeight = 96;
        private const uint PreserveStackAndFocus = 0x14; // NOZORDER | NOACTIVATE
        private IntPtr MainHandle => new WindowInteropHelper(main).Handle;

        public bool MainShown => state == DisplayState.Normal || state == DisplayState.EdgeDockedVisible;
        public bool TrayHidden => state == DisplayState.TrayHidden;

        [StructLayout(LayoutKind.Sequential)]
        private struct Bounds { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Point { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

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

        public bool Drag()
        {
            var start = Forms.Cursor.Position;
            if (!GetWindowRect(MainHandle, out Bounds before)) return false;
            main.DragMove();
            var cursor = Forms.Cursor.Position;
            if (!GetWindowRect(MainHandle, out Bounds after) ||
                (start == cursor && before.Left == after.Left && before.Top == after.Top)) return false;
            // DragMove also returns on cancellation; a restored rectangle is
            // not a completed move.
            if (before.Left == after.Left && before.Top == after.Top) return false;
            var screen = Forms.Screen.FromPoint(cursor);
            var work = screen.WorkingArea;
            var monitor = MonitorFromPoint(new Point { X = cursor.X, Y = cursor.Y }, 2);
            var dpi = GetDpiForMonitor(monitor, 0, out uint xDpi, out uint yDpi) == 0
                ? new DpiScale(xDpi / 96.0, yDpi / 96.0) : VisualTreeHelper.GetDpi(main);
            int threshold = Pixels(SnapDistance, dpi.DpiScaleX);
            int leftDistance = Math.Abs(cursor.X - work.Left);
            int rightDistance = Math.Abs(cursor.X - (work.Right - 1));
            edge = Math.Min(leftDistance, rightDistance) > threshold ? (Edge?)null :
                leftDistance <= rightDistance ? Edge.Left : Edge.Right;
            state = edge.HasValue ? DisplayState.EdgeDockedVisible : DisplayState.Normal;
            UpdateCollapseButton();
            if (edge.HasValue)
                ApplyBounds(Fit((int)Math.Round(main.Width), (int)Math.Round(main.Height), work, dpi, after, false));
            return edge.HasValue;
        }

        public void PrepareBackdropSurface(Action arrangeBrowser)
        {
            if (!GetWindowRect(MainHandle, out Bounds final) || final.Bottom - final.Top < 180) return;
            // Use a provisional viewport only while DWM cloaks the window.
            // Commit the final client size through WM_SIZE, WPF layout, and
            // the hosted HWND rather than invalidating just the frame.
            int stageHeight = (final.Bottom - final.Top) * 2 / 3;
            if (!SetWindowPos(MainHandle, IntPtr.Zero, final.Left, final.Top,
                final.Right - final.Left, stageHeight, PreserveStackAndFocus)) return;
            try
            {
                arrangeBrowser();
                WindowAppearance.WaitForComposition();
            }
            finally
            {
                ApplyBounds(final);
                arrangeBrowser();
            }
        }

        private Bounds Fit(int width, int height, System.Drawing.Rectangle work,
            DpiScale dpi, Bounds current, bool initial)
        {
            int maxWidth = Math.Max(1, (int)Math.Floor(work.Width / dpi.DpiScaleX));
            int maxHeight = Math.Max(1, (int)Math.Floor(work.Height / dpi.DpiScaleY));
            int w = Pixels(Clamp(width, Math.Min(380, maxWidth), maxWidth), dpi.DpiScaleX);
            int h = Pixels(Clamp(height, Math.Min(300, maxHeight), maxHeight), dpi.DpiScaleY);
            int right = Math.Max(work.Left, work.Right - w);
            int bottom = Math.Max(work.Top, work.Bottom - h);
            int x = edge == Edge.Left ? work.Left : edge == Edge.Right ? right :
                Clamp(initial ? right - Pixels(32, dpi.DpiScaleX) : current.Left, work.Left, right);
            int y = Clamp(initial ? bottom - Pixels(32, dpi.DpiScaleY) : current.Top, work.Top, bottom);
            return new Bounds { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }

        private bool TryFit(int width, int height, bool initial, out Bounds bounds)
        {
            bounds = default;
            if (!GetWindowRect(MainHandle, out Bounds current)) return false;
            bounds = Fit(width, height, Forms.Screen.FromHandle(MainHandle).WorkingArea,
                VisualTreeHelper.GetDpi(main), current, initial);
            return true;
        }

        private void ApplyBounds(Bounds target)
        {
            if (!GetWindowRect(MainHandle, out Bounds current) ||
                (target.Left == current.Left && target.Top == current.Top &&
                 target.Right == current.Right && target.Bottom == current.Bottom)) return;
            SetWindowPos(MainHandle, IntPtr.Zero, target.Left, target.Top,
                target.Right - target.Left, target.Bottom - target.Top, PreserveStackAndFocus);
        }

        public void Resize(int width, int height, bool initial = false)
        {
            if (TryFit(width, height, initial, out Bounds bounds)) ApplyBounds(bounds);
        }

        public void Preview(int width, int height)
        {
            if (!TryFit(width, height, false, out Bounds bounds)) return;
            if (outline == null) outline = new ResizeOutline();
            outline.Show(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        }

        public void CancelPreview()
        {
            outline?.Dispose();
            outline = null;
        }

        public void PrepareShow()
        {
            tab?.Hide();
            state = edge.HasValue ? DisplayState.EdgeDockedVisible : DisplayState.Normal;
            UpdateCollapseButton();
        }

        public void HideToTray()
        {
            CancelPreview();
            tab?.Hide();
            state = DisplayState.TrayHidden;
        }

        public void HideToEdge()
        {
            CancelPreview();
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

        public void Dispose()
        {
            CancelPreview();
            tab?.Close();
        }

        private static int Pixels(double value, double dpi) => (int)Math.Round(value * dpi);
        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }
}
