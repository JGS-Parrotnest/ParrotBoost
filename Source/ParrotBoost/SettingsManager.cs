using System;
using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace ParrotBoost;

public class UserSettings
{
    public bool IsDarkMode { get; set; } = false;
    public string Language { get; set; } = "en-US";
    public bool LaunchAtStartup { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ParrotBoostSystemEnabled { get; set; } = false;
    
    // Performance preferences
    public bool OptServices { get; set; } = true;
    public bool OptMemory { get; set; } = true;
    public bool OptTasks { get; set; } = true;
    public bool OptNtfs { get; set; } = true;
    public bool OptPriority { get; set; } = true;
    public bool OptUsb { get; set; } = true;
    public bool OptDelivery { get; set; } = true;
    public bool OptTick { get; set; } = true;
    public bool EnableGameMode { get; set; } = false;

    // Performance monitor persistence
    public float LastCpuLoad { get; set; } = 0;
    public float LastGpuLoad { get; set; } = 0;
    public float LastCpuTemp { get; set; } = 0;
    public float LastGpuTemp { get; set; } = 0;

    // Cleanup preferences
    public bool CleanUpdateCache { get; set; } = true;
    public bool CleanRecycleBin { get; set; } = true;
    public bool CleanComponentStore { get; set; } = false;
    public bool CleanMinidumps { get; set; } = false;
    public bool OptimizeBootFiles { get; set; } = false;
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JGS",
        "settings.json"
    );

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                return settings ?? new UserSettings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }
        return new UserSettings();
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            string directory = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
