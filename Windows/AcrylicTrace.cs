using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DeskCal
{
    internal sealed class AcrylicTrace : IDisposable
    {
        private readonly Window window;
        private readonly Grid surface;
        private readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        private readonly Thread writer;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly string path;
        private HwndSource source;
        private string lastLayout;
        private long hitSample = -1000;
        private bool closed;
        private const int FirstId = 0x5100;
        private static readonly string[] markers = { "COLD_START_GRAY", "BEFORE_MANUAL_RESIZE",
            "MANUAL_RESIZE_ACRYLIC_OK", "TRAY_RESTORE_GRAY", "TRAY_RESIZE_ACRYLIC_OK",
            "EDGE_RESTORE_GRAY", "EDGE_RESIZE_ACRYLIC_OK", "OBSERVED_GRAY", "OBSERVED_ACRYLIC_OK" };
        private readonly bool[] registered = new bool[markers.Length];

        public AcrylicTrace(Window window, Grid surface)
        {
            this.window = window;
            this.surface = surface;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskCal", "logs");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"acrylic-no-webview2-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Process.GetCurrentProcess().Id}.log");
            var stream = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            writer = new Thread(() => {
                using (stream) {
                    try { foreach (var line in lines.GetConsumingEnumerable()) stream.WriteLine(line); }
                    catch (IOException) { /* Disk unavailable during the session. */ }
                }
            }) { IsBackground = true };
            writer.Start();
            Record("startup OS=" + Environment.OSVersion + " variant=B-pure-WPF");
            window.SourceInitialized += (_, __) => {
                source = (HwndSource)PresentationSource.FromVisual(window);
                source.AddHook(Hook);
                for (int i = 0; i < markers.Length; i++)
                    registered[i] = RegisterHotKey(source.Handle, FirstId + i, 0x4003, (uint)('1' + i));
                Record("SourceInitialized hotkeys=" + string.Join(",", registered));
            };
            window.Loaded += (_, __) => Record("Loaded");
            window.ContentRendered += (_, __) => Record("ContentRendered");
            window.StateChanged += (_, __) => Record("StateChanged");
            window.IsVisibleChanged += (_, __) => Record("IsVisibleChanged " + window.IsVisible);
            window.SizeChanged += (_, e) => Record("SizeChanged previous=" + e.PreviousSize);
            surface.SizeChanged += (_, e) => Record("Surface.SizeChanged previous=" + e.PreviousSize);
            surface.LayoutUpdated += (_, __) => {
                string layout = window.RenderSize + "/" + surface.RenderSize + "/" + surface.IsArrangeValid;
                if (lastLayout != layout) { lastLayout = layout; Record("LayoutUpdated geometry/validity changed"); }
            };
        }

        public void Menu(Forms.ContextMenuStrip menu)
        {
            var group = new Forms.ToolStripMenuItem("Acrylic 诊断");
            for (int i = 0; i < markers.Length; i++) {
                string marker = markers[i];
                group.DropDownItems.Add($"Ctrl+Alt+{i+1}  {marker}", null, (_, __) => Mark(marker));
            }
            menu.Items.Add(group);
        }

        private void Mark(string marker)
        {
            Record("MARK " + marker);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int L,T,R,B; public override string ToString() => $"{L},{T},{R},{B}"; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Placement { public int Length,Flags,Show,MinX,MinY,MaxX,MaxY; public Rect Normal; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Pos { public IntPtr Window,After; public int X,Y,W,H; public uint Flags; }
        private delegate bool Each(IntPtr h, IntPtr arg);
        [DllImport("user32")] private static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
        [DllImport("user32")] private static extern bool UnregisterHotKey(IntPtr h,int id);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr h,out Rect r);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr h,out Rect r);
        [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] private static extern bool GetWindowPlacement(IntPtr h,ref Placement p);
        [DllImport("user32")] private static extern bool EnumChildWindows(IntPtr h,Each each,IntPtr arg);
        [DllImport("user32")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h,StringBuilder s,int n);
        [DllImport("user32",EntryPoint="GetWindowLongW")] private static extern int Style(IntPtr h,int index);
        [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("dwmapi")] private static extern int DwmIsCompositionEnabled(out bool enabled);
        [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(IntPtr h,int a,out int v,int bytes);
        private static string H(IntPtr h) => h.ToInt64().ToString("X");
        private static string Id(object o) => o == null ? "-" : RuntimeHelpers.GetHashCode(o).ToString("X");
        private static string Native(IntPtr h)
        {
            GetWindowRect(h,out var r); GetClientRect(h,out var c);
            return $"hwnd={H(h)} rect={r} client={c} style={Style(h,-16):X8}/{Style(h,-20):X8} visible={IsWindowVisible(h)}";
        }
        private static string Layout(FrameworkElement e) =>
            $"{e.ActualWidth},{e.ActualHeight}/{e.RenderSize}/measure:{e.IsMeasureValid},arrange:{e.IsArrangeValid}";

        public void Record(string reason)
        {
            if (closed) return;
            var b = new StringBuilder().Append(DateTimeOffset.Now.ToString("O")).Append(" ticks=")
                .Append(clock.ElapsedTicks).Append(" ").Append(reason);
            try {
                if (reason.StartsWith("MARK ") || reason == "Closed" || reason == "pure WPF surface ready") {
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name);
                    using (var proc = Process.GetCurrentProcess()) {
                        var modules = proc.Modules.Cast<ProcessModule>().Select(m => m.ModuleName);
                        var matches = assemblies.Concat(modules).Where(n =>
                            n.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0);
                        b.Append(" browserModules=[").Append(string.Join(",", matches)).Append("]");
                    }
                }
                var src = PresentationSource.FromVisual(window) as HwndSource;
                b.Append($" | source={Id(src)} target={Id(src?.CompositionTarget)} rootVisual={Id(src?.RootVisual)} windowBg={window.Background} targetBg={src?.CompositionTarget?.BackgroundColor} panelBg={(window.Content as Panel)?.Background ?? Brushes.Transparent}(null={((window.Content as Panel)?.Background == null)})");
                b.Append($" WPF={Layout(window)} active={window.IsActive} visible={window.IsVisible} state={window.WindowState} windowStyle={window.WindowStyle} allowsTransparency={window.AllowsTransparency} surface={Layout(surface)} surfaceBg={surface.Background}");
                var h = source?.Handle ?? IntPtr.Zero;
                if (h != IntPtr.Zero) {
                    var p = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
                    bool got = GetWindowPlacement(h,ref p);
                    int comp = DwmIsCompositionEnabled(out bool enabled);
                    b.Append($" | {Native(h)} dpi={GetDpiForWindow(h)} placement={got}/{p.Flags}/{p.Show}/{p.Normal} composition={comp:X8}/{enabled}");
                    foreach (int a in new[]{14,20,33,38}) {
                        int hr = DwmGetWindowAttribute(h,a,out int v,4);
                        b.Append($" attr{a}={hr:X8}/").Append(hr == 0 ? v.ToString() : "?");
                    }
                    EnumChildWindows(h,(child,_) => {
                        var name = new StringBuilder(128); GetClassName(child,name,name.Capacity);
                        b.Append($" | child {name} parent={H(GetParent(child))} {Native(child)}"); return true;
                    },IntPtr.Zero);

                }
            } catch (Exception e) { b.Append(" snapshotError=").Append(e.GetType().Name).Append("/").Append(e.HResult.ToString("X8")); }
            lines.Add(b.ToString());
        }

        private IntPtr Hook(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled)
        {
            if (msg == 0x312 && w.ToInt64() >= FirstId && w.ToInt64() < FirstId + markers.Length) {
                Mark(markers[w.ToInt32()-FirstId]); handled=true; return IntPtr.Zero;
            }
            string name;
            switch(msg) {
                case 5: name="WM_SIZE"; break;
                case 6: name="WM_ACTIVATE"; break;
                case 0x1c: name="WM_ACTIVATEAPP"; break;
                case 0x18: name="WM_SHOWWINDOW"; break;
                case 0x47:
                    var p=Marshal.PtrToStructure<Pos>(l);
                    name=$"WM_WINDOWPOSCHANGED xy={p.X},{p.Y} size={p.W},{p.H} flags={p.Flags:X}"; break;
                case 0x83: name="WM_NCCALCSIZE"; break;
                case 0x84:
                    if(clock.ElapsedMilliseconds-hitSample < 500) return IntPtr.Zero;
                    hitSample=clock.ElapsedMilliseconds; name="WM_NCHITTEST(sample)"; break;
                case 0x7d: name="WM_STYLECHANGED"; break;
                case 0x2e0: name="WM_DPICHANGED"; break;
                case 0x31e: name="WM_DWMCOMPOSITIONCHANGED"; break;
                case 0x31f: name="WM_DWMNCRENDERINGCHANGED"; break;
                case 0x320: name="WM_DWMCOLORIZATIONCOLORCHANGED"; break;
                case 0x231: name="WM_ENTERSIZEMOVE"; break;
                case 0x232: name="WM_EXITSIZEMOVE"; break;
                default: return IntPtr.Zero;
            }
            Record(name+" w="+H(w)+" l="+H(l));
            return IntPtr.Zero;
        }
        public void Dispose()
        {
            Record("Closed");
            closed=true;
            if (source != null && !source.IsDisposed) {
                for(int i=0;i<registered.Length;i++) if(registered[i]) UnregisterHotKey(source.Handle,FirstId+i);
                source.RemoveHook(Hook);
            }
            lines.CompleteAdding(); writer.Join();
        }
    }
}
