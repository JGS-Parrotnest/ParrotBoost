using Microsoft.Win32;

namespace ParrotBoost;

internal static class RegistryHelper
{
    private static bool TryResolvePath(string path, out RegistryKey baseKey, out string subKey)
    {
        baseKey = Registry.CurrentUser;
        subKey = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string[] parts = path.Split(new[] { '\\' }, 2);
        if (parts.Length != 2)
        {
            return false;
        }

        baseKey = parts[0] == "HKEY_LOCAL_MACHINE" ? Registry.LocalMachine : Registry.CurrentUser;
        subKey = parts[1];
        return !string.IsNullOrWhiteSpace(subKey);
    }

    public static void SetDword(string path, string keyName, int value)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            using var key = baseKey.CreateSubKey(subKey, true);
            key?.SetValue(keyName, value, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    public static int GetDword(string path, string keyName, int defaultValue = 0)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return defaultValue;
            }

            using var key = baseKey.OpenSubKey(subKey);
            var val = key?.GetValue(keyName);
            return val is int i ? i : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static void SetString(string path, string keyName, string value)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            using var key = baseKey.CreateSubKey(subKey, true);
            key?.SetValue(keyName, value, RegistryValueKind.String);
        }
        catch
        {
        }
    }

    public static string GetString(string path, string keyName, string defaultValue = "")
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return defaultValue;
            }

            using var key = baseKey.OpenSubKey(subKey);
            var val = key?.GetValue(keyName);
            return val is string s ? s : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    public static string? GetStringOrNull(string path, string keyName)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return null;
            }

            using var key = baseKey.OpenSubKey(subKey);
            var val = key?.GetValue(keyName);
            return val as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool KeyExists(string path)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return false;
            }

            using var key = baseKey.OpenSubKey(subKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void CreateKey(string path)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            using var _ = baseKey.CreateSubKey(subKey, true);
        }
        catch
        {
        }
    }

    public static void DeleteKey(string path)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            baseKey.DeleteSubKeyTree(subKey, false);
        }
        catch
        {
        }
    }

    public static void DeleteValue(string path, string valueName)
    {
        try
        {
            if (!TryResolvePath(path, out RegistryKey baseKey, out string subKey))
            {
                return;
            }

            using var key = baseKey.OpenSubKey(subKey, true);
            key?.DeleteValue(valueName, false);
        }
        catch
        {
        }
    }
}
