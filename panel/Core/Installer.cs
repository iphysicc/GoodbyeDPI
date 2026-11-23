using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace GoodbyeDPI.Panel.Core
{
    /// <summary>
    /// Puts GoodbyeDPI into Program Files and runs it as the "GoodbyeDPI" Windows service
    /// (the name is fixed in goodbyedpi's service.c).
    /// </summary>
    public static class Installer
    {
        public const string ServiceName = "GoodbyeDPI";
        const string DriverService = "WinDivert";
        const string SettingsKey = @"SOFTWARE\GoodbyeDPI Panel";
        const string PanelExeName = "GoodbyeDPI-Panel.exe";
        static readonly string[] Payload = { "goodbyedpi.exe", "WinDivert.dll", "WinDivert64.sys" };

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GoodbyeDPI");
        public static string EngineExe => Path.Combine(Dir, "goodbyedpi.exe");
        static string PanelExe => Path.Combine(Dir, PanelExeName);
        static string Shortcut => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "GoodbyeDPI.lnk");

        public static string CurrentExe => Assembly.GetExecutingAssembly().Location;
        public static bool RunningFromInstallDir =>
            string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(PanelExe), StringComparison.OrdinalIgnoreCase);

        // ---------- platform ----------

        /// <summary>Returns a reason when this PC can't run WinDivert, otherwise null.</summary>
        public static string Unsupported()
        {
            if (!Environment.Is64BitOperatingSystem)
                return "Bu sürüm yalnız 64 bit Windows'ta çalışır.";
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            if (arch == System.Runtime.InteropServices.Architecture.Arm64)
                return "ARM işlemcili Windows desteklenmiyor: WinDivert sürücüsü yalnız x64'te çalışır.";
            return null;
        }

        // ---------- files ----------

        static byte[] ReadPayload(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + name))
            using (var m = new MemoryStream())
            {
                if (s == null) throw new InvalidOperationException("Gömülü dosya eksik: " + name);
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        static bool SameContent(string path, byte[] data)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != data.Length) return false;
            using (var sha = SHA256.Create())
                return sha.ComputeHash(File.ReadAllBytes(path)).SequenceEqual(sha.ComputeHash(data));
        }

        public static bool FilesCurrent() =>
            Payload.All(n => SameContent(Path.Combine(Dir, n), ReadPayload(n)));

        /// <summary>Copies the engine and WinDivert, the panel itself and a Start menu shortcut.</summary>
        public static void InstallFiles()
        {
            Directory.CreateDirectory(Dir);

            // The setup owns the files it installed: an older panel copy must not put back
            // its own (older) engine. Only the portable panel installs and updates them.
            if (InstalledBySetup && Payload.All(n => File.Exists(Path.Combine(Dir, n))))
            {
                CreateShortcut();
                return;
            }

            var changed = Payload.Where(n => !SameContent(Path.Combine(Dir, n), ReadPayload(n))).ToList();
            if (changed.Count > 0)
            {
                // Files in use can't be replaced: stop the engine and unload the driver first.
                bool wasRunning = IsRunning();
                if (wasRunning) Stop();
                Sc("stop " + DriverService);
                foreach (var n in changed)
                    WriteWithRetry(Path.Combine(Dir, n), ReadPayload(n));
                if (wasRunning) Start();
            }

            if (!RunningFromInstallDir)
            {
                try { File.Copy(CurrentExe, PanelExe, true); }
                catch (IOException) { /* an older panel is open; the next start updates it */ }
            }
            CreateShortcut();
        }

        static void WriteWithRetry(string path, byte[] data)
        {
            for (int i = 0; ; i++)
            {
                try { File.WriteAllBytes(path, data); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(300); }
            }
        }

        static void CreateShortcut()
        {
            if (File.Exists(Shortcut)) return;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(type);
                dynamic link = shell.CreateShortcut(Shortcut);
                link.TargetPath = PanelExe;
                link.WorkingDirectory = Dir;
                link.Description = "GoodbyeDPI";
                link.Save();
            }
            catch { /* a missing shortcut is not worth an error */ }
        }

        // ---------- settings ----------

        public static Settings LoadSettings()
        {
            var s = new Settings();
            using (var k = Registry.LocalMachine.OpenSubKey(SettingsKey))
            {
                if (k != null)
                {
                    s.Mode = k.GetValue("Mode") as string ?? s.Mode;
                    s.Dns = k.GetValue("Dns") as string ?? s.Dns;
                }
            }
            if (!Settings.IsKnownMode(s.Mode)) s.Mode = "";
            if (!Settings.DnsOptions.Any(d => d.Arg == s.Dns)) s.Dns = Settings.DnsOptions[0].Arg;
            return s;
        }

        public static void SaveSettings(Settings s)
        {
            using (var k = Registry.LocalMachine.CreateSubKey(SettingsKey))
            {
                k.SetValue("Mode", s.Mode);
                k.SetValue("Dns", s.Dns);
            }
        }

        // ---------- service ----------

        static ServiceController Find() =>
            ServiceController.GetServices().FirstOrDefault(
                s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

        public static ServiceControllerStatus? Status()
        {
            using (var s = Find()) return s?.Status;
        }

        public static bool IsRunning()
        {
            var st = Status();
            return st == ServiceControllerStatus.Running || st == ServiceControllerStatus.StartPending;
        }

        public static bool Autostart()
        {
            using (var s = Find()) return s != null && s.StartType == ServiceStartMode.Automatic;
        }

        /// <summary>Creates the service or updates its command line and start type.</summary>
        public static void Configure(Settings s, bool autostart)
        {
            string bin = "\"\\\"" + EngineExe + "\\\" " + s.Arguments + "\"";
            string start = autostart ? "auto" : "demand";
            int rc = Find() == null
                ? Sc($"create {ServiceName} binPath= {bin} start= {start} DisplayName= \"GoodbyeDPI\"")
                : Sc($"config {ServiceName} binPath= {bin} start= {start}");
            if (rc != 0)
                throw new InvalidOperationException($"Servis ayarlanamadı (sc hata {rc}).");
            Sc($"description {ServiceName} \"DPI engellerini aşar, DNS sorgularını HTTPS üzerinden çözer.\"");
            // restart by itself if it ever crashes
            Sc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        }

        public static void Start()
        {
            using (var s = Find())
            {
                if (s == null) throw new InvalidOperationException("Servis kurulu değil.");
                if (s.Status == ServiceControllerStatus.Running) return;
                if (s.Status == ServiceControllerStatus.StopPending)
                    s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                s.Start();
                s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
        }

        public static void Stop()
        {
            using (var s = Find())
            {
                if (s == null || s.Status == ServiceControllerStatus.Stopped) return;
                if (s.Status != ServiceControllerStatus.StopPending) s.Stop();
                s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            FlushDns();
        }

        public static void FlushDns() => Run("ipconfig.exe", "/flushdns", 5000, out _);

        /// <summary>
        /// Applies the options and (re)starts the service, then waits until goodbyedpi
        /// reports that its filter is active. Returns false if it stopped instead.
        /// </summary>
        public static bool Restart(Settings s, bool autostart)
        {
            Configure(s, autostart);
            Stop();
            DateTime started = DateTime.Now;
            Start();
            return WaitUntilActive(started, TimeSpan.FromSeconds(10));
        }

        static bool WaitUntilActive(DateTime since, TimeSpan timeout)
        {
            DateTime end = DateTime.Now + timeout;
            while (DateTime.Now < end)
            {
                if (!IsRunning()) return false;
                try
                {
                    var fi = new FileInfo(LogFile);
                    // the log is rewritten at every start: ignore the previous run's file
                    if (fi.Exists && fi.LastWriteTime >= since.AddSeconds(-1))
                    {
                        string text;
                        using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var sr = new StreamReader(fs))
                            text = sr.ReadToEnd();
                        if (text.Contains("GoodbyeDPI is now running")) return true;
                    }
                }
                catch (IOException) { }
                Thread.Sleep(200);
            }
            return IsRunning();   // an old engine without a log: trust the service state
        }

        /// <summary>
        /// The service can't show why it stopped, so run the engine once in the foreground
        /// with the same options and return its error message.
        /// </summary>
        public static string Diagnose(Settings s)
        {
            // the service writes goodbyedpi.log; fall back to a foreground run
            string output = null;
            try
            {
                using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    output = sr.ReadToEnd();
            }
            catch { }
            if (string.IsNullOrWhiteSpace(output) || ErrorLine(output) == null)
                Run(EngineExe, s.Arguments + " --debug-exit", 12000, out output);
            return ErrorLine(output);
        }

        public static string LogFile => Path.Combine(Dir, "goodbyedpi.log");

        static string ErrorLine(string output)
        {
            var lines = output.Replace("\r", "").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            int i = lines.FindIndex(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                                         l.StartsWith("WARNING: no DNS") || l.StartsWith("DoH:"));
            if (i < 0) return null;
            return string.Join(" ", lines.Skip(i).Take(3));
        }

        // ---------- uninstall ----------

        static string SetupUninstaller => Path.Combine(Dir, "unins000.exe");

        /// <summary>True when the setup program installed it (it then owns the uninstall).</summary>
        public static bool InstalledBySetup => File.Exists(SetupUninstaller);

        public static void Uninstall()
        {
            if (InstalledBySetup)
            {
                // the setup's uninstaller stops the service and removes everything, also
                // the entry in Settings > Apps; it waits for this panel to exit
                Stop();
                // start it after this panel has exited, or its exe can't be deleted
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c ping 127.0.0.1 -n 3 >nul & \"{SetupUninstaller}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                return;
            }

            Stop();
            try { Startup.Disable(); } catch { }
            Sc($"delete {ServiceName}");
            Sc("stop " + DriverService);
            try { File.Delete(Shortcut); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(SettingsKey, false); } catch { }

            foreach (var n in Payload)
                try { File.Delete(Path.Combine(Dir, n)); } catch { }

            // The panel can't delete itself while it runs: let cmd finish after we exit.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{Dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        // ---------- helpers ----------

        static int Sc(string args) => Run("sc.exe", args, 20000, out _);

        static int Run(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            var sb = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (o, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    p.WaitForExit(2000);
                    lock (sb) output = sb.ToString();
                    return -1;
                }
                p.WaitForExit();
                lock (sb) output = sb.ToString();
                return p.ExitCode;
            }
        }
    }
}
