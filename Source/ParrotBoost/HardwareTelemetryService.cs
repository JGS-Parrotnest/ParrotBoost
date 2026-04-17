using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using NLog;

namespace ParrotBoost;

internal sealed class HardwareTelemetryService : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan PerfCounterCacheDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WmiCacheDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GpuFallbackRetention = TimeSpan.FromSeconds(5);
    private const float MaxUncorroboratedGpuJumpCelsius = 25f;

#if NET9_0_OR_GREATER
    private readonly Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif

    private TemperatureMonitoringModule? _temperatureModule;
    private readonly ManagementScope _cimv2Scope = new(@"root\CIMV2");
    private readonly List<PerformanceCounter> _cpuCoreCounters = [];
    private readonly List<PerformanceCounter> _gpuLoadCounters = [];
    private readonly List<PerformanceCounter> _vramCounters = [];
    private readonly Dictionary<string, (float Value, DateTime Timestamp)> _cache = [];
    private PerformanceCounter? _cpuCounter;
    private bool _countersInitialized;
    private bool _scopesConnected;
    private bool _isDisposed;
    private float? _lastAcceptedGpuTemperature;
    private DateTimeOffset _lastAcceptedGpuTemperatureTimestampUtc = DateTimeOffset.MinValue;

    public HardwareTelemetryService()
    {
    }

    public event Action<CriticalTemperatureEvent> CriticalTemperatureDetected
    {
        add => EnsureTemperatureModule().CriticalTemperatureDetected += value;
        remove
        {
            if (_temperatureModule != null)
            {
                _temperatureModule.CriticalTemperatureDetected -= value;
            }
        }
    }

    public TemperatureSnapshot GetCurrentTemperatures() => EnsureTemperatureModule().GetCurrentTemperatures();
    public TemperatureSnapshot getCurrentTemperatures() => GetCurrentTemperatures();
    public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> GetTemperatureHistory() => EnsureTemperatureModule().GetTemperatureHistory();
    public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> getTemperatureHistory() => GetTemperatureHistory();
    public IReadOnlyList<CriticalTemperatureEvent> GetCriticalEvents() => EnsureTemperatureModule().GetCriticalEvents();
    public IReadOnlyList<CriticalTemperatureEvent> getCriticalEvents() => GetCriticalEvents();

    private void EnsureScopesConnected()
    {
        if (_scopesConnected || _isDisposed)
        {
            return;
        }

        try
        {
            _cimv2Scope.Connect();
            _scopesConnected = _cimv2Scope.IsConnected;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "CIMV2 scope connection failed.");
        }
    }

    private TemperatureMonitoringModule EnsureTemperatureModule()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(HardwareTelemetryService));
            }

            _temperatureModule ??= new TemperatureMonitoringModule();
            return _temperatureModule;
        }
    }

    private void EnsureCountersInitialized()
    {
        if (_countersInitialized || _isDisposed)
        {
            return;
        }

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();

            var cpuCategory = new PerformanceCounterCategory("Processor");
            foreach (var instance in cpuCategory.GetInstanceNames().Where(i => i != "_Total" && int.TryParse(i, out _)))
            {
                var counter = new PerformanceCounter("Processor", "% Processor Time", instance);
                counter.NextValue();
                _cpuCoreCounters.Add(counter);
            }

            var gpuCategory = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in gpuCategory.GetInstanceNames().Where(i => i.Contains("engtype_", StringComparison.OrdinalIgnoreCase)))
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                counter.NextValue();
                _gpuLoadCounters.Add(counter);
            }

            var vramCategory = new PerformanceCounterCategory("GPU Adapter Memory");
            foreach (var instance in vramCategory.GetInstanceNames())
            {
                var counter = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance);
                counter.NextValue();
                _vramCounters.Add(counter);
            }

            _countersInitialized = true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize telemetry counters.");
        }
    }

    private bool TryGetCachedValue(string key, out float value)
    {
        return TryGetCachedValue(key, PerfCounterCacheDuration, out value);
    }

    private bool TryGetCachedValue(string key, TimeSpan cacheDuration, out float value)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.Now - entry.Timestamp < cacheDuration)
        {
            value = entry.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private void SetCachedValue(string key, float value)
    {
        _cache[key] = (value, DateTime.Now);
    }

    public float? TryGetCpuClockSpeed()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            EnsureScopesConnected();
            if (!_cimv2Scope.IsConnected)
            {
                return null;
            }

            if (TryGetCachedValue("CpuClock", WmiCacheDuration, out float cached))
            {
                return cached;
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(_cimv2Scope, new ObjectQuery("SELECT CurrentClockSpeed FROM Win32_Processor"));
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    float value = Convert.ToSingle(obj["CurrentClockSpeed"]);
                    if (value > 0 && value < 10000)
                    {
                        SetCachedValue("CpuClock", value);
                        return value;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Trace(ex, "Failed to read CPU clock speed.");
            }

            return null;
        }
    }

    public float[] TryGetCpuLoadPerCore()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return [];
            }

            EnsureCountersInitialized();
            var values = new List<float>(_cpuCoreCounters.Count);
            foreach (var counter in _cpuCoreCounters)
            {
                try
                {
                    values.Add(Compatibility.Clamp(counter.NextValue(), 0, 100));
                }
                catch
                {
                }
            }

            return values.ToArray();
        }
    }

    public float? TryGetCpuTemperature()
    {
        var snapshot = EnsureTemperatureModule().GetCurrentTemperatures();
        var packageReading = snapshot.CpuSensors
            .Where(reading => reading.Origin == TemperatureOrigin.CpuPackage && reading.IsValid)
            .OrderByDescending(reading => reading.Celsius)
            .FirstOrDefault();

        if (packageReading != null)
        {
            return packageReading.Celsius;
        }

        return snapshot.CpuSensors
            .Where(reading => reading.IsValid)
            .Select(reading => reading.Celsius)
            .DefaultIfEmpty()
            .Max();
    }

    public float? TryGetGpuTemperature()
    {
        var snapshot = EnsureTemperatureModule().GetCurrentTemperatures();
        var selectedReading = SelectPrimaryGpuTemperature(snapshot.GpuSensors);
        var alternativeReading = TryGetAlternativeGpuTemperature(snapshot.CapturedAtUtc);
        TimeSpan previousAge = _lastAcceptedGpuTemperatureTimestampUtc == DateTimeOffset.MinValue
            ? TimeSpan.MaxValue
            : snapshot.CapturedAtUtc - _lastAcceptedGpuTemperatureTimestampUtc;
        float? validatedTemperature = SelectValidatedGpuTemperature(
            snapshot.GpuSensors,
            alternativeReading,
            _lastAcceptedGpuTemperature,
            previousAge);

        if (validatedTemperature.HasValue)
        {
            _lastAcceptedGpuTemperature = validatedTemperature.Value;
            _lastAcceptedGpuTemperatureTimestampUtc = snapshot.CapturedAtUtc;
            if (selectedReading != null)
            {
                Logger.Trace("Validated GPU temperature sensor: {0} / {1} = {2:F1}C", selectedReading.DeviceName, selectedReading.SensorName, validatedTemperature.Value);
            }

            return validatedTemperature.Value;
        }

        if (snapshot.HasErrors)
        {
            Logger.Debug("GPU temperature unavailable: {0}", string.Join(" | ", snapshot.Errors));
        }

        return null;
    }

    public float? TryGetCpuLoad()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            EnsureCountersInitialized();
            if (_cpuCounter == null)
            {
                return null;
            }

            if (TryGetCachedValue("CpuLoad", out float cached))
            {
                return cached;
            }

            try
            {
                float value = Compatibility.Clamp(_cpuCounter.NextValue(), 0, 100);
                SetCachedValue("CpuLoad", value);
                return value;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to read CPU load.");
                return null;
            }
        }
    }

    public float? TryGetVramUsagePercentage()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            EnsureCountersInitialized();
            EnsureScopesConnected();
            if (_vramCounters.Count == 0 || !_cimv2Scope.IsConnected)
            {
                return null;
            }

            if (TryGetCachedValue("VramUsage", WmiCacheDuration, out float cached))
            {
                return cached;
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(_cimv2Scope, new ObjectQuery("SELECT AdapterRAM FROM Win32_VideoController"));
                long totalVram = 0;
                foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
                {
                    totalVram = Math.Max(totalVram, Convert.ToInt64(obj["AdapterRAM"]));
                }

                if (totalVram <= 0)
                {
                    return null;
                }

                float currentUsage = 0;
                foreach (var counter in _vramCounters)
                {
                    try
                    {
                        currentUsage += counter.NextValue();
                    }
                    catch
                    {
                    }
                }

                float percentage = Compatibility.Clamp((currentUsage / totalVram) * 100f, 0, 100);
                SetCachedValue("VramUsage", percentage);
                return percentage;
            }
            catch (Exception ex)
            {
                Logger.Trace(ex, "Failed to read VRAM usage.");
                return null;
            }
        }
    }

    public float? TryGetGpuLoad()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            EnsureCountersInitialized();
            if (TryGetCachedValue("GpuLoad", out float cached))
            {
                return cached;
            }

            try
            {
                float maxValue = 0;
                foreach (var counter in _gpuLoadCounters)
                {
                    try
                    {
                        maxValue = Math.Max(maxValue, counter.NextValue());
                    }
                    catch
                    {
                    }
                }

                maxValue = Compatibility.Clamp(maxValue, 0, 100);
                SetCachedValue("GpuLoad", maxValue);
                return maxValue;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to read GPU load.");
                return null;
            }
        }
    }

    internal static string FormatTemperature(float? temperature)
    {
        return temperature is > 0
            ? $"{Math.Round(temperature.Value, MidpointRounding.AwayFromZero):0}°C"
            : "--°C";
    }

    internal static TemperatureSensorReading? SelectPrimaryGpuTemperature(IEnumerable<TemperatureSensorReading> readings)
    {
        return readings
            .Where(IsPlausibleGpuReading)
            .OrderByDescending(GetGpuTemperaturePriority)
            .ThenByDescending(reading => reading.Celsius)
            .FirstOrDefault();
    }

    internal static float? SelectValidatedGpuTemperature(
        IEnumerable<TemperatureSensorReading> readings,
        TemperatureSensorReading? alternativeReading,
        float? previousAcceptedTemperature,
        TimeSpan previousAcceptedAge)
    {
        var primary = SelectPrimaryGpuTemperature(readings);
        var alternative = alternativeReading is not null && IsPlausibleGpuReading(alternativeReading)
            ? alternativeReading
            : null;

        if (primary != null && alternative != null)
        {
            float delta = Math.Abs(primary.Celsius!.Value - alternative.Celsius!.Value);
            if (delta <= 8f)
            {
                return primary.Celsius;
            }

            if (IsHotspotLikeSensor(primary.SensorName) || delta >= 15f)
            {
                return alternative.Celsius;
            }
        }

        if (primary != null)
        {
            if (previousAcceptedTemperature.HasValue
                && previousAcceptedAge <= GpuFallbackRetention
                && Math.Abs(primary.Celsius!.Value - previousAcceptedTemperature.Value) > MaxUncorroboratedGpuJumpCelsius)
            {
                return alternative?.Celsius ?? previousAcceptedTemperature;
            }

            return primary.Celsius;
        }

        if (alternative != null)
        {
            return alternative.Celsius;
        }

        if (previousAcceptedTemperature.HasValue && previousAcceptedAge <= GpuFallbackRetention)
        {
            return previousAcceptedTemperature;
        }

        return null;
    }

    private static int GetGpuTemperaturePriority(TemperatureSensorReading reading)
    {
        string sensorName = reading.SensorName.ToLowerInvariant();
        string deviceName = reading.DeviceName.ToLowerInvariant();

        if (sensorName.Contains("core") || sensorName == "edge")
        {
            return 100;
        }

        if (sensorName.Contains("gpu") && !sensorName.Contains("hot") && !sensorName.Contains("junction") && !sensorName.Contains("memory"))
        {
            return 90;
        }

        if (deviceName.Contains("nvidia") && sensorName.Contains("temperature"))
        {
            return 80;
        }

        if (sensorName.Contains("junction") || sensorName.Contains("hot") || sensorName.Contains("memory"))
        {
            return 10;
        }

        return 50;
    }

    private TemperatureSensorReading? TryGetAlternativeGpuTemperature(DateTimeOffset timestampUtc)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            if (TryGetCachedValue("GpuAltTemp", WmiCacheDuration, out float cached))
            {
                return new TemperatureSensorReading(
                    "windows:nvidia-smi:gpu:0",
                    "NVIDIA GPU",
                    "GPU Temperature",
                    TemperatureOrigin.GpuCore,
                    cached,
                    timestampUtc,
                    TemperatureReadStatus.Ok,
                    index: 0);
            }
        }

        var result = CommandTemperatureReader.TryRun(
            "nvidia-smi",
            "--query-gpu=name,temperature.gpu --format=csv,noheader,nounits",
            3000);

        if (!result.Success)
        {
            return null;
        }

        foreach (var line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || !float.TryParse(parts[1].Trim(), out float value))
            {
                continue;
            }

            lock (_syncRoot)
            {
                SetCachedValue("GpuAltTemp", value);
            }

            return new TemperatureSensorReading(
                "windows:nvidia-smi:gpu:0",
                parts[0].Trim(),
                "GPU Temperature",
                TemperatureOrigin.GpuCore,
                value,
                timestampUtc,
                TemperatureReadStatus.Ok,
                index: 0);
        }

        return null;
    }

    private static bool IsPlausibleGpuReading(TemperatureSensorReading reading)
    {
        return reading.IsValid && reading.Celsius is >= 10f and <= 120f;
    }

    private static bool IsHotspotLikeSensor(string sensorName)
    {
        string lowered = sensorName.ToLowerInvariant();
        return lowered.Contains("hot")
            || lowered.Contains("junction")
            || lowered.Contains("memory");
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _temperatureModule?.Dispose();
        _cpuCounter?.Dispose();
        foreach (var counter in _cpuCoreCounters) counter.Dispose();
        foreach (var counter in _gpuLoadCounters) counter.Dispose();
        foreach (var counter in _vramCounters) counter.Dispose();
        _cpuCoreCounters.Clear();
        _gpuLoadCounters.Clear();
        _vramCounters.Clear();
    }

    public static void RunInternalDiagnostics()
    {
        Logger.Info("Starting Internal Hardware Telemetry Diagnostics...");
        using var service = new HardwareTelemetryService();

        try
        {
            var snapshot = service.GetCurrentTemperatures();
            Logger.Info("[TEST] CPU sensors: {0}, GPU sensors: {1}, errors: {2}", snapshot.CpuSensors.Count, snapshot.GpuSensors.Count, snapshot.Errors.Count);
            Logger.Info("[TEST] CPU aggregate temp: {0}", FormatTemperature(service.TryGetCpuTemperature()));
            Logger.Info("[TEST] GPU aggregate temp: {0}", FormatTemperature(service.TryGetGpuTemperature()));
            Logger.Info("[TEST] CPU load: {0:F1}%", service.TryGetCpuLoad());
            Logger.Info("[TEST] GPU load: {0:F1}%", service.TryGetGpuLoad());
            Logger.Info("[TEST] Critical events buffered: {0}", service.GetCriticalEvents().Count);
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex, "Internal diagnostics failed with critical error.");
            throw;
        }
    }
}
