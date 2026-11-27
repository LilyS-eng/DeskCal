using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

// Tests run the published entry point on an STA. Only DOM observations travel
// on stdin/stdout. All mouse, keyboard and focus actions come from Windows.
internal class InputObserver
{
    private static Window backing;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
        int width, int height, uint flags);

    private static void SetBackdropProbe(Window widget, string color)
    {
        if (color == "off")
        {
            if (backing != null) backing.Close();
            backing = null;
            return;
        }
        if (backing == null)
        {
            backing = new Window {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false,
                Left = widget.Left, Top = widget.Top, Width = widget.Width, Height = widget.Height
            };
            backing.Show();
            SetWindowPos(new WindowInteropHelper(backing).Handle, new WindowInteropHelper(widget).Handle,
                         0, 0, 0, 0, 0x13); // NOMOVE | NOSIZE | NOACTIVATE
        }
        backing.Background = color == "red" ? Brushes.Red : Brushes.Blue;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var reader = new Thread(() => ReadRequests(dispatcher)) { IsBackground = true };
        reader.Start();
        return AppDomain.CurrentDomain.ExecuteAssembly(Path.GetFullPath(args[0]), new string[0]);
    }

    private static void ReadRequests(Dispatcher dispatcher)
    {
        string input;
        while ((input = Console.ReadLine()) != null)
        {
            string expression = Encoding.UTF8.GetString(Convert.FromBase64String(input));
            try
            {
                Task<string> work = dispatcher.Invoke(new Func<Task<string>>(() => Inspect(expression)));
                string result = work.GetAwaiter().GetResult() ?? "null";
                Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(result)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                Console.WriteLine("ERR");
            }
        }
    }

    private static Task<string> Inspect(string expression)
    {
        var app = Application.Current;
        if (expression.StartsWith("backdrop:"))
        {
            SetBackdropProbe(app.MainWindow, expression.Substring("backdrop:".Length));
            return Task.FromResult("true");
        }
        if (expression.StartsWith("tray:"))
        {
            var widget = app.MainWindow;
            var tray = (Forms.NotifyIcon)widget.GetType().GetField("tray",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(widget);
            if (expression == "tray:left")
                typeof(Forms.NotifyIcon).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(tray, new object[] { new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0) });
            else if (expression == "tray:diagnostic-ab")
            {
                var diagnostic = tray.ContextMenuStrip.Items.OfType<Forms.ToolStripMenuItem>()
                    .Single(item => item.DropDownItems.Count >= 10);
                diagnostic.DropDownItems[diagnostic.DropDownItems.Count - 1].PerformClick();
            }
            else if (expression == "tray:exit")
                tray.ContextMenuStrip.Items[tray.ContextMenuStrip.Items.Count - 1].PerformClick();
            return Task.FromResult("true");
        }
        var layout = app == null || app.MainWindow == null ? null : app.MainWindow.Content as Panel;
        var browser = layout == null ? null : layout.Children.OfType<WebView2>().FirstOrDefault();
        if (browser == null || browser.CoreWebView2 == null) return Task.FromResult("null");
        return browser.CoreWebView2.ExecuteScriptAsync(expression);
    }
}
