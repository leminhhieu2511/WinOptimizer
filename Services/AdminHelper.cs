using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WinOptimizer.Services;

public static class AdminHelper
{
    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts a new elevated instance. Returns false if the user cancels the UAC prompt.</summary>
    public static bool RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, App.AfterRestartArg) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
