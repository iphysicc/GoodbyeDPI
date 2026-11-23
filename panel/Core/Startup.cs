using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace GoodbyeDPI.Panel.Core
{
    /// <summary>
    /// Starts the panel in the notification area at logon. The panel needs administrator
    /// rights and Windows skips such programs in the Run key, so a scheduled task with
    /// "highest privileges" is used. Battery and time limits are turned off: by default a
    /// task doesn't start on battery and is killed after 72 hours.
    /// </summary>
    public static class Startup
    {
        public const string TaskName = "GoodbyeDPI Panel";

        public static void Enable()
        {
            string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
            string exe = SecurityElement.Escape(Path.Combine(Installer.Dir, "GoodbyeDPI-Panel.exe"));
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>GoodbyeDPI panelini bildirim alanında başlatır.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>""{exe}""</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";
            string file = Path.Combine(Path.GetTempPath(), "goodbyedpi-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            try
            {
                int rc = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
                if (rc != 0) throw new InvalidOperationException($"Açılış görevi eklenemedi (schtasks hata {rc}).");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        public static void Disable() => Schtasks($"/Delete /TN \"{TaskName}\" /F");

        public static bool Enabled() => Schtasks($"/Query /TN \"{TaskName}\"") == 0;

        static int Schtasks(string args)
        {
            using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }
}
