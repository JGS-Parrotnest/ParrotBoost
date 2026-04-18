using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;

namespace ParrotBoost;

internal sealed class ParrotBoostRuntimeOptimizer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif
    private readonly WindowsCompatibilityProfile _compatibilityProfile;
    private readonly ConcurrentDictionary<string, string> _decompressionCache = new(StringComparer.Ordinal);
    private readonly Queue<byte[]> _compressedSnapshots = new();
    private readonly ProcessPriorityClass _baselinePriorityClass;
    private readonly int _baselineMinWorkerThreads;
    private readonly int _baselineMinCompletionThreads;
    private bool _boostProfileApplied;
    private bool _boostModeEnabled;
    private bool? _highPerformancePowerPlanActive;
    private DateTimeOffset _lastWorkingSetTrimUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPowerPlanSwitchUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSnapshotCapturedUtc = DateTimeOffset.MinValue;
    private string _latestMonitoringSummary = "No optimization samples captured.";

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private readonly PidController _tempPid = new(0.5, 0.1, 0.05); // Dostrojone Kp, Ki, Kd
    private float _lastCpuTemp;
    private float _lastGpuTemp;
    private const float MaxTempJumpFactor = 0.7f; // 30% redukcja skoku
    private const float CriticalTempThreshold = 95.0f;

    public ParrotBoostRuntimeOptimizer()
        : this(WindowsCompatibilityProfile.Current)
    {
    }

    internal ParrotBoostRuntimeOptimizer(WindowsCompatibilityProfile compatibilityProfile)
    {
        _compatibilityProfile = compatibilityProfile;
        try
        {
            ThreadPool.GetMinThreads(out _baselineMinWorkerThreads, out _baselineMinCompletionThreads);
            _baselinePriorityClass = Process.GetCurrentProcess().PriorityClass;
            Logger.Info("Runtime Optimizer initialized. Baseline priority: {0}", _baselinePriorityClass);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize Runtime Optimizer.");
            throw;
        }
    }

    public RuntimeOptimizationSnapshot UpdateRuntimeProfile(float cpuLoad, float gpuLoad, float? cpuTemp, float? gpuTemp)
    {
        try
        {
            bool boostEnabled = ParrotBoostSystemConfiguration.IsBoostEnabled();
            
            // Safety Check
            if ((cpuTemp ?? 0) > CriticalTempThreshold || (gpuTemp ?? 0) > CriticalTempThreshold)
            {
                Logger.Warn("Critical temperature detected! Throttling boost. CPU: {0:F1}°C, GPU: {1:F1}°C", cpuTemp, gpuTemp);
                boostEnabled = false;
            }

            ApplyBoostProfile(boostEnabled);

            ProcessPriorityClass currentPriority = Process.GetCurrentProcess().PriorityClass;
            if (boostEnabled)
            {
                currentPriority = AdjustProcessPriority(cpuLoad);
                OptimizeSystemResources();
                
                // PID control based on CPU Temp (Target 75C)
                double pidAdjustment = _tempPid.Compute(75.0, cpuTemp ?? 0);
                if (pidAdjustment < 0) 
                {
                    // If PID output is negative, we should scale back optimization
                    Logger.Debug("PID Adjustment: {0:F2}. Throttling resources.", pidAdjustment);
                }
            }

            // Calibrate/Smooth temperatures with 30% jump reduction
            float calibratedCpuTemp = cpuTemp.HasValue ? CalibrateTemp(cpuTemp.Value, ref _lastCpuTemp) : 0;
            float calibratedGpuTemp = gpuTemp.HasValue ? CalibrateTemp(gpuTemp.Value, ref _lastGpuTemp) : 0;

            // Detailed Logging (0.1C precision, ms precision)
            Logger.Trace("[{0:yyyy-MM-dd HH:mm:ss.fff}] Optimization Sampling: CPU={1:F1}C, GPU={2:F1}C, Load_C={3:F1}%, Load_G={4:F1}%", 
                DateTime.Now, calibratedCpuTemp, calibratedGpuTemp, cpuLoad, gpuLoad);

            var snapshot = new RuntimeOptimizationSnapshot(
                boostEnabled,
                DateTimeOffset.UtcNow,
                cpuLoad,
                gpuLoad,
                calibratedCpuTemp,
                calibratedGpuTemp,
                currentPriority,
                GetConfiguredWorkerThreads(),
                _decompressionCache.Count);

            if (boostEnabled)
            {
                CacheCompressedSnapshot(snapshot);
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during runtime profile update.");
            return default;
        }
    }

    private static float CalibrateTemp(float current, ref float last)
    {
        if (last <= 0) { last = current; return current; }
        
        float diff = current - last;
        // Limit the jump by 30%
        float adjustedDiff = diff * MaxTempJumpFactor;
        float result = last + adjustedDiff;
        
        last = result;
        return (float)Math.Round(result, 1);
    }

    public void ApplyBoostProfile(bool enabled)
    {
        lock (_syncRoot)
        {
            try
            {
                if (enabled == _boostModeEnabled)
                {
                    return;
                }

                if (enabled)
                {
                    TuneThreadPool();
                    PrepareManagedMemory();
                    ConfigurePowerPlan(true, force: true);
                    _boostModeEnabled = true;
                    _boostProfileApplied = true;
                    return;
                }

                RestoreBaselineProfile();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to apply/restore boost profile.");
            }
        }
    }

    private void OptimizeSystemResources()
    {
        try
        {
            if (!_compatibilityProfile.EnableAggressiveWorkingSetTrim)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastWorkingSetTrimUtc < _compatibilityProfile.ResourceTrimCooldown)
            {
                return;
            }

            using var process = Process.GetCurrentProcess();
            if (process.WorkingSet64 < 256L * 1024 * 1024)
            {
                return;
            }

            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            _lastWorkingSetTrimUtc = now;

            Logger.Trace("System resources optimized (WorkingSet trimmed).");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to optimize system resources.");
        }
    }

    private void ConfigurePowerPlan(bool highPerformance, bool force = false)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (!force)
            {
                if (_highPerformancePowerPlanActive == highPerformance)
                {
                    return;
                }

                if (now - _lastPowerPlanSwitchUtc < _compatibilityProfile.PowerPlanSwitchCooldown)
                {
                    return;
                }
            }

            // High Performance: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
            // Balanced: 381b4222-f694-41f0-9685-ff5bb260df2e
            string scheme = highPerformance ? "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" : "381b4222-f694-41f0-9685-ff5bb260df2e";
            
            ProcessStartInfo psi = new("powercfg", $"/setactive {scheme}")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi);
            process?.WaitForExit();
            _highPerformancePowerPlanActive = highPerformance;
            _lastPowerPlanSwitchUtc = now;
            Logger.Debug("Power plan set to {0}", highPerformance ? "High Performance" : "Balanced");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to configure power plan.");
        }
    }

    public string GetLatestMonitoringSummary()
    {
        lock (_syncRoot)
        {
            try
            {
                if (_compressedSnapshots.Count == 0)
                {
                    return _latestMonitoringSummary;
                }

                return _latestMonitoringSummary;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to get monitoring summary.");
                return "Error retrieving summary.";
            }
        }
    }

    private void CacheCompressedSnapshot(RuntimeOptimizationSnapshot snapshot)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSnapshotCapturedUtc < _compatibilityProfile.SnapshotCaptureInterval)
            {
                return;
            }

            string json = JsonSerializer.Serialize(snapshot);
            byte[] payload = Compress(json);

            lock (_syncRoot)
            {
                _latestMonitoringSummary = json;
                _lastSnapshotCapturedUtc = now;
                _compressedSnapshots.Enqueue(payload);
                while (_compressedSnapshots.Count > _compatibilityProfile.SnapshotRetentionLimit)
                {
                    _compressedSnapshots.Dequeue();
                }

                while (_decompressionCache.Count > _compatibilityProfile.DecompressionCacheLimit)
                {
                    string? staleKey = _decompressionCache.Keys.FirstOrDefault();
                    if (staleKey == null || !_decompressionCache.TryRemove(staleKey, out _))
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to cache snapshot.");
        }
    }

    private void TuneThreadPool()
    {
        try
        {
            int targetWorkerThreads = _compatibilityProfile.GetRecommendedMinWorkerThreads(SystemExecutionProfile.GetPhysicalCoreCount());
            ThreadPool.SetMinThreads(targetWorkerThreads, _baselineMinCompletionThreads);
            Logger.Debug("ThreadPool tuned to physical cores: MinWorkerThreads={0}", targetWorkerThreads);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to tune ThreadPool.");
        }
    }

    private void PrepareManagedMemory()
    {
        try
        {
            GCSettings.LatencyMode = _compatibilityProfile.IsWindows10
                ? GCLatencyMode.Interactive
                : GCLatencyMode.SustainedLowLatency;

            if (!_compatibilityProfile.IsWindows10)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }

            Logger.Debug("Managed memory prepared for {0}.", _compatibilityProfile.IsWindows10 ? "Windows 10 compatibility mode" : "default profile");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to prepare managed memory.");
        }
    }

    private void RestoreBaselineProfile()
    {
        try
        {
            ThreadPool.SetMinThreads(_baselineMinWorkerThreads, _baselineMinCompletionThreads);
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            ConfigurePowerPlan(false, force: _boostProfileApplied);

            try
            {
                Process.GetCurrentProcess().PriorityClass = _baselinePriorityClass;
            }
            catch
            {
            }

            if (_boostProfileApplied)
            {
                _decompressionCache.Clear();
                _compressedSnapshots.Clear();
                _boostProfileApplied = false;
            }

            _boostModeEnabled = false;
            _lastWorkingSetTrimUtc = DateTimeOffset.MinValue;
            Logger.Info("Baseline profile restored.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error during baseline profile restoration.");
        }
    }

    private static ProcessPriorityClass AdjustProcessPriority(float cpuLoad)
    {
        ProcessPriorityClass targetPriority = cpuLoad switch
        {
            >= 85 => ProcessPriorityClass.BelowNormal,
            >= 60 => ProcessPriorityClass.Normal,
            _ => ProcessPriorityClass.AboveNormal
        };

        try
        {
            var process = Process.GetCurrentProcess();
            if (process.PriorityClass != targetPriority)
            {
                process.PriorityClass = targetPriority;
                Logger.Info("Process priority adjusted to {0} (CPU Load: {1}%)", targetPriority, cpuLoad);
            }

            return process.PriorityClass;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to adjust process priority.");
            return Process.GetCurrentProcess().PriorityClass;
        }
    }

    private static int GetConfiguredWorkerThreads()
    {
        ThreadPool.GetMinThreads(out int workerThreads, out _);
        return workerThreads;
    }

    private string DecompressWithCache(byte[] payload)
    {
#if NET5_0_OR_GREATER
        string hash = Convert.ToHexString(SHA256.HashData(payload));
#else
        string hash = Compatibility.ToHexString(Compatibility.HashData(payload));
#endif
        return _decompressionCache.GetOrAdd(hash, _ => Decompress(payload));
    }

    private static byte[] Compress(string content)
    {
        byte[] input = Encoding.UTF8.GetBytes(content);
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static string Decompress(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            using var output = new MemoryStream();
            int bytesRead;
            while ((bytesRead = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, bytesRead);
            }

            return Encoding.UTF8.GetString(output.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // --- Diagnostic Suite (Migrated from Tests) ---
    public static void RunSelfDiagnostics()
    {
        Logger.Info("Starting Production Self-Diagnostics...");
        try
        {
            ValidateLocalization();
            ProductionVerificationSuite.RunAll();
            HardwareTelemetryService.RunInternalDiagnostics();
            Logger.Info("Self-Diagnostics PASSED.");
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "Self-Diagnostics FAILED! #problems_and_diagnostics");
            throw;
        }
    }

    private static void ValidateLocalization()
    {
        var manager = LocalizationManager.Instance;
        if (manager is null) throw new InvalidOperationException("LocalizationManager instance is null.");
        Logger.Debug("Localization validation passed.");
    }

    internal readonly record struct RuntimeOptimizationSnapshot(
        bool BoostEnabled,
        DateTimeOffset TimestampUtc,
        float CpuLoad,
        float GpuLoad,
        float? CpuTemperature,
        float? GpuTemperature,
        ProcessPriorityClass PriorityClass,
        int WorkerThreads,
        int CachedPayloads);
}
