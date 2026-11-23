using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace NewsDesk.Desktop.Services;

/// <summary>
/// Registers a per-user Windows scheduled task that runs this executable with --run-due every 15 minutes while the user
/// is signed in. No administrator rights and no stored password. Missed runs start when the PC becomes available.
/// </summary>
public static class BackgroundCollection
{
    private const string TaskName = @"\PersonalNewsDesk\Collect";

    public static bool IsEnabled()
    {
        var (code, _) = Schtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static (bool Ok, string Message) Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var start = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Collects news for Personal News Desk projects that are due.</Description></RegistrationInfo>
              <Triggers>
                <TimeTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Repetition><Interval>PT15M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--run-due</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"newsdesk-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (code, output) = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            return code == 0
                ? (true, "Background collection is on. Due projects are checked every 15 minutes while you are signed in.")
                : (false, "Could not register the scheduled task: " + output.Trim());
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    public static (bool Ok, string Message) Disable()
    {
        var (code, output) = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0 ? (true, "Background collection is off. Projects run only when you refresh them.") : (false, output.Trim());
    }

    private static (int Code, string Output) Schtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, output);
    }
}
