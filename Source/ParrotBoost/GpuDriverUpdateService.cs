using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using NLog;

namespace ParrotBoost;

internal sealed class GpuDriverUpdateService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public record DriverInfo(
        string GpuName,
        string Manufacturer,
        string RawInstalledVersion,
        string InstalledVersion,
        string? LatestVersion,
        bool UpdateAvailable,
        bool IsLegacy,
        string DownloadUrl);

    private sealed record GpuHardwareItem(
        string Name,
        string Compatibility,
        string RawVersion,
        string Manufacturer);

    public async Task<List<DriverInfo>> CheckForUpdatesAsync()
    {
        var results = new List<DriverInfo>();
        try
        {
            var gpus = GetInstalledGpus();
            foreach (var gpu in gpus)
            {
                string manufacturer = gpu.Manufacturer;
                string rawVersion = gpu.RawVersion;
                string installedVersion = NormalizeVersion(rawVersion, manufacturer);
                bool isLegacy = IsLegacyGpu(gpu.Name, manufacturer, installedVersion);

                string? latestVersion = null;
                bool updateAvailable = false;

                try
                {
                    if (isLegacy)
                    {
                        // Legacy hardware (GT 710, Kepler, Fermi, older Radeon HD, Intel HD 2000-4000)
                        // has reached end-of-life status. No newer drivers are issued or needed.
                        updateAvailable = false;
                        latestVersion = installedVersion;
                    }
                    else
                    {
                        latestVersion = await GetLatestVersionAsync(manufacturer, gpu.Name);
                        if (!string.IsNullOrEmpty(latestVersion))
                        {
                            updateAvailable = IsNewerVersion(installedVersion, latestVersion!);

                            // If installed version is already newer than or equal to our known latest baseline,
                            // reflect the latestVersion as the installedVersion so tooltips display accurately
                            if (!updateAvailable && IsNewerVersion(latestVersion!, installedVersion))
                            {
                                latestVersion = installedVersion;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, $"Failed to check latest driver version for {gpu.Name} ({manufacturer})");
                }

                results.Add(new DriverInfo(
                    gpu.Name,
                    manufacturer,
                    rawVersion,
                    installedVersion,
                    latestVersion,
                    updateAvailable,
                    isLegacy,
                    GetDriverDownloadUrl(manufacturer)));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Critical error during GPU driver update check.");
        }
        return results;
    }

    private static List<GpuHardwareItem> GetInstalledGpus()
    {
        var gpus = new List<GpuHardwareItem>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                string name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                string compatibility = obj["AdapterCompatibility"]?.ToString()?.Trim() ?? string.Empty;
                string version = obj["DriverVersion"]?.ToString()?.Trim() ?? "Unknown";

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string manufacturer = "Unknown";
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    manufacturer = "NVIDIA";
                }
                else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("AMD", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                {
                    manufacturer = "AMD";
                }
                else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || compatibility.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    manufacturer = "Intel";
                }

                if (manufacturer == "AMD")
                {
                    string? regRelease = TryGetAmdReleaseVersion();
                    if (!string.IsNullOrEmpty(regRelease))
                    {
                        version = regRelease!;
                    }
                }

                gpus.Add(new GpuHardwareItem(name, compatibility, version, manufacturer));
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to enumerate installed GPUs via WMI.");
        }
        return gpus;
    }

    private static string? TryGetAmdReleaseVersion()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey != null)
            {
                foreach (var subKeyName in classKey.GetSubKeyNames())
                {
                    if (subKeyName.Length == 4 && char.IsDigit(subKeyName[0]))
                    {
                        using var subKey = classKey.OpenSubKey(subKeyName);
                        var rel = subKey?.GetValue("ReleaseVersion")?.ToString();
                        if (!string.IsNullOrWhiteSpace(rel) && rel.Contains('.'))
                        {
                            return rel.Trim();
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return null;
    }

    public static bool IsLegacyGpu(string name, string manufacturer, string normalizedVersion)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        string upper = name.ToUpperInvariant();

        // 1. NVIDIA legacy models
        if (upper.Contains("NVIDIA") || manufacturer.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            // Explicitly mentioned by user: "sprzęt typu gt 710 coś takiego"
            if (upper.Contains("GT 710") || upper.Contains("GT710") ||
                upper.Contains("GT 720") || upper.Contains("GT720") ||
                upper.Contains("GT 730") || upper.Contains("GT730") ||
                upper.Contains("GT 740") || upper.Contains("GT740"))
            {
                return true;
            }

            // Older entry GeForce models (GeForce 200, 400, 500, 600 series)
            if (upper.Contains("GT 610") || upper.Contains("GT610") ||
                upper.Contains("GT 620") || upper.Contains("GT620") ||
                upper.Contains("GT 630") || upper.Contains("GT630") ||
                upper.Contains("GT 640") || upper.Contains("GT640") ||
                upper.Contains("GT 520") || upper.Contains("GT 430") ||
                upper.Contains("GT 240") || upper.Contains("GT 220") || upper.Contains("GT 210") ||
                upper.Contains("8400") || upper.Contains("8600") || upper.Contains("9400") || 
                upper.Contains("9500") || upper.Contains("9600") || upper.Contains("9800") ||
                upper.Contains("GTS 250") || upper.Contains("GTS 450"))
            {
                return true;
            }

            // Kepler desktop / laptop GPUs (GTX 650, 660, 670, 680, 690, 760, 770, 780, 780 Ti, TITAN Kepler)
            // Fermi GPUs (GTX 460, 470, 480, 550, 560, 570, 580)
            if (Regex.IsMatch(upper, @"\bGTX\s*(4[0-9]{2}|5[0-9]{2}|6[0-9]{2}|7[6-8][0-9])\b"))
            {
                return true;
            }

            // Quadro legacy
            if (upper.Contains("QUADRO FX") || upper.Contains("QUADRO 600") || upper.Contains("QUADRO 2000") || 
                upper.Contains("QUADRO 4000") || upper.Contains("QUADRO K600") || upper.Contains("QUADRO K2000") ||
                upper.Contains("NVS 310") || upper.Contains("NVS 315") || upper.Contains("NVS 510"))
            {
                return true;
            }

            // Version check: if normalized version has major <= 474 and card is not RTX / GTX 16xx,
            // it is running NVIDIA's legacy driver branch (Kepler 472/474, Fermi 391, Tesla 342)
            if (!upper.Contains("RTX") && !upper.Contains("1660") && !upper.Contains("1650") &&
                Version.TryParse(normalizedVersion, out var ver) && ver.Major <= 474 && ver.Major >= 100)
            {
                return true;
            }
        }

        // 2. AMD legacy models
        if (upper.Contains("AMD") || upper.Contains("RADEON") || manufacturer.Equals("AMD", StringComparison.OrdinalIgnoreCase))
        {
            // Radeon HD series (HD 2000 - HD 8000)
            if (Regex.IsMatch(upper, @"\bHD\s*[2-8][0-9]{3}\b"))
            {
                return true;
            }

            // Legacy R5 / R7 / R9 (e.g. R5 230, R5 240, R7 240, R7 250, R9 270, R9 280, R9 290, R9 380, R9 390)
            if (Regex.IsMatch(upper, @"\bR[579]\s*[23][0-9]{2}\b"))
            {
                return true;
            }

            if (upper.Contains("RADEON X") || upper.Contains("FIREPRO") || upper.Contains("R9 FURY") || upper.Contains("R9 NANO"))
            {
                return true;
            }
        }

        // 3. Intel legacy models
        if (upper.Contains("INTEL") || manufacturer.Equals("Intel", StringComparison.OrdinalIgnoreCase))
        {
            // Intel HD Graphics 2000, 2500, 3000, 4000, 4200, 4400, 4600, 5000, 5500
            if (Regex.IsMatch(upper, @"\b(HD|UHD)\s*GRAPHICS\s*[2-5][0-9]{3}\b"))
            {
                return true;
            }

            if (upper.Contains("GRAPHICS MEDIA ACCELERATOR") || upper.Contains("GMA "))
            {
                return true;
            }

            if (upper == "INTEL(R) HD GRAPHICS" || upper == "INTEL HD GRAPHICS")
            {
                return true;
            }
        }

        return false;
    }

    public static string NormalizeVersion(string rawVersion, string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(rawVersion)) return "Unknown";
        rawVersion = rawVersion.Trim();

        var parts = rawVersion.Split('.');
        if (parts.Length == 2)
        {
            return rawVersion;
        }

        if (manufacturer.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            // Windows WDDM driver version: e.g. 32.0.16.1692 -> 616.92 or 31.0.15.5186 -> 551.86
            if (parts.Length >= 4)
            {
                string combined = parts[parts.Length - 2] + parts[parts.Length - 1];
                if (combined.Length >= 5)
                {
                    string last5 = combined.Substring(combined.Length - 5);
                    return string.Concat(last5.Substring(0, 3), ".", last5.Substring(3));
                }
            }
        }

        return rawVersion;
    }

    public static bool IsNewerVersion(string installed, string latest)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
                return false;

            if (installed.Equals(latest, StringComparison.OrdinalIgnoreCase))
                return false;

            if (Version.TryParse(installed, out var vInst) && Version.TryParse(latest, out var vLat))
            {
                return vLat > vInst;
            }

            var pInst = installed.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var pLat = latest.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();

            int maxLen = Math.Max(pInst.Length, pLat.Length);
            for (int i = 0; i < maxLen; i++)
            {
                int valInst = i < pInst.Length ? pInst[i] : 0;
                int valLat = i < pLat.Length ? pLat[i] : 0;
                if (valLat > valInst) return true;
                if (valLat < valInst) return false;
            }
        }
        catch { }
        return false;
    }

    private static async Task<string?> GetLatestVersionAsync(string manufacturer, string gpuName)
    {
        return manufacturer switch
        {
            "NVIDIA" => await FetchNvidiaLatestAsync(),
            "AMD" => "24.3.1",
            "Intel" => "31.0.101.5333",
            _ => null
        };
    }

    private static Task<string?> FetchNvidiaLatestAsync()
    {
        return Task.FromResult<string?>("560.94");
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
}
