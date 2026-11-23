using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace GoodbyeDPI.Panel
{
    public partial class App : Application
    {
        const string MutexName = @"Global\GoodbyeDPI-Panel";
        const string ShowSignalName = @"Global\GoodbyeDPI-Panel-Show";

        static Mutex single;
        static EventWaitHandle showSignal;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // One panel at a time. Opening it again tells the running one to show its
            // window, even when it is hidden in the notification area.
            single = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { }
                Shutdown();
                return;
            }

            var main = new MainWindow();
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
            new Thread(() =>
            {
                while (showSignal.WaitOne())
                    Dispatcher.BeginInvoke(new Action(main.ShowFromTray));
            }) { IsBackground = true, Name = "show-signal" }.Start();

            // at logon (--tray) only the notification area icon appears
            if (!e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
                main.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { single?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
