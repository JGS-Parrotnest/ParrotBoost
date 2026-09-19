using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ParrotBoost;

internal sealed class DeviceManagerDiagnosticService
{
    internal readonly record struct DeviceIssue(
        string Name,
        string Manufacturer,
        int ErrorCode,
        string Status);

    internal readonly record struct DriverHealthReport(
        int TotalDevices,
        int HealthyDevices,
        IReadOnlyList<DeviceIssue> Issues,
        string Summary,
        string Recommendation,
        bool HasPotentialCpuRisk);

    public DriverHealthReport Analyze()
    {
        try
        {
            var issues = new List<DeviceIssue>();
            int totalDevices = 0;

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, ConfigManagerErrorCode, Status FROM Win32_PnPEntity");

            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                totalDevices++;

                string name = obj["Name"]?.ToString() ?? "Unknown device";
                string manufacturer = obj["Manufacturer"]?.ToString() ?? "Unknown vendor";
                int errorCode = Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0);
                string status = obj["Status"]?.ToString() ?? "Unknown";

                if (errorCode != 0 || !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new DeviceIssue(name, manufacturer, errorCode, status));
                }
            }

            int healthyDevices = Math.Max(0, totalDevices - issues.Count);
            bool cpuRisk = issues.Any(issue => issue.ErrorCode is 10 or 14 or 28 or 31 or 37 or 43);
            string summary = issues.Count == 0
                ? $"No Device Manager issues detected across {totalDevices} devices."
                : $"{issues.Count} device issue(s) detected across {totalDevices} devices.";
            string recommendation = issues.Count == 0
                ? "Drivers look healthy. Prefer official vendor packages and keep automatic driver replacement disabled when chasing stability issues."
                : "Investigate devices with non-zero ConfigManagerErrorCode first. Reinstall official vendor drivers and avoid unsigned or generic packages during troubleshooting.";

            return new DriverHealthReport(
                totalDevices,
                healthyDevices,
                issues,
                summary,
                recommendation,
                cpuRisk);
        }
        catch (Exception ex)
        {
            return new DriverHealthReport(
                0,
                0,
                Array.Empty<DeviceIssue>(),
                "Device diagnostics unavailable.",
                $"WMI query failed: {ex.Message}",
                false);
        }
    }
}
