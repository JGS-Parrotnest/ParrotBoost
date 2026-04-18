using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;

namespace ParrotBoost;

internal sealed class BoostImpactPredictor
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan EnvironmentCacheDuration = TimeSpan.FromSeconds(15);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

#if NET9_0_OR_GREATER
    private readonly Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif

    private BoostPredictionEnvironment _cachedEnvironment = BoostPredictionEnvironment.Unknown;
    private DateTimeOffset _lastEnvironmentRefreshUtc = DateTimeOffset.MinValue;

    public BoostImpactPrediction Predict(
        BoostPlanConfiguration plan,
        float cpuLoad,
        float gpuLoad,
        float? cpuTemp,
        float? gpuTemp)
    {
        return PredictCore(plan, GetEnvironment(), cpuLoad, gpuLoad, cpuTemp, gpuTemp);
    }

    internal static BoostImpactPrediction PredictCore(
        BoostPlanConfiguration plan,
        BoostPredictionEnvironment environment,
        float cpuLoad,
        float gpuLoad,
        float? cpuTemp,
        float? gpuTemp)
    {
        cpuLoad = Compatibility.Clamp(cpuLoad, 0, 100);
        gpuLoad = Compatibility.Clamp(gpuLoad, 0, 100);

        int enabledStepCount = CountEnabledSteps(plan);
        if (enabledStepCount == 0)
        {
            return new BoostImpactPrediction(
                0,
                0,
                90,
                "No boost steps are enabled.",
                "Enable at least one optimization step to estimate a gain.");
        }

        double cpuOpportunity = ScoreCpuOpportunity(cpuLoad);
        double backgroundOpportunity = ScoreBackgroundOpportunity(environment.ProcessCount);
        double memoryOpportunity = plan.OptMemory ? ScoreMemoryOpportunity(environment.MemoryLoadPercent) : 0;
        double thermalFactor = ScoreThermalFactor(cpuTemp, gpuTemp);
        double gpuPenalty = ScoreGpuBoundPenalty(cpuLoad, gpuLoad);
        double workloadFactor = ScoreWorkloadFactor(cpuLoad, gpuLoad);

        double estimate = 0;
        if (plan.EnableGameMode) estimate += 1.8 * cpuOpportunity;
        if (plan.OptPriority) estimate += 2.4 * cpuOpportunity;
        if (plan.OptTick) estimate += 1.1 * cpuOpportunity;
        if (plan.OptTasks) estimate += 1.0 * backgroundOpportunity;
        if (plan.OptServices) estimate += 0.8 * backgroundOpportunity;
        if (plan.OptDelivery) estimate += 0.35 * backgroundOpportunity;
        if (plan.OptMemory) estimate += memoryOpportunity;
        if (plan.OptUsb) estimate += 0.35 * workloadFactor;
        if (plan.OptNtfs) estimate += 0.25 * workloadFactor;

        estimate *= thermalFactor;
        estimate *= gpuPenalty;
        estimate *= workloadFactor;

        if (!environment.IsAdministrator)
        {
            estimate *= 0.65;
        }

        estimate = Math.Min(16, Math.Max(0, estimate));

        int confidence = 76;
        if (environment.ProcessCount <= 0) confidence -= 8;
        if (!environment.MemoryLoadPercent.HasValue) confidence -= 10;
        if (!cpuTemp.HasValue && !gpuTemp.HasValue) confidence -= 12;
        if (!environment.IsAdministrator) confidence -= 18;
        if (cpuLoad >= 70) confidence += 6;
        if (backgroundOpportunity >= 2.5) confidence += 5;
        if (thermalFactor < 0.5) confidence -= 10;
        confidence = Math.Max(25, Math.Min(92, confidence));

        if (estimate < 0.75)
        {
            return new BoostImpactPrediction(
                0,
                Math.Max(1, enabledStepCount >= 4 ? 2 : 1),
                confidence,
                BuildSummary(cpuLoad, gpuLoad, cpuTemp, gpuTemp, environment, estimate),
                BuildDetails(cpuLoad, gpuLoad, environment, confidence));
        }

        double spread = confidence switch
        {
            >= 85 => 1.5,
            >= 70 => 2.5,
            >= 55 => 3.5,
            _ => 4.5
        };

        int min = (int)Math.Floor(Math.Max(0, estimate - spread));
        int max = (int)Math.Ceiling(Math.Min(18, estimate + spread));
        if (max < min)
        {
            max = min;
        }

        return new BoostImpactPrediction(
            min,
            max,
            confidence,
            BuildSummary(cpuLoad, gpuLoad, cpuTemp, gpuTemp, environment, estimate),
            BuildDetails(cpuLoad, gpuLoad, environment, confidence));
    }

    private BoostPredictionEnvironment GetEnvironment()
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastEnvironmentRefreshUtc < EnvironmentCacheDuration)
            {
                return _cachedEnvironment;
            }

            _cachedEnvironment = CaptureEnvironment();
            _lastEnvironmentRefreshUtc = now;
            return _cachedEnvironment;
        }
    }

    private static BoostPredictionEnvironment CaptureEnvironment()
    {
        int processCount = 0;
        float? memoryLoadPercent = null;

        try
        {
            processCount = Process.GetProcesses().Length;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sample process count for boost prediction.");
        }

        try
        {
            var memoryStatus = new MEMORYSTATUSEX();
            memoryStatus.Init();
            if (GlobalMemoryStatusEx(ref memoryStatus))
            {
                memoryLoadPercent = memoryStatus.dwMemoryLoad;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sample memory load for boost prediction.");
        }

        bool isAdministrator = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            isAdministrator = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to determine elevation state for boost prediction.");
        }

        return new BoostPredictionEnvironment(processCount, memoryLoadPercent, isAdministrator);
    }

    private static int CountEnabledSteps(BoostPlanConfiguration plan)
    {
        int count = 0;
        if (plan.OptServices) count++;
        if (plan.OptMemory) count++;
        if (plan.OptTasks) count++;
        if (plan.OptNtfs) count++;
        if (plan.OptPriority) count++;
        if (plan.OptUsb) count++;
        if (plan.OptDelivery) count++;
        if (plan.OptTick) count++;
        if (plan.EnableGameMode) count++;
        return count;
    }

    private static double ScoreCpuOpportunity(float cpuLoad)
    {
        return cpuLoad switch
        {
            >= 90 => 1.0,
            >= 75 => 0.85,
            >= 60 => 0.65,
            >= 45 => 0.4,
            >= 30 => 0.2,
            _ => 0.08
        };
    }

    private static double ScoreBackgroundOpportunity(int processCount)
    {
        return processCount switch
        {
            >= 240 => 3.5,
            >= 210 => 2.7,
            >= 180 => 2.0,
            >= 150 => 1.2,
            >= 120 => 0.6,
            > 0 => 0.2,
            _ => 0
        };
    }

    private static double ScoreMemoryOpportunity(float? memoryLoadPercent)
    {
        if (!memoryLoadPercent.HasValue)
        {
            return 0;
        }

        return memoryLoadPercent.Value switch
        {
            >= 90 => 3.2,
            >= 82 => 2.2,
            >= 72 => 1.2,
            >= 62 => 0.4,
            _ => 0
        };
    }

    private static double ScoreThermalFactor(float? cpuTemp, float? gpuTemp)
    {
        float hottest = Math.Max(cpuTemp ?? 0, gpuTemp ?? 0);
        if (hottest <= 0)
        {
            return 0.8;
        }

        return hottest switch
        {
            >= 90 => 0.18,
            >= 85 => 0.4,
            >= 80 => 0.68,
            >= 75 => 0.85,
            _ => 1.0
        };
    }

    private static double ScoreGpuBoundPenalty(float cpuLoad, float gpuLoad)
    {
        if (gpuLoad >= 95 && cpuLoad < 55)
        {
            return 0.25;
        }

        if (gpuLoad >= 90 && cpuLoad < 60)
        {
            return 0.45;
        }

        if (gpuLoad >= 80 && cpuLoad < 55)
        {
            return 0.7;
        }

        return 1.0;
    }

    private static double ScoreWorkloadFactor(float cpuLoad, float gpuLoad)
    {
        if (cpuLoad < 25 && gpuLoad < 25)
        {
            return 0.15;
        }

        if (cpuLoad < 35 && gpuLoad < 35)
        {
            return 0.3;
        }

        if (cpuLoad < 45 && gpuLoad < 45)
        {
            return 0.55;
        }

        return 1.0;
    }

    private static string BuildSummary(
        float cpuLoad,
        float gpuLoad,
        float? cpuTemp,
        float? gpuTemp,
        BoostPredictionEnvironment environment,
        double estimate)
    {
        float hottest = Math.Max(cpuTemp ?? 0, gpuTemp ?? 0);

        if (!environment.IsAdministrator)
        {
            return "Run as administrator for a trustworthy boost estimate.";
        }

        if (hottest >= 85)
        {
            return "Thermal headroom is poor, so boost potential is limited.";
        }

        if (cpuLoad < 35 && gpuLoad < 35)
        {
            return "Current workload is light; any gain will be hard to notice.";
        }

        if (gpuLoad >= 90 && cpuLoad < 60)
        {
            return "This looks GPU-bound, so Windows tweaks should only help a little.";
        }

        if (environment.MemoryLoadPercent is >= 80 && estimate >= 3)
        {
            return "Memory pressure is high enough for cleanup and scheduling to help.";
        }

        if (cpuLoad >= 70 && environment.ProcessCount >= 170)
        {
            return "CPU pressure and background churn suggest a measurable gain.";
        }

        if (cpuLoad >= 65)
        {
            return "This looks CPU-limited enough for a moderate real gain.";
        }

        return "Only a small but plausible gain is visible from live system pressure.";
    }

    private static string BuildDetails(
        float cpuLoad,
        float gpuLoad,
        BoostPredictionEnvironment environment,
        int confidence)
    {
        string memoryLoad = environment.MemoryLoadPercent.HasValue
            ? $"{Math.Round(environment.MemoryLoadPercent.Value, MidpointRounding.AwayFromZero):0}% RAM load"
            : "RAM load unavailable";
        string processCount = environment.ProcessCount > 0
            ? $"{environment.ProcessCount} processes"
            : "Process count unavailable";

        return $"Confidence {confidence}% | CPU {cpuLoad:0}% | GPU {gpuLoad:0}% | {memoryLoad} | {processCount}";
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public void Init()
    {
        dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
    }
}

internal readonly struct BoostPlanConfiguration
{
    public BoostPlanConfiguration(
        bool optServices,
        bool optMemory,
        bool optTasks,
        bool optNtfs,
        bool optPriority,
        bool optUsb,
        bool optDelivery,
        bool optTick,
        bool enableGameMode)
    {
        OptServices = optServices;
        OptMemory = optMemory;
        OptTasks = optTasks;
        OptNtfs = optNtfs;
        OptPriority = optPriority;
        OptUsb = optUsb;
        OptDelivery = optDelivery;
        OptTick = optTick;
        EnableGameMode = enableGameMode;
    }

    public bool OptServices { get; }
    public bool OptMemory { get; }
    public bool OptTasks { get; }
    public bool OptNtfs { get; }
    public bool OptPriority { get; }
    public bool OptUsb { get; }
    public bool OptDelivery { get; }
    public bool OptTick { get; }
    public bool EnableGameMode { get; }
}

internal readonly struct BoostPredictionEnvironment
{
    public static readonly BoostPredictionEnvironment Unknown = new(0, null, false);

    public BoostPredictionEnvironment(int processCount, float? memoryLoadPercent, bool isAdministrator)
    {
        ProcessCount = processCount;
        MemoryLoadPercent = memoryLoadPercent;
        IsAdministrator = isAdministrator;
    }

    public int ProcessCount { get; }
    public float? MemoryLoadPercent { get; }
    public bool IsAdministrator { get; }
}

internal readonly struct BoostImpactPrediction
{
    public static readonly BoostImpactPrediction Pending = new(-1, -1, 0, "Sampling live bottlenecks for a realistic estimate.", "Waiting for telemetry...");

    public BoostImpactPrediction(
        int minimumGainPercent,
        int maximumGainPercent,
        int confidencePercent,
        string summary,
        string details)
    {
        MinimumGainPercent = minimumGainPercent;
        MaximumGainPercent = maximumGainPercent;
        ConfidencePercent = confidencePercent;
        Summary = summary;
        Details = details;
    }

    public int MinimumGainPercent { get; }
    public int MaximumGainPercent { get; }
    public int ConfidencePercent { get; }
    public string Summary { get; }
    public string Details { get; }

    public string RangeLabel
    {
        get
        {
            if (MinimumGainPercent < 0 || MaximumGainPercent < 0)
            {
                return "Analyzing";
            }

            if (MaximumGainPercent <= 0)
            {
                return "0%";
            }

            if (MinimumGainPercent <= 0)
            {
                return $"0-{MaximumGainPercent}%";
            }

            if (MinimumGainPercent == MaximumGainPercent)
            {
                return $"+{MinimumGainPercent}%";
            }

            return $"+{MinimumGainPercent}-{MaximumGainPercent}%";
        }
    }
}
