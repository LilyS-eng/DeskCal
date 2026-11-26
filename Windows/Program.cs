using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
                var app = new Application();
                app.Run(new Widget());
            }
        }
    }

    internal sealed class Widget : Window
    {
        private const string Host = "deskcal.invalid";
        private const string Page = "https://" + Host + "/index.html?widget=1&host=windows&build=1.0.3";
        private readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCal");
        private readonly WebView2 web = new WebView2();
        private readonly Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        private Forms.NotifyIcon tray;
        private bool closing;
        private bool firstAppearance = true;
        private WindowAppearance appearance;
        private const int GripHeight = 12;

        public Widget()
        {
            Title = "DeskCal 1.0.3";
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
            handle.MouseLeftButtonDown += (_, e) => {
                if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            var layout = new DockPanel();
            DockPanel.SetDock(handle, Dock.Top);
            layout.Children.Add(handle);
            layout.Children.Add(web);
            Content = layout;

            SourceInitialized += (_, __) => appearance = new WindowAppearance(this);
            Loaded += Start;
            Closed += (_, __) =>
            {
                closing = true;
                tray?.Dispose();
                menu.Dispose();
                web.Dispose();
            };
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
                    if (e.IsSuccess && IsActive) web.Focus();
                    if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                        MessageBox.Show("日历载入失败。请从系统托盘刷新组件。", Title);
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
                        MessageBox.Show(status + (appearance.AcrylicActive ? "\n已启用系统 Acrylic。" : "\n使用兼容底色。"), "DeskCal 1.0.3 · 输入诊断");
                    }
                    catch (Exception e) { if (!closing) MessageBox.Show(e.Message, Title); }
                });
                menu.Items.Add("显示窗口", null, (_, __) => Recover());
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add("退出", null, (_, __) => Close());
                tray = new Forms.NotifyIcon {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath),
                    Text = "DeskCal 1.0.3", ContextMenuStrip = menu, Visible = true
                };
                tray.DoubleClick += (_, __) => Recover();
                core.Navigate(Page);
            }
            catch (Exception e)
            {
                if (closing) return;
                string detail = e is WebView2RuntimeNotFoundException
                    ? "请安装 Microsoft Edge WebView2 Evergreen 运行时，再打开 DeskCal。"
                    : e.Message;
                MessageBox.Show("DeskCal 无法启动。\n" + detail, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void Recover()
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
            web.Focus();
        }

        private void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (e.Source != Page) return;
            string message;
            try { message = e.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; }
            var p = (message ?? "").Split(' ');
            if (p.Length != 5 || p[0] != "appearance" || !int.TryParse(p[1], out int width) ||
                !int.TryParse(p[2], out int percent) || !int.TryParse(p[3], out int blur)) return;
            if (width < 380 || width > 860 || percent < 70 || percent > 160 || blur < 0 || blur > 48 || (p[4] != "light" && p[4] != "dark")) return;
            double zoom = percent / 100.0;
            Width = Math.Min(SystemParameters.WorkArea.Width, width * zoom);
            Height = Math.Min(SystemParameters.WorkArea.Height, 520 * zoom + GripHeight);
            web.ZoomFactor = zoom;
            appearance.Apply(blur > 0, p[4] == "dark");
            if (firstAppearance)
            {
                var work = SystemParameters.WorkArea;
                Left = Math.Max(work.Left, work.Right - Width - 32);
                Top = Math.Max(work.Top, work.Bottom - Height - 32);
                firstAppearance = false;
            }
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
