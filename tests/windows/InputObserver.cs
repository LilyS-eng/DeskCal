using System;
using System.IO;
using System.Linq;
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
        var layout = app == null || app.MainWindow == null ? null : app.MainWindow.Content as Panel;
        var browser = layout == null ? null : layout.Children.OfType<WebView2>().FirstOrDefault();
        if (browser == null || browser.CoreWebView2 == null) return Task.FromResult("null");
        return browser.CoreWebView2.ExecuteScriptAsync(expression);
    }
}
