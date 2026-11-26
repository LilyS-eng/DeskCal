using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCal
{
    internal sealed class ReminderScheduler : IDisposable
    {
        private static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(90);
        private readonly DispatcherTimer timer;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly string receiptsPath;
        private readonly Dictionary<string, long> receipts;
        private List<Reminder> schedule = new List<Reminder>();
        private Window popup;
        private StackPanel messages;
        private bool disposed;

        private sealed class Reminder
        {
            public DateTime At;
            public string Title;
            public string Key;
        }

        public ReminderScheduler(Dispatcher dispatcher, string profile)
        {
            receiptsPath = Path.Combine(profile, "DeskCal-reminder-receipts.json");
            receipts = LoadReceipts();
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) {
                Interval = TimeSpan.FromSeconds(5)
            };
            timer.Tick += (_, __) => Scan();
            timer.Start();
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.TimeChanged += ClockChanged;
        }

        public void Update(string payload)
        {
            try
            {
                var next = new List<Reminder>();
                using (var hash = SHA256.Create())
                {
                    foreach (var task in json.Deserialize<string[][]>(payload))
                    {
                        if (task == null || task.Length != 4 || string.IsNullOrEmpty(task[0]) ||
                            string.IsNullOrWhiteSpace(task[1]) || !DateTime.TryParseExact(
                                task[2] + " " + task[3], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out DateTime at)) continue;
                        // Timestamp is part of the receipt. Moving a reminder gives it a new identity.
                        var identity = Encoding.UTF8.GetBytes(json.Serialize(new[] { task[0], task[2], task[3] }));
                        next.Add(new Reminder {
                            At = at, Title = task[1], Key = Convert.ToBase64String(hash.ComputeHash(identity))
                        });
                    }
                }
                schedule = next;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                Trace.TraceWarning("Invalid reminder snapshot: " + e.Message);
                return;
            }
            Scan();
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) timer.Dispatcher.BeginInvoke(new Action(Scan));
        }

        private void ClockChanged(object sender, EventArgs e)
        {
            timer.Dispatcher.BeginInvoke(new Action(Scan));
        }

        private void Scan()
        {
            if (disposed) return;
            var now = DateTime.Now;
            var due = schedule.Where(item => item.At <= now && now - item.At <= CatchUp &&
                    !receipts.ContainsKey(item.Key))
                .GroupBy(item => item.Key).Select(group => group.First()).OrderBy(item => item.At).ToList();
            if (due.Count == 0) return;

            Show(due);
            long utc = DateTime.UtcNow.Ticks;
            foreach (var item in due) receipts[item.Key] = utc;
            foreach (var key in receipts.Where(entry => entry.Value < utc - TimeSpan.FromDays(7).Ticks)
                .Select(entry => entry.Key).ToList())
                receipts.Remove(key);
            SaveReceipts();
            SystemSounds.Asterisk.Play();
        }

        private void Show(List<Reminder> due)
        {
            if (popup == null)
            {
                var panel = new DockPanel { Margin = new Thickness(16) };
                var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var dismiss = new Button { Content = "关闭", Padding = new Thickness(8, 2, 8, 2) };
                dismiss.Click += (_, __) => popup.Close();
                DockPanel.SetDock(dismiss, Dock.Right);
                header.Children.Add(dismiss);
                header.Children.Add(new TextBlock {
                    Text = "DeskCal 提醒", FontSize = 17, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                DockPanel.SetDock(header, Dock.Top);
                panel.Children.Add(header);
                messages = new StackPanel();
                panel.Children.Add(new ScrollViewer {
                    Content = messages, MaxHeight = 208, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
                popup = new Window {
                    Title = "DeskCal 提醒", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.Manual, Topmost = true,
                    ShowInTaskbar = false, ShowActivated = false,
                    Width = 342, SizeToContent = SizeToContent.Height, Content = panel,
                    Background = new SolidColorBrush(Color.FromRgb(247, 248, 251))
                };
                popup.Closed += (_, __) => { popup = null; messages = null; };
            }
            foreach (var item in due)
            {
                messages.Children.Insert(0, new TextBlock {
                    Text = item.At.ToString("HH:mm") + "  " + item.Title, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5)
                });
            }
            popup.Show();
            popup.UpdateLayout();
            var work = SystemParameters.WorkArea;
            popup.Left = Math.Max(work.Left, work.Right - popup.ActualWidth - 20);
            popup.Top = Math.Max(work.Top, work.Bottom - popup.ActualHeight - 20);
        }

        private Dictionary<string, long> LoadReceipts()
        {
            try
            {
                if (File.Exists(receiptsPath))
                    return json.Deserialize<Dictionary<string, long>>(File.ReadAllText(receiptsPath))
                        ?? new Dictionary<string, long>();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException ||
                e is ArgumentException || e is InvalidOperationException)
            {
                Trace.TraceWarning("Could not read reminder receipts: " + e.Message);
            }
            return new Dictionary<string, long>();
        }

        private void SaveReceipts()
        {
            try
            {
                string temporary = receiptsPath + ".tmp";
                File.WriteAllText(temporary, json.Serialize(receipts));
                if (File.Exists(receiptsPath)) File.Replace(temporary, receiptsPath, null);
                else File.Move(temporary, receiptsPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not persist reminder receipts: " + e.Message);
            }
        }

        public void Dispose()
        {
            disposed = true;
            timer.Stop();
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.TimeChanged -= ClockChanged;
            popup?.Close();
        }
    }
}
