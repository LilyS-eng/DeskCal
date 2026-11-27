using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCal
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            using (var mutex = new Mutex(true, @"Local\DeskCal.Windows.Widget", out bool first))
            {
                if (!first)
                {
                    MessageBox.Show("DeskCal 已经在运行。请先从系统托盘退出正在运行的版本。", "DeskCal");
                    return;
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.Run(new Widget());
            }
        }
    }

    internal sealed class Widget : Window
    {
        private readonly Grid surface = new Grid { Background = Brushes.Transparent, Focusable = true };
        private readonly Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        private Forms.NotifyIcon tray;
        private bool closing;
        private DispatcherOperation pendingReveal;
        private bool awaitingFrame;
        private bool needsReveal = true;
        private bool focusOnReveal;
        private bool surfaceReady;
        private readonly WidgetPlacement placement;
        private WindowAppearance appearance;
        private readonly AcrylicTrace trace;
        private const int GripHeight = 30;
        private const int CloseWidth = 34;
        private const int CloseHeight = 26;

        public Widget()
        {
            Title = "DeskCal Acrylic B (WPF only)";
            Width = 620;
            Height = 520 + GripHeight;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - Width - 32);
            Top = Math.Max(work.Top, work.Bottom - Height - 32);
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            AllowsTransparency = false;

            var handle = new Border {
                Height = GripHeight, Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll, ToolTip = "拖动窗口",
                Child = new Border { Width = 32, Height = 3, CornerRadius = new CornerRadius(2),
                    Background = Brushes.LightSlateGray, VerticalAlignment = VerticalAlignment.Center }
            };
            var close = new Button {
                Content = "×", Width = CloseWidth, Height = CloseHeight, FontSize = 20,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Foreground = Brushes.WhiteSmoke, Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "隐藏到托盘"
            };
            close.Template = CloseButtonTemplate();
            System.Windows.Automation.AutomationProperties.SetName(close, "隐藏到托盘");
            close.Click += (_, __) => Close();
            var header = new Grid { Height = GripHeight };
            header.Children.Add(handle);
            header.Children.Add(close);
            handle.MouseLeftButtonDown += (_, e) => {
                if (e.ButtonState == MouseButtonState.Pressed) {
                    DragMove();
                    if (placement.AfterDrag()) HideToEdge();
                }
            };
            var layout = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(surface);
            Content = layout;

            trace = new AcrylicTrace(this, surface);
            SourceInitialized += (_, __) => appearance = new WindowAppearance(this, trace);
            placement = new WidgetPlacement(this, header, CloseButtonTemplate(), HideToEdge, () => { trace.Record("edge restore requested"); Recover(); });
            AddResizeGrip();
            Loaded += Start;
            Closing += (_, e) => {
                if (closing || tray == null) return;
                e.Cancel = true;
                HideToTray();
            };
            Application.Current.SessionEnding += EndSession;
            Closed += (_, __) =>
            {
                closing = true;
                CancelReveal();
                placement.Dispose();
                Application.Current.SessionEnding -= EndSession;
                tray?.Dispose();
                menu.Dispose();
                trace.Dispose();
            };
        }

        private static ControlTemplate CloseButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "CloseSurface";
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), border.Name));
            template.Triggers.Add(hover);
            return template;
        }

        private void AddResizeGrip()
        {
            var grip = new Border {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.SizeNWSE, Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = "◢", FontSize = 18, Foreground = Brushes.LightSlateGray,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            surface.Children.Add(grip);
            Point start = default;
            Size initial = default;
            Vector scale = default;
            grip.MouseLeftButtonDown += (_, e) => {
                start = PointToScreen(e.GetPosition(this));
                initial = RenderSize;
                var dpi = VisualTreeHelper.GetDpi(this);
                scale = new Vector(dpi.DpiScaleX, dpi.DpiScaleY);
                // Capture can synchronously dispatch MouseMove. Set the origin first.
                if (!grip.CaptureMouse()) return;
                trace.Record("WPF grip down");
                e.Handled = true;
            };
            grip.MouseMove += (_, e) => {
                if (!grip.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
                var delta = PointToScreen(e.GetPosition(this)) - start;
                placement.Resize((int)Math.Round(initial.Width + delta.X / scale.X),
                    (int)Math.Round(initial.Height + delta.Y / scale.Y));
            };
            grip.MouseLeftButtonUp += (_, e) => {
                if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
            };
            grip.LostMouseCapture += (_, __) => trace.Record("WPF grip lostcapture");
        }

        private void Start(object sender, RoutedEventArgs args)
        {
            Loaded -= Start;
            var show = new Forms.ToolStripMenuItem("显示 DeskCal", null, (_, __) => { trace.Record("tray menu show"); Recover(); });
            var hide = new Forms.ToolStripMenuItem("隐藏 DeskCal", null, (_, __) => HideToTray());
            menu.Items.Insert(0, show);
            menu.Items.Insert(1, hide);
            menu.Items.Insert(2, new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripSeparator());
            trace.Menu(menu);
            menu.Items.Add("退出 DeskCal", null, (_, __) => Exit());
            menu.Opening += (_, __) => {
                show.Enabled = !placement.MainShown;
                hide.Enabled = !placement.TrayHidden;
            };
            tray = new Forms.NotifyIcon {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath),
                Text = "DeskCal Acrylic B (WPF only)", ContextMenuStrip = menu, Visible = true
            };
            tray.MouseClick += (_, e) => {
                if (e.Button == Forms.MouseButtons.Left) ToggleVisibility();
            };
            // Match the initial geometry recorded in the paired x64 WebView2 run.
            placement.Resize(916, 745, initial: true);
            surfaceReady = true;
            trace.Record("pure WPF surface ready");
            QueueReveal();
        }

        private void EndSession(object sender, SessionEndingCancelEventArgs e)
        {
            // WPF's Application session-ending handler performs the shutdown.
            closing = true;
        }

        private void Exit()
        {
            closing = true;
            Close();
        }

        private void ToggleVisibility()
        {
            trace.Record("tray toggle");
            if (placement.MainShown) HideToTray();
            else Recover();
        }

        private void HideToTray()
        {
            trace.Record("tray hide begin");
            placement.HideToTray();
            ConcealMain();
        }

        private void HideToEdge()
        {
            trace.Record("edge hide begin");
            ConcealMain();
            placement.HideToEdge();
            trace.Record("edge hide end");
        }

        private void ConcealMain()
        {
            CancelReveal();
            needsReveal = true;
            appearance.Conceal();
            trace.Record("before Hide");
            Hide();
            trace.Record("after Hide");
        }

        private void QueueReveal()
        {
            if (!needsReveal || !surfaceReady || !IsVisible || awaitingFrame || closing) return;
            awaitingFrame = true;
            CompositionTarget.Rendering += RevealAfterFrame;
            InvalidateVisual();
        }

        private void CancelReveal()
        {
            CompositionTarget.Rendering -= RevealAfterFrame;
            pendingReveal?.Abort();
            pendingReveal = null;
            awaitingFrame = false;
        }

        private void RevealAfterFrame(object sender, EventArgs args)
        {
            CompositionTarget.Rendering -= RevealAfterFrame;
            trace.Record("CompositionTarget.Rendering reveal");
            pendingReveal = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                pendingReveal = null;
                awaitingFrame = false;
                if (!IsVisible || closing) return;
                trace.Record("ContextIdle before Present");
                appearance.Present();
                needsReveal = false;
                if (focusOnReveal)
                {
                    focusOnReveal = false;
                    Activate();
                    surface.Focus();
                }
            }));
        }

        private void Recover()
        {
            trace.Record("recover before PrepareShow");
            placement.PrepareShow();
            WindowState = WindowState.Normal;
            if (!IsVisible)
            {
                focusOnReveal = true;
                trace.Record("before Show");
                Show();
                trace.Record("after Show");
                QueueReveal();
                return;
            }
            Activate();
            surface.Focus();
        }

    }
}
