using Microsoft.Win32;

namespace ParrotBoost;

internal static class ParrotBoostSystemConfiguration
{
    private const string RegistryPath = @"Software\JGS\ParrotBoost";
    private const string EnabledValueName = "Enabled";

    public static bool IsBoostEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return (key?.GetValue(EnabledValueName) as int? ?? 0) == 1;
        }
        catch
        {
            return false;
        }
    }

    public static void SetBoostEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            key?.SetValue(EnabledValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }
}
