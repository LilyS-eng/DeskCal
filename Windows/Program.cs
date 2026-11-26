using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
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
        private const string Host = "deskcal.invalid";
        private const string Page = "https://" + Host + "/index.html?widget=1&host=windows&build=1.0.6";
        private readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal");
        private readonly WebView2 web = new WebView2();
        private readonly Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        private Forms.NotifyIcon tray;
        private bool closing;
        private DispatcherOperation pendingReveal;
        private bool awaitingFrame;
        private bool needsReveal = true;
        private bool focusOnReveal;
        private bool pageReady;
        private readonly WidgetPlacement placement;
        private ReminderScheduler reminders;
        private bool firstAppearance = true;
        private WindowAppearance appearance;
        private const int GripHeight = 30;
        private const int CloseWidth = 34;
        private const int CloseHeight = 26;

        public Widget()
        {
            Title = "DeskCal 1.0.6";
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

            web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0, 0, 0, 0);
            web.AllowExternalDrop = false;

            // A separate layout row is the only native drag target. It shares
            // no pixels or mouse handlers with the windowed browser control.
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
            layout.Children.Add(web);
            Content = layout;

            SourceInitialized += (_, __) => appearance = new WindowAppearance(this);
            placement = new WidgetPlacement(this, header, CloseButtonTemplate(), HideToEdge, Recover);
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
                reminders?.Dispose();
                tray?.Dispose();
                menu.Dispose();
                web.Dispose();
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

        private async void Start(object sender, RoutedEventArgs args)
        {
            Loaded -= Start;
            try
            {
                Directory.CreateDirectory(dataDir);
                string assets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
                if (!File.Exists(Path.Combine(assets, "index.html")))
                    throw new FileNotFoundException("缺少 web\\index.html，请完整解压程序包。");
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2"));
                if (closing) return;
                await web.EnsureCoreWebView2Async(env);
                if (closing) return;
                var core = web.CoreWebView2;
                reminders = new ReminderScheduler(Dispatcher, env.UserDataFolder);
                core.SetVirtualHostNameToFolderMapping(Host, assets, CoreWebView2HostResourceAccessKind.Deny);
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                // A download can begin with a blob navigation on some runtime versions.
                core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Page &&
                    !e.Uri.StartsWith("blob:https://" + Host + "/", StringComparison.Ordinal);
                core.FrameNavigationStarting += (_, e) => e.Cancel = true;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += Receive;
                core.DownloadStarting += SaveDownload;
                core.NavigationCompleted += (_, e) => {
                    if (e.IsSuccess)
                    {
                        pageReady = true;
                        QueueReveal();
                        if (IsActive) web.Focus();
                    }
                    if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        pageReady = true;
                        firstAppearance = false;
                        QueueReveal();
                        MessageBox.Show("日历载入失败。请从系统托盘刷新组件。", Title);
                    }
                };
                menu.Items.Add("刷新组件", null, (_, __) => core.Reload());
                menu.Items.Add("查看输入诊断", null, async (_, __) => {
                    try
                    {
                        var counts = await core.ExecuteScriptAsync("window.deskcalInputStatus?.() ?? null");
                        var parts = counts.Trim('[', ']').Split(',');
                        string status = parts.Length == 2
                            ? $"网页已就绪。自上次加载起，按下 {parts[0]} 次，点击 {parts[1]} 次。"
                            : "网页的输入诊断尚未就绪。";
                        MessageBox.Show(status + (appearance.AcrylicActive ? "\n已启用系统 Acrylic。" : "\n使用兼容底色。"), "DeskCal 1.0.6 · 输入诊断");
                    }
                    catch (Exception e) { if (!closing) MessageBox.Show(e.Message, Title); }
                });
                var show = new Forms.ToolStripMenuItem("显示 DeskCal", null, (_, __) => Recover());
                var hide = new Forms.ToolStripMenuItem("隐藏 DeskCal", null, (_, __) => HideToTray());
                menu.Items.Insert(0, show);
                menu.Items.Insert(1, hide);
                menu.Items.Insert(2, new Forms.ToolStripSeparator());
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("退出 DeskCal", null, (_, __) => Exit());
                menu.Opening += (_, __) => {
                    show.Enabled = !placement.MainShown;
                    hide.Enabled = !placement.TrayHidden;
                };
                tray = new Forms.NotifyIcon {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath),
                    Text = "DeskCal 1.0.6", ContextMenuStrip = menu, Visible = true
                };
                tray.MouseClick += (_, e) => {
                    if (e.Button == Forms.MouseButtons.Left) ToggleVisibility();
                };
                core.Navigate(Page);
            }
            catch (Exception e)
            {
                if (closing) return;
                string detail = e is WebView2RuntimeNotFoundException
                    ? "请安装 Microsoft Edge WebView2 Evergreen 运行时，再打开 DeskCal。"
                    : e.Message;
                MessageBox.Show("DeskCal 无法启动。\n" + detail, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                Exit();
            }
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
            if (placement.MainShown) HideToTray();
            else Recover();
        }

        private void HideToTray()
        {
            placement.HideToTray();
            ConcealMain();
        }

        private void HideToEdge()
        {
            ConcealMain();
            placement.HideToEdge();
        }

        private void ConcealMain()
        {
            CancelReveal();
            needsReveal = true;
            appearance.Conceal();
            Hide();
        }

        private void QueueReveal()
        {
            if (!needsReveal || !pageReady || firstAppearance || !IsVisible || awaitingFrame || closing) return;
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
            pendingReveal = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                pendingReveal = null;
                awaitingFrame = false;
                if (!IsVisible || closing) return;
                appearance.Present();
                needsReveal = false;
                if (focusOnReveal)
                {
                    focusOnReveal = false;
                    Activate();
                    web.Focus();
                }
            }));
        }

        private void Recover()
        {
            placement.PrepareShow();
            WindowState = WindowState.Normal;
            if (!IsVisible)
            {
                focusOnReveal = true;
                Show();
                QueueReveal();
                return;
            }
            Activate();
            web.Focus();
        }

        private void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.Source != Page) return;
            string message;
            try { message = e.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; }
            const string schedule = "reminders ";
            if (message != null && message.StartsWith(schedule, StringComparison.Ordinal))
            {
                reminders.Update(message.Substring(schedule.Length));
                return;
            }
            var p = (message ?? "").Split(' ');
            if (p.Length == 4 && p[0] == "size" && int.TryParse(p[1], out int requestedWidth) &&
                int.TryParse(p[2], out int requestedHeight) && (p[3] == "preview" || p[3] == "save"))
            {
                placement.Resize(requestedWidth, requestedHeight);
                if (p[3] == "save") SendSize("saved");
                return;
            }

            if (p.Length != 6 || p[0] != "appearance" || !int.TryParse(p[1], out int width) ||
                !int.TryParse(p[2], out int height) || !int.TryParse(p[3], out int percent) ||
                !int.TryParse(p[4], out int blur) || percent < 70 || percent > 160 ||
                blur < 0 || blur > 48 || (p[5] != "light" && p[5] != "dark")) return;
            double zoom = percent / 100.0;
            if (web.ZoomFactor != zoom) web.ZoomFactor = zoom;
            appearance.Apply(blur > 0, p[5] == "dark");
            if (firstAppearance)
            {
                firstAppearance = false;
                placement.Resize(width, height, initial: true);
                SendSize("current");
                QueueReveal();
            }
        }

        private void SendSize(string kind)
        {
            web.CoreWebView2.PostWebMessageAsString($"size {kind} {Math.Round(Width):0} {Math.Round(Height):0}");
        }

        private void SaveDownload(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            var pending = e.GetDeferral();
            e.Handled = true;
            // Let WebView2 return from its callback before opening a modal dialog.
            Dispatcher.BeginInvoke(new Action(() => {
                try
                {
                    var file = Path.GetFileName(e.ResultFilePath);
                    var dialog = new SaveFileDialog { FileName = file, Filter = "导出文件 (*" +
                        Path.GetExtension(file) + ")|*" + Path.GetExtension(file), OverwritePrompt = true };
                    if (!closing && dialog.ShowDialog(this) == true) e.ResultFilePath = dialog.FileName;
                    else e.Cancel = true;
                }
                finally { pending.Complete(); }
            }));
        }
    }
}
