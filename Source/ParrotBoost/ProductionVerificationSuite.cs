using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;

namespace ParrotBoost;

internal static class ProductionVerificationSuite
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static void RunAll()
    {
        Logger.Info("Starting production verification suite.");

        VerifySnapshotIncludesCpuAndGpuSensors();
        VerifyHistoryRetention();
        VerifyCriticalTransitions();
        VerifyProviderFailureSurfacing();
        VerifyPollingPerformance();
        VerifyGpuSelectionHeuristics();
        VerifyWindowsCompatibilityProfiles();
        VerifyBoostPredictionHeuristics();

        Logger.Info("Production verification suite passed.");
    }

    private static void VerifySnapshotIncludesCpuAndGpuSensors()
    {
        var clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new QueueTemperatureProvider(
        [
            BuildResult(clock.UtcNow,
            [
                Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 62, clock.UtcNow),
                Sensor("cpu/core/0", "CPU", "Core #0", TemperatureOrigin.CpuCore, 59, clock.UtcNow, 0),
                Sensor("cpu/core/1", "CPU", "Core #1", TemperatureOrigin.CpuCore, 61, clock.UtcNow, 1),
                Sensor("gpu/0", "RTX 4070", "GPU Core", TemperatureOrigin.GpuCore, 54, clock.UtcNow, 0),
                Sensor("gpu/1", "Intel Arc", "GPU Core", TemperatureOrigin.GpuCore, 49, clock.UtcNow, 1)
            ])
        ]);

        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow, autoStartPolling: false);
        module.PollNow();

        var snapshot = module.GetCurrentTemperatures();
        Require(snapshot.CpuSensors.Count == 3, "Expected three CPU sensor readings.");
        Require(snapshot.GpuSensors.Count == 2, "Expected two GPU sensor readings.");
        Require(snapshot.Errors.Count == 0, "Expected no snapshot errors.");
    }

    private static void VerifyHistoryRetention()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new VerificationClock(start);
        using var provider = new QueueTemperatureProvider();
        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow, autoStartPolling: false);

        for (int i = 0; i < 75; i++)
        {
            provider.Enqueue(BuildResult(clock.UtcNow,
            [
                Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 50 + i % 10, clock.UtcNow)
            ]));

            module.PollNow();
            if (i < 74)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        var history = module.GetTemperatureHistory();
        Require(history.ContainsKey("cpu/package"), "Expected package temperature history.");
        Require(history["cpu/package"].Count >= 60 && history["cpu/package"].Count <= 61, "History retention window is outside the expected range.");
        Require(history["cpu/package"].All(point => clock.UtcNow - point.TimestampUtc <= TimeSpan.FromSeconds(60)), "History contains stale temperature samples.");
    }

    private static void VerifyCriticalTransitions()
    {
        var clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new QueueTemperatureProvider(
        [
            BuildResult(clock.UtcNow, [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 84, clock.UtcNow)]),
            BuildResult(clock.UtcNow.AddSeconds(1), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 86, clock.UtcNow.AddSeconds(1))]),
            BuildResult(clock.UtcNow.AddSeconds(2), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 87, clock.UtcNow.AddSeconds(2))]),
            BuildResult(clock.UtcNow.AddSeconds(3), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 82, clock.UtcNow.AddSeconds(3))]),
            BuildResult(clock.UtcNow.AddSeconds(4), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 88, clock.UtcNow.AddSeconds(4))])
        ]);

        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow, autoStartPolling: false);
        for (int i = 0; i < 5; i++)
        {
            module.PollNow();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var events = module.GetCriticalEvents();
        Require(events.Count == 2, "Expected exactly two critical threshold transitions.");
        Require(events.All(ev => ev.Value >= 85), "Critical event threshold was not enforced.");
    }

    private static void VerifyProviderFailureSurfacing()
    {
        var clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new ThrowingTemperatureProvider(new UnauthorizedAccessException("permission denied"));
        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow, autoStartPolling: false);

        module.PollNow();
        var snapshot = module.GetCurrentTemperatures();

        Require(snapshot.HasErrors, "Provider failures must surface in the snapshot.");
        Require(snapshot.Errors.Any(message => message.Contains("permission", StringComparison.OrdinalIgnoreCase)), "Expected permission failure to be exposed.");
        Require(snapshot.CpuSensors.Any(reading => reading.Status == TemperatureReadStatus.PermissionDenied), "Expected CPU permission denial marker.");
        Require(snapshot.GpuSensors.Any(reading => reading.Status == TemperatureReadStatus.PermissionDenied), "Expected GPU permission denial marker.");
    }

    private static void VerifyPollingPerformance()
    {
        var clock = new VerificationClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new QueueTemperatureProvider();
        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow, autoStartPolling: false);

        for (int i = 0; i < 120; i++)
        {
            provider.Enqueue(BuildResult(clock.UtcNow,
            [
                Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 60, clock.UtcNow),
                Sensor("cpu/core/0", "CPU", "Core #0", TemperatureOrigin.CpuCore, 58, clock.UtcNow, 0),
                Sensor("cpu/core/1", "CPU", "Core #1", TemperatureOrigin.CpuCore, 61, clock.UtcNow, 1),
                Sensor("gpu/0", "GPU", "GPU Core", TemperatureOrigin.GpuCore, 52, clock.UtcNow, 0)
            ]));
        }

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            module.PollNow();
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        stopwatch.Stop();

        Require(stopwatch.ElapsedMilliseconds < 500, $"Polling verification exceeded the expected budget: {stopwatch.ElapsedMilliseconds} ms.");
    }

    private static void VerifyGpuSelectionHeuristics()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var selectedPrimary = HardwareTelemetryService.SelectPrimaryGpuTemperature(
        [
            Sensor("gpu/hotspot", "AMD Radeon RX 7800 XT", "Hot Spot", TemperatureOrigin.GpuCore, 88, timestamp),
            Sensor("gpu/core", "AMD Radeon RX 7800 XT", "GPU Core", TemperatureOrigin.GpuCore, 71, timestamp),
            Sensor("gpu/memory", "AMD Radeon RX 7800 XT", "Memory Junction", TemperatureOrigin.GpuCore, 82, timestamp)
        ]);

        Require(selectedPrimary?.SensorName == "GPU Core", "Primary GPU selection should prefer the core sensor.");
        Require(selectedPrimary?.Celsius == 71, "Primary GPU selection returned an unexpected value.");

        var selectedGeneric = HardwareTelemetryService.SelectPrimaryGpuTemperature(
        [
            Sensor("gpu/generic", "NVIDIA GeForce RTX 4070", "GPU Temperature", TemperatureOrigin.GpuCore, 63, timestamp),
            Sensor("gpu/hotspot", "NVIDIA GeForce RTX 4070", "Hot Spot", TemperatureOrigin.GpuCore, 75, timestamp)
        ]);

        Require(selectedGeneric?.SensorName == "GPU Temperature", "Primary GPU selection should fall back to the generic GPU sensor.");
        Require(selectedGeneric?.Celsius == 63, "Generic GPU fallback returned an unexpected value.");

        float? validatedAlternative = HardwareTelemetryService.SelectValidatedGpuTemperature(
        [
            Sensor("gpu/hotspot", "AMD Radeon RX 7800 XT", "Hot Spot", TemperatureOrigin.GpuCore, 96, timestamp)
        ],
        Sensor("gpu/nvidia-smi", "AMD Radeon RX 7800 XT", "GPU Temperature", TemperatureOrigin.GpuCore, 71, timestamp),
        null,
        TimeSpan.MaxValue);

        Require(validatedAlternative == 71, "Alternative GPU validation did not win against an implausible hotspot reading.");

        float? validatedFallback = HardwareTelemetryService.SelectValidatedGpuTemperature(
        [
            Sensor("gpu/core", "NVIDIA GeForce RTX 4070", "GPU Core", TemperatureOrigin.GpuCore, 95, timestamp)
        ],
        null,
        64,
        TimeSpan.FromSeconds(2));

        Require(validatedFallback == 64, "Previous GPU reading should have been retained after a large jump.");
    }

    private static void VerifyWindowsCompatibilityProfiles()
    {
        var windows10 = WindowsCompatibilityProfile.ForWindowsVersion(new Version(10, 0, 19045));
        Require(windows10.IsWindows10, "Windows 10 profile detection failed.");
        Require(!windows10.IsWindows11OrGreater, "Windows 10 profile should not identify as Windows 11.");
        Require(windows10.TelemetryRefreshInterval == TimeSpan.FromSeconds(2), "Unexpected Windows 10 telemetry interval.");
        Require(windows10.WmiCacheDuration == TimeSpan.FromSeconds(5), "Unexpected Windows 10 WMI cache duration.");
        Require(windows10.StartupTelemetryDelay == TimeSpan.FromSeconds(8), "Unexpected Windows 10 telemetry startup delay.");
        Require(windows10.HardwareInventoryDelay == TimeSpan.FromSeconds(10), "Unexpected Windows 10 hardware inventory delay.");
        Require(windows10.DriverUpdateDelay == TimeSpan.FromSeconds(45), "Unexpected Windows 10 driver-update delay.");
        Require(windows10.SuspendTelemetryDuringDefenderScans, "Windows 10 profile should suspend expensive telemetry during Defender scans.");
        Require(!windows10.EnableDetailedGpuCounters, "Windows 10 profile should disable detailed GPU counters.");
        Require(!windows10.EnableAggressiveWorkingSetTrim, "Windows 10 profile should disable aggressive trimming.");
        Require(windows10.GetRecommendedMinWorkerThreads(8) == 4, "Unexpected Windows 10 worker-thread cap.");

        var windows11 = WindowsCompatibilityProfile.ForWindowsVersion(new Version(10, 0, 22631));
        Require(!windows11.IsWindows10, "Windows 11 profile detection failed.");
        Require(windows11.IsWindows11OrGreater, "Windows 11 profile should identify as Windows 11 or greater.");
        Require(windows11.TelemetryRefreshInterval == TimeSpan.FromSeconds(1), "Unexpected Windows 11 telemetry interval.");
        Require(windows11.WmiCacheDuration == TimeSpan.FromSeconds(2), "Unexpected Windows 11 WMI cache duration.");
        Require(windows11.StartupTelemetryDelay == TimeSpan.FromSeconds(2), "Unexpected Windows 11 telemetry startup delay.");
        Require(!windows11.SuspendTelemetryDuringDefenderScans, "Windows 11 profile should not enable Defender throttling by default.");
        Require(windows11.EnableDetailedGpuCounters, "Windows 11 profile should keep detailed GPU counters.");
        Require(windows11.EnableAggressiveWorkingSetTrim, "Windows 11 profile should keep aggressive trimming.");
        Require(windows11.GetRecommendedMinWorkerThreads(8) == 8, "Unexpected Windows 11 worker-thread recommendation.");
    }

    private static void VerifyBoostPredictionHeuristics()
    {
        var aggressivePlan = new BoostPlanConfiguration(
            optServices: true,
            optMemory: true,
            optTasks: true,
            optNtfs: true,
            optPriority: true,
            optUsb: true,
            optDelivery: true,
            optTick: true,
            enableGameMode: true);

        var strongCpuPressure = BoostImpactPredictor.PredictCore(
            aggressivePlan,
            new BoostPredictionEnvironment(215, 84, true),
            cpuLoad: 88,
            gpuLoad: 52,
            cpuTemp: 72,
            gpuTemp: 64);

        var gpuBoundLightCpu = BoostImpactPredictor.PredictCore(
            aggressivePlan,
            new BoostPredictionEnvironment(150, 62, true),
            cpuLoad: 32,
            gpuLoad: 96,
            cpuTemp: 66,
            gpuTemp: 73);

        Require(strongCpuPressure.MaximumGainPercent > gpuBoundLightCpu.MaximumGainPercent, "CPU-bound background pressure should predict more gain than a mostly GPU-bound workload.");
        Require(gpuBoundLightCpu.MaximumGainPercent <= 6, "GPU-bound workloads should not receive an inflated boost estimate.");
        Require(strongCpuPressure.ConfidencePercent >= 60, "Strong CPU-pressure prediction should retain usable confidence.");
    }

    private static TemperatureProviderReadResult BuildResult(DateTimeOffset timestamp, IReadOnlyList<TemperatureSensorReading> readings)
    {
        return new TemperatureProviderReadResult
        {
            Readings = readings.Select(reading => reading.WithTimestamp(timestamp)).ToArray()
        };
    }

    private static TemperatureSensorReading Sensor(
        string id,
        string deviceName,
        string sensorName,
        TemperatureOrigin origin,
        float value,
        DateTimeOffset timestamp,
        int? index = null)
    {
        return new TemperatureSensorReading(id, deviceName, sensorName, origin, value, timestamp, TemperatureReadStatus.Ok, index: index);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class VerificationClock
    {
        public VerificationClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan delta)
        {
            UtcNow = UtcNow.Add(delta);
        }
    }

    private sealed class QueueTemperatureProvider : ITemperatureProvider
    {
        private readonly Queue<TemperatureProviderReadResult> _results = new();

        public QueueTemperatureProvider()
        {
        }

        public QueueTemperatureProvider(IEnumerable<TemperatureProviderReadResult> initialResults)
        {
            foreach (var result in initialResults)
            {
                _results.Enqueue(result);
            }
        }

        public string ProviderName => "ProductionVerificationProvider";

        public void Enqueue(TemperatureProviderReadResult result)
        {
            _results.Enqueue(result);
        }

        public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
        {
            if (_results.Count == 0)
            {
                return new TemperatureProviderReadResult
                {
                    Errors = ["No queued readings."]
                };
            }

            return _results.Dequeue();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingTemperatureProvider : ITemperatureProvider
    {
        private readonly Exception _exception;

        public ThrowingTemperatureProvider(Exception exception)
        {
            _exception = exception;
        }

        public string ProviderName => "ProductionVerificationProvider";

        public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
        {
            throw _exception;
        }

        public void Dispose()
        {
        }
    }
}
