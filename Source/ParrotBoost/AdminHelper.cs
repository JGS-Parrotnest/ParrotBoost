using System;
using System.Diagnostics;
using System.Security.Principal;

namespace ParrotBoost;

internal static class AdminHelper
{
    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RestartAsAdmin()
    {
        string? exePath = null;
#if NET5_0_OR_GREATER
        exePath = Environment.ProcessPath;
#endif
        if (string.IsNullOrWhiteSpace(exePath))
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            Environment.Exit(0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
