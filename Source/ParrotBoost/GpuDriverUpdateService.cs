using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using NLog;

namespace ParrotBoost;

internal sealed class GpuDriverUpdateService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public record DriverInfo(string Manufacturer, string InstalledVersion, string? LatestVersion, bool UpdateAvailable, string DownloadUrl);

    public async Task<List<DriverInfo>> CheckForUpdatesAsync()
    {
        var results = new List<DriverInfo>();
        try
        {
            var gpus = GetInstalledGpus();
            foreach (var gpu in gpus)
            {
                string installedVersion = gpu.Version;
                string manufacturer = gpu.Manufacturer;
                string? latestVersion = null;
                bool updateAvailable = false;

                try
                {
                    latestVersion = await GetLatestVersionAsync(manufacturer);
                    if (!string.IsNullOrEmpty(latestVersion))
                    {
                        updateAvailable = IsNewerVersion(installedVersion, latestVersion!);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Failed to check latest driver version for {manufacturer}");
                }

                results.Add(new DriverInfo(manufacturer, installedVersion, latestVersion, updateAvailable, GetDriverDownloadUrl(manufacturer)));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Critical error during GPU driver update check.");
        }
        return results;
    }

    private static List<(string Manufacturer, string Version)> GetInstalledGpus()
    {
        var gpus = new List<(string Manufacturer, string Version)>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string compatibility = obj["AdapterCompatibility"]?.ToString() ?? "";
                string version = obj["DriverVersion"]?.ToString() ?? "Unknown";

                string manufacturer = "Unknown";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) manufacturer = "NVIDIA";
                else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("AMD", StringComparison.OrdinalIgnoreCase)) manufacturer = "AMD";
                else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("Intel", StringComparison.OrdinalIgnoreCase)) manufacturer = "Intel";

                gpus.Add((manufacturer, version));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate installed GPUs via WMI.");
        }
        return gpus;
    }

    private static async Task<string?> GetLatestVersionAsync(string manufacturer)
    {
        // W rzeczywistej implementacji tutaj byłyby zapytania do API producentów.
        // Ze względu na brak publicznych, prostych API bez kluczy, używamy endpointów symulujących lub statycznych list.
        
        return manufacturer switch
        {
            "NVIDIA" => await FetchNvidiaLatest(),
            "AMD" => "24.3.1",
            "Intel" => "31.0.101.5333",
            _ => null
        };
    }

    private static Task<string?> FetchNvidiaLatest()
    {
        return Task.FromResult<string?>("551.86");
    }

    public static string GetDriverDownloadUrl(string manufacturer)
    {
        return manufacturer switch
        {
            "NVIDIA" => "https://www.nvidia.com/Download/index.aspx",
            "AMD" => "https://www.amd.com/en/support",
            "Intel" => "https://www.intel.com/content/www/us/en/support/detect.html",
            _ => "https://www.google.com/search?q=graphics+drivers+update"
        };
    }

    private static bool IsNewerVersion(string installed, string latest)
    {
        try
        {
            string cleanInstalled = NormalizeVersion(installed);
            string cleanLatest = NormalizeVersion(latest);

            if (Version.TryParse(cleanInstalled, out var vInst) && Version.TryParse(cleanLatest, out var vLat))
            {
                return vLat > vInst;
            }
        }
        catch { }
        return false;
    }

    private static string NormalizeVersion(string version)
    {
        if (version.Contains('.') && version.Split('.').Length > 3)
        {
            var parts = version.Split('.');
            if (parts.Length >= 5)
            {
                string lastTwo = parts[3] + parts[4];
                if (lastTwo.Length >= 5)
                {
                    return lastTwo.Substring(lastTwo.Length - 5, 3) + "." + lastTwo.Substring(lastTwo.Length - 2);
                }
            }
        }
        return version;
    }
}
