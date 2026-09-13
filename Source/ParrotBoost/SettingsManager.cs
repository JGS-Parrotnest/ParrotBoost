using System;
using System.IO;
using System.Text.Json;

namespace ParrotBoost;

public class UserSettings
{
    public const int MinCpuUsageLimitPercent = 5;
    public const int MaxCpuUsageLimitPercent = 100;

    public bool IsDarkMode { get; set; } = false;
    public string Language { get; set; } = "en-US";
    public bool LaunchAtStartup { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ParrotBoostSystemEnabled { get; set; } = false;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public int CpuUsageLimitPercent { get; set; } = 80;

    public bool OptServices { get; set; } = true;
    public bool OptMemory { get; set; } = true;
    public bool OptTasks { get; set; } = true;
    public bool OptNtfs { get; set; } = true;
    public bool OptPriority { get; set; } = true;
    public bool OptUsb { get; set; } = true;
    public bool OptDelivery { get; set; } = true;
    public bool OptBlockDeliveryUpload { get; set; } = true;
    public bool OptDisableTelemetryTasks { get; set; } = true;
    public bool OptDisableCopilot { get; set; } = true;
    public bool OptDisableRecall { get; set; } = true;
    public bool OptDisableClickToDo { get; set; } = true;
    public bool OptDisableGenerativeSearchAi { get; set; } = true;
    public bool OptDisableAppAiFeatures { get; set; } = true;
    public bool OptDisableEdgeAi { get; set; } = true;
    public bool OptDisableAdvertisingId { get; set; } = true;
    public bool OptDisableDiagnosticData { get; set; } = true;
    public bool OptDisableActivityHistory { get; set; } = true;
    public bool OptDisableLocationTracking { get; set; } = true;
    public bool OptDisableFeedbackNotifications { get; set; } = true;
    public bool OptTick { get; set; } = true;
    public bool EnableGameMode { get; set; } = false;

    public float LastCpuLoad { get; set; } = 0;
    public float LastGpuLoad { get; set; } = 0;
    public float LastCpuTemp { get; set; } = 0;
    public float LastGpuTemp { get; set; } = 0;

    public bool CleanPrefetch { get; set; } = true;
    public bool CleanTemp { get; set; } = true;
    public bool CleanWinTemp { get; set; } = true;
    public bool CleanUpdateCache { get; set; } = true;
    public bool CleanRecycleBin { get; set; } = true;
    public bool CleanBrowserCache { get; set; } = true;
    public bool CleanThumbnails { get; set; } = true;
    public bool CleanErrorReporting { get; set; } = true;
    public bool CleanSystemLogs { get; set; } = true;
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

    public static void Normalize(UserSettings settings)
    {
        if (settings == null) return;

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            settings.Language = "en-US";
        }

        if (settings.CpuUsageLimitPercent < UserSettings.MinCpuUsageLimitPercent)
        {
            settings.CpuUsageLimitPercent = UserSettings.MinCpuUsageLimitPercent;
        }
        else if (settings.CpuUsageLimitPercent > UserSettings.MaxCpuUsageLimitPercent)
        {
            settings.CpuUsageLimitPercent = UserSettings.MaxCpuUsageLimitPercent;
        }
    }

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<UserSettings>(json);
                if (settings != null)
                {
                    Normalize(settings);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }

        var defaultSettings = new UserSettings();
        Normalize(defaultSettings);
        return defaultSettings;
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            Normalize(settings);
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
