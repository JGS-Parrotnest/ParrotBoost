using System;
using System.Runtime.InteropServices;

namespace ParrotBoost;

internal sealed class WindowsCompatibilityProfile
{
    private const int Windows11Build = 22000;

    private WindowsCompatibilityProfile(bool isWindows, Version version, bool isWindows10)
    {
        IsWindows = isWindows;
        Version = version;
        IsWindows10 = isWindows && isWindows10;
        IsWindows11OrGreater = isWindows && !isWindows10;

        TelemetryRefreshInterval = IsWindows10 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1);
        TemperaturePollingInterval = IsWindows10 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1);
        PerformanceCounterCacheDuration = IsWindows10 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1);
        WmiCacheDuration = IsWindows10 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
        AlternativeGpuTemperatureCacheDuration = IsWindows10 ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5);
        AlternativeGpuProbeCooldown = IsWindows10 ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(8);
        ResourceTrimCooldown = IsWindows10 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10);
        SnapshotCaptureInterval = IsWindows10 ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(3);
        PowerPlanSwitchCooldown = IsWindows10 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10);
        OptimizationLogInterval = TimeSpan.FromSeconds(15);
        StartupTelemetryDelay = IsWindows10 ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(2);
        HardwareInventoryDelay = IsWindows10 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2);
        DriverUpdateDelay = IsWindows10 ? TimeSpan.FromSeconds(45) : TimeSpan.FromSeconds(8);
        DefenderProbeInterval = IsWindows10 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(3);
        DefenderBusyCpuThresholdPercent = IsWindows10 ? 2.5f : 5f;
        SnapshotRetentionLimit = IsWindows10 ? 8 : 16;
        DecompressionCacheLimit = IsWindows10 ? 8 : 16;
        EnableDetailedCpuCounters = !IsWindows10;
        EnableDetailedGpuCounters = !IsWindows10;
        EnableAggressiveWorkingSetTrim = !IsWindows10;
        SuspendTelemetryDuringDefenderScans = IsWindows10;
    }

    public static WindowsCompatibilityProfile Current { get; } = CreateCurrent();

    public bool IsWindows { get; }
    public Version Version { get; }
    public bool IsWindows10 { get; }
    public bool IsWindows11OrGreater { get; }
    public TimeSpan TelemetryRefreshInterval { get; }
    public TimeSpan TemperaturePollingInterval { get; }
    public TimeSpan PerformanceCounterCacheDuration { get; }
    public TimeSpan WmiCacheDuration { get; }
    public TimeSpan AlternativeGpuTemperatureCacheDuration { get; }
    public TimeSpan AlternativeGpuProbeCooldown { get; }
    public TimeSpan ResourceTrimCooldown { get; }
    public TimeSpan SnapshotCaptureInterval { get; }
    public TimeSpan PowerPlanSwitchCooldown { get; }
    public TimeSpan OptimizationLogInterval { get; }
    public TimeSpan StartupTelemetryDelay { get; }
    public TimeSpan HardwareInventoryDelay { get; }
    public TimeSpan DriverUpdateDelay { get; }
    public TimeSpan DefenderProbeInterval { get; }
    public float DefenderBusyCpuThresholdPercent { get; }
    public int SnapshotRetentionLimit { get; }
    public int DecompressionCacheLimit { get; }
    public bool EnableDetailedCpuCounters { get; }
    public bool EnableDetailedGpuCounters { get; }
    public bool EnableAggressiveWorkingSetTrim { get; }
    public bool SuspendTelemetryDuringDefenderScans { get; }

    public int GetRecommendedMinWorkerThreads(int physicalCoreCount)
    {
        int sanitized = Math.Max(1, physicalCoreCount);
        return IsWindows10 ? Math.Min(sanitized, 4) : sanitized;
    }

    public static WindowsCompatibilityProfile ForWindowsVersion(Version version)
    {
        bool isWindows10 = version.Major == 10 && version.Build > 0 && version.Build < Windows11Build;
        return new WindowsCompatibilityProfile(isWindows: true, version, isWindows10);
    }

    private static WindowsCompatibilityProfile CreateCurrent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsCompatibilityProfile(
                isWindows: false,
                version: Environment.OSVersion.Version,
                isWindows10: false);
        }

        return ForWindowsVersion(Environment.OSVersion.Version);
    }
}
