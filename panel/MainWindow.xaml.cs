using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GoodbyeDPI.Panel.Core;

namespace GoodbyeDPI.Panel
{
    public partial class MainWindow : Window
    {
        Settings settings = new Settings();
        bool ready;            // setup finished, controls are live
        bool busy;             // an action is running
        bool userStopped = true;
        bool exiting;          // Çıkış chosen: really close instead of hiding
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        readonly TrayIcon tray = new TrayIcon();
        readonly DispatcherTimer netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        DateTime lastBackgroundCheck = DateTime.MinValue;
        Updater.Release update;
        CancellationTokenSource searchCts;

        public MainWindow()
        {
            InitializeComponent();
            DnsBox.ItemsSource = Settings.DnsOptions;
            VersionText.Text = "v" + Updater.Current.ToString(3);
            timer.Tick += (s, e) => { if (!busy) Refresh(); };
            // setup runs even when the panel starts hidden in the notification area
            SourceInitialized += (s, e) => RoundCorners();
            Dispatcher.BeginInvoke(new Action(Initialize));

            // a new network (another Wi-Fi, a hotspot, waking up) may need another method
            netTimer.Tick += async (s, e) => { netTimer.Stop(); await BackgroundCheck(); };
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
                (s, e) => Dispatcher.BeginInvoke(new Action(() => { netTimer.Stop(); netTimer.Start(); }));
            updateTimer.Tick += async (s, e) => await CheckForUpdate();
            KeyDown += (s, e) => { if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) HideOverlay(); };

            // X keeps the panel in the notification area; Çıkış in its menu closes it
            Closing += (s, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
            IsVisibleChanged += (s, e) => timer.Interval = IsVisible ? TimeSpan.FromSeconds(1.5) : TimeSpan.FromSeconds(8);
            tray.OpenRequested += ShowFromTray;
            tray.ToggleRequested += () => MainButton_Click(null, null);
            tray.OpenLogRequested += OpenLog;
            tray.ExitRequested += ExitApp;
        }

        void OpenLog()
        {
            if (File.Exists(Installer.LogFile))
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Installer.LogFile}\"") { UseShellExecute = true }); }
                catch { }
                return;
            }
            ShowFromTray();
            ShowProblem("Henüz günlük dosyası oluşmamış. Korumayı bir kez başlattıktan sonra burada görünür.");
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;    // bring it above other windows once
            Topmost = false;
            Focus();
        }

        void ExitApp()
        {
            exiting = true;
            timer.Stop();
            tray.Dispose();
            Application.Current.Shutdown();
        }

        // ---------- startup ----------

        async void Initialize()
        {
            string why = Installer.Unsupported();
            if (why != null)
            {
                StateTitle.Text = "Desteklenmiyor";
                ShowProblem(why);
                return;
            }

            StateTitle.Text = "Hazırlanıyor";
            StateLine.Text = "GoodbyeDPI bu bilgisayara kuruluyor.";
            bool ok = await RunBusy(MainButton, () =>
            {
                Installer.InstallFiles();
                settings = Installer.LoadSettings();
                // keep the service pointing at this install, with the saved options
                Installer.Configure(settings, Installer.Autostart());
                return Task.CompletedTask;
            });
            if (!ok)
            {
                StateTitle.Text = "Kurulamadı";
                StateLine.Text = " ";
                return;
            }

            DnsBox.SelectedItem = Settings.DnsOptions.First(d => d.Arg == settings.Dns);
            bool auto = Installer.Autostart();
            AutoBox.IsChecked = auto;
            userStopped = !Installer.IsRunning();
            ready = true;
            SetControlsEnabled(true);
            Refresh();
            timer.Start();

            // older installs started only the service with Windows, not the panel
            if (auto) _ = Task.Run(() => { try { Startup.Enable(); } catch { } });

            // protection already on (e.g. after logon): make sure it still works here
            if (Installer.IsRunning()) { netTimer.Stop(); netTimer.Start(); }

            await CheckForUpdate();
            updateTimer.Start();
        }

        /// <summary>
        /// Quietly re-tests the running protection; searches for another method only when
        /// the current one no longer opens the test sites (another network, another ISP).
        /// </summary>
        async Task BackgroundCheck()
        {
            if (!ready || busy || !Installer.IsRunning()) return;
            if (DateTime.Now - lastBackgroundCheck < TimeSpan.FromSeconds(60)) return;
            lastBackgroundCheck = DateTime.Now;

            var r = await Checker.Run(6);
            if (busy || !Installer.IsRunning()) return;
            if (AutoTuner.IsGood(r)) { tray.SetProblem(null); return; }
            if (!await Checker.InternetWorks()) return;   // offline: the next network change retries
            if (busy || !Installer.IsRunning()) return;

            await Connect(search: true);
            Refresh();
        }

        async Task CheckForUpdate()
        {
            update = await Updater.CheckAsync();
            if (update == null) return;
            UpdateButton.Content = "Güncelle (" + update.Version.ToString(3) + ")";
            UpdateButton.Visibility = Visibility.Visible;
            VersionText.Visibility = Visibility.Collapsed;
        }

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (busy || update == null) return;
            var u = update;
            bool ok = await RunBusy(UpdateButton, () => Updater.DownloadAndRunAsync(u));
            // the setup closes this panel itself and opens the new one when it finishes
            if (ok) ExitApp();
        }

        // ---------- state ----------

        void Refresh()
        {
            if (!ready) return;
            var status = Installer.Status();
            bool running = status == ServiceControllerStatus.Running || status == ServiceControllerStatus.StartPending;

            ModeLine.Text = string.IsNullOrEmpty(settings.Mode)
                ? "Başlatınca otomatik bulunur"
                : "Bu bağlantıda çalışan: " + settings.Mode;

            if (running)
            {
                StateTitle.Text = "Koruma açık";
                StateLine.Text = "Pencereyi kapatabilirsin, arka planda çalışır.";
                MainButton.Style = (Style)FindResource("TonalButton");
                MainButton.Content = "Durdur";
                tray.SetState(true);
            }
            else
            {
                StateTitle.Text = "Koruma kapalı";
                StateLine.Text = "Başlatınca DPI engeli aşılır, DNS sorguları şifrelenir.";
                MainButton.Style = (Style)FindResource("PrimaryButton");
                MainButton.Content = "Başlat";
                tray.SetState(false);
                if (!userStopped)
                {
                    // it was running and stopped without us: tell why
                    userStopped = true;
                    _ = ExplainStop();
                }
            }
        }

        async Task ExplainStop()
        {
            var s = settings;
            string why = await Task.Run(() => Installer.Diagnose(s));
            ShowProblem("GoodbyeDPI durdu. " + (why ?? "Bilgisayarı yeniden başlatıp tekrar dene."));
        }

        void ShowSearching()
        {
            StateTitle.Text = "Bağlanıyor";
            StateLine.Text = "Bu bağlantıda çalışan yöntem aranıyor.";
        }

        void Progress(int i, int n) =>
            Dispatcher.Invoke(() => ModeLine.Text = $"Deneniyor: {i}/{n}");

        void ShowOutcome(AutoTuner.Outcome o)
        {
            if (o.Mode != null)
            {
                HideTestResult();
                tray.SetProblem(null);
                return;
            }
            tray.SetProblem("bazı siteler açılmıyor");
            var last = o.Last;
            if (last != null && last.Broken.Count > 0)
                TestResult.Text = "Engelli siteleri açan yöntemler şu siteleri bozuyor: " + string.Join(", ", last.Broken) +
                    ". Koruma en az bozan yöntemle açık bırakıldı.";
            else
                TestResult.Text = "Hiçbir yöntem Discord ve Roblox'u açamadı. Koruma açık bırakıldı, DNS engeli yine aşılır." +
                    (last != null && last.Problems.Count > 0 ? "\n" + string.Join("\n", last.Problems) : "");
            TestResult.Visibility = Visibility.Visible;
        }

        // ---------- actions ----------

        async void MainButton_Click(object sender, RoutedEventArgs e)
        {
            if (searchCts != null)
            {
                searchCts.Cancel();
                MainButton.IsEnabled = false;
                return;
            }
            if (busy) return;
            HideProblem();
            HideTestResult();
            if (Installer.IsRunning())
            {
                userStopped = true;
                await RunBusy(MainButton, () => Task.Run(Installer.Stop));
            }
            else
            {
                await Connect(search: false);
            }
            Refresh();
        }

        async void Retune_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            HideProblem();
            HideTestResult();
            await Connect(search: true);
            Refresh();
        }

        /// <summary>Starts protection with a method that works on this connection.</summary>
        async Task Connect(bool search)
        {
            bool auto = AutoBox.IsChecked == true;
            bool wasRunning = Installer.IsRunning();
            var s = settings;
            userStopped = true;
            ShowSearching();
            AutoTuner.Outcome outcome = null;
            bool cancelled = false;
            var cts = new CancellationTokenSource();
            searchCts = cts;
            bool ok = await RunBusy(search ? RetuneButton : null, async () =>
            {
                try
                {
                    outcome = search
                        ? await AutoTuner.Search(s, auto, Progress, cts.Token)
                        : await AutoTuner.StartAndVerify(s, auto, Progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    if (!wasRunning) Installer.Stop();
                }
            }, cancellable: true);
            searchCts = null;
            cts.Dispose();
            if (ok && !cancelled && outcome != null)
            {
                ShowOutcome(outcome);
                userStopped = !Installer.IsRunning();
            }
        }

        async void Setting_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!ready || busy) return;
            if (DnsBox.SelectedItem is Option d) settings.Dns = d.Arg;
            Installer.SaveSettings(settings);
            HideProblem();
            if (Installer.IsRunning())
            {
                await Connect(search: false);
            }
            else
            {
                bool auto = AutoBox.IsChecked == true;
                var s = settings;
                await RunBusy(null, () => Task.Run(() => Installer.Configure(s, auto)));
            }
            Refresh();
        }

        async void Autostart_Changed(object sender, RoutedEventArgs e)
        {
            if (!ready || busy) return;
            bool auto = AutoBox.IsChecked == true;
            var s = settings;
            await RunBusy(null, () => Task.Run(() =>
            {
                Installer.Configure(s, auto);
                // with Windows: the service starts protection, the panel shows its icon
                if (auto) Startup.Enable(); else Startup.Disable();
            }));
        }

        void Uninstall_Click(object sender, RoutedEventArgs e) => Overlay.Visibility = Visibility.Visible;
        void CancelUninstall_Click(object sender, RoutedEventArgs e) => HideOverlay();
        void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

        async void ConfirmUninstall_Click(object sender, RoutedEventArgs e)
        {
            userStopped = true;
            timer.Stop();
            bool ok = await RunBusy(ConfirmUninstallButton, () => Task.Run(Installer.Uninstall));
            if (ok) ExitApp();
            else HideOverlay();
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        // ---------- helpers ----------

        /// <summary>Runs work with three dots in the button and the controls locked.</summary>
        async Task<bool> RunBusy(Button button, Func<Task> work, bool cancellable = false)
        {
            busy = true;
            tray.SetBusy(true);
            object content = button?.Content;
            if (button != null) button.Content = new LoadingDots(button.Foreground);
            SetControlsEnabled(false);
            if (cancellable)
            {
                MainButton.Style = (Style)FindResource("TonalButton");
                MainButton.Content = "İptal";
                MainButton.IsEnabled = true;
            }
            try
            {
                await Task.Run(work);
                return true;
            }
            catch (Exception ex)
            {
                ShowProblem(ex.InnerException?.Message ?? ex.Message);
                return false;
            }
            finally
            {
                if (button != null) button.Content = content;
                busy = false;
                tray.SetBusy(false);
                SetControlsEnabled(ready);
            }
        }

        void SetControlsEnabled(bool on)
        {
            MainButton.IsEnabled = on;
            RetuneButton.IsEnabled = on;
            DnsBox.IsEnabled = on;
            AutoBox.IsEnabled = on;
            UninstallButton.IsEnabled = on;
            UpdateButton.IsEnabled = on;
        }

        void ShowProblem(string text)
        {
            Problem.Text = text;
            Problem.Visibility = Visibility.Visible;
        }

        void HideProblem() => Problem.Visibility = Visibility.Collapsed;
        void HideTestResult() => TestResult.Visibility = Visibility.Collapsed;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void RoundCorners()
        {
            // Windows 11 rounds borderless windows only when asked (no effect on Windows 10)
            int round = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int)); }
            catch { }
        }
    }
}
