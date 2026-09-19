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
    private const float MaxUncorroboratedGpuJumpCelsius = 25f;

#if NET9_0_OR_GREATER
    private readonly Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif

    private TemperatureMonitoringModule? _temperatureModule;
    private readonly WindowsCompatibilityProfile _compatibilityProfile;
    private readonly TimeSpan _perfCounterCacheDuration;
    private readonly TimeSpan _wmiCacheDuration;
    private readonly TimeSpan _altGpuTemperatureCacheDuration;
    private readonly TimeSpan _altGpuProbeCooldown;
    private readonly ManagementScope _cimv2Scope = new(@"root\CIMV2");
    private readonly Dictionary<string, (float Value, DateTime Timestamp)> _cache = [];
    private bool _scopesConnected;
    private bool _isDisposed;
    private float? _lastAcceptedGpuTemperature;
    private DateTimeOffset _lastAcceptedGpuTemperatureTimestampUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAlternativeGpuProbeUtc = DateTimeOffset.MinValue;

    private static long _lastIdleTime;
    private static long _lastKernelTime;
    private static long _lastUserTime;
    private static bool _hasLastTimes;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idleTime, out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    public HardwareTelemetryService()
        : this(WindowsCompatibilityProfile.Current)
    {
    }

    internal HardwareTelemetryService(WindowsCompatibilityProfile compatibilityProfile)
    {
        _compatibilityProfile = compatibilityProfile;
        _perfCounterCacheDuration = compatibilityProfile.PerformanceCounterCacheDuration;
        _wmiCacheDuration = compatibilityProfile.WmiCacheDuration;
        _altGpuTemperatureCacheDuration = compatibilityProfile.AlternativeGpuTemperatureCacheDuration;
        _altGpuProbeCooldown = compatibilityProfile.AlternativeGpuProbeCooldown;
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
    public TemperatureSnapshot RefreshCurrentTemperatures()
    {
        var module = EnsureTemperatureModule();
        module.PollNow();
        return module.GetCurrentTemperatures();
    }

    public TemperatureSnapshot getCurrentTemperatures() => GetCurrentTemperatures();
    public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> GetTemperatureHistory() => EnsureTemperatureModule().GetTemperatureHistory();
    public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> getTemperatureHistory() => GetTemperatureHistory();
    public IReadOnlyList<CriticalTemperatureEvent> GetCriticalEvents() => EnsureTemperatureModule().GetCriticalEvents();
    public IReadOnlyList<CriticalTemperatureEvent> getCriticalEvents() => GetCriticalEvents();

    public void SetBackgroundMode(bool isBackground)
    {
        var interval = isBackground ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);
        EnsureTemperatureModule().SetPollingInterval(interval);
    }

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

    private bool TryGetCachedValue(string key, out float value)
    {
        return TryGetCachedValue(key, _perfCounterCacheDuration, out value);
    }

    private bool TryGetCachedValue(string key, TimeSpan cacheDuration, out float value)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.Timestamp < cacheDuration)
        {
            value = entry.Value;
            return true;
        }

        value = 0;
        return false;
    }

    private void SetCachedValue(string key, float value)
    {
        _cache[key] = (value, DateTime.UtcNow);
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

            if (TryGetCachedValue("CpuClock", _wmiCacheDuration, out float cached))
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
        float? total = TryGetCpuLoad();
        return total.HasValue ? [total.Value] : [];
    }

    private PerformanceCounter? _cpuPerfCounter;
    private float _smoothedCpuTemp;

    public float? TryGetCpuTemperature()
    {
        var snapshot = EnsureTemperatureModule().GetCurrentTemperatures();
        var preferredReading = snapshot.CpuSensors
            .Where(r => r.IsValid && r.Celsius.HasValue && r.Celsius.Value > 15 && Math.Abs(r.Celsius.Value - 27.85f) > 0.6f)
            .OrderBy(r => GetCpuSensorPriority(r.SensorName))
            .FirstOrDefault();

        if (preferredReading != null && preferredReading.Celsius.HasValue)
        {
            return preferredReading.Celsius.Value;
        }

        return TryGetFallbackCpuTemperature();
    }

    private static int GetCpuSensorPriority(string sensorName)
    {
        if (sensorName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)) return 0;
        if (sensorName.Contains("Package", StringComparison.OrdinalIgnoreCase)) return 1;
        if (sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase)) return 2;
        if (sensorName.Contains("Core Max", StringComparison.OrdinalIgnoreCase)) return 3;
        if (sensorName.Contains("Core Average", StringComparison.OrdinalIgnoreCase)) return 4;
        if (sensorName.Contains("CPU Core", StringComparison.OrdinalIgnoreCase)) return 5;
        if (sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase)) return 6;
        return 10;
    }

    private static bool _thermalZoneUnavailable;

    private float? TryGetFallbackCpuTemperature()
    {
        if (TryGetCachedValue("FallbackCpuTemp", TimeSpan.FromSeconds(3), out float cached))
        {
            return cached;
        }

        if (!_thermalZoneUnavailable)
        {
            try
            {
                if (PerformanceCounterCategory.Exists("Thermal Zone Information"))
                {
                    var cat = new PerformanceCounterCategory("Thermal Zone Information");
                    var instances = cat.GetInstanceNames();
                    if (instances.Length == 0)
                    {
                        _thermalZoneUnavailable = true;
                    }
                    else
                    {
                        foreach (var inst in instances)
                        {
                            if (inst.Contains("TZ00", StringComparison.OrdinalIgnoreCase)) continue;
                            using var pc = new PerformanceCounter("Thermal Zone Information", "Temperature", inst);
                            float valK = pc.NextValue();
                            if (Math.Abs(valK - 301.0f) < 0.6f) continue;
                            float valC = valK - 273.15f;
                            if (valC > 15 && valC < 115 && Math.Abs(valC - 27.85f) > 0.6f)
                            {
                                SetCachedValue("FallbackCpuTemp", valC);
                                return valC;
                            }
                        }
                    }
                }
                else
                {
                    _thermalZoneUnavailable = true;
                }
            }
            catch
            {
                _thermalZoneUnavailable = true;
            }
        }

        float? coreTemp = TryGetCoreTempSharedMemory();
        if (coreTemp.HasValue && coreTemp.Value > 15 && coreTemp.Value < 115)
        {
            SetCachedValue("FallbackCpuTemp", coreTemp.Value);
            return coreTemp.Value;
        }

        float? load = TryGetCpuLoad();
        float baseTemp = 36.0f;
        float dynamicEstimate = baseTemp + ((load ?? 5.0f) * 0.42f);
        _smoothedCpuTemp = _smoothedCpuTemp <= 0 ? dynamicEstimate : (_smoothedCpuTemp * 0.85f + dynamicEstimate * 0.15f);
        return (float)Math.Round(_smoothedCpuTemp, 1);
    }

    private static float? TryGetCoreTempSharedMemory()
    {
        try
        {
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting("CoreTempMappingObject", System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 1024, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            uint uiLoadCount = accessor.ReadUInt32(0);
            uint uiTjMaxCount = accessor.ReadUInt32(4);
            int coreCount = accessor.ReadInt32(8);
            if (coreCount > 0 && coreCount <= 128)
            {
                float totalTemp = 0;
                int validCount = 0;
                for (int i = 0; i < coreCount; i++)
                {
                    float temp = accessor.ReadSingle(12 + (i * 4));
                    if (temp > 10 && temp < 120)
                    {
                        totalTemp += temp;
                        validCount++;
                    }
                }
                if (validCount > 0)
                {
                    return totalTemp / validCount;
                }
            }
        }
        catch
        {
        }
        return null;
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
            return validatedTemperature.Value;
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

            if (TryGetCachedValue("CpuLoad", TimeSpan.FromMilliseconds(400), out float cached))
            {
                return cached;
            }

            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long idleVal = FileTimeToLong(idle);
                long kernelVal = FileTimeToLong(kernel);
                long userVal = FileTimeToLong(user);

                if (_hasLastTimes)
                {
                    long usrDiff = userVal - _lastUserTime;
                    long kerDiff = kernelVal - _lastKernelTime;
                    long idlDiff = idleVal - _lastIdleTime;

                    long sysTotal = usrDiff + kerDiff;
                    if (sysTotal > 0)
                    {
                        long busy = sysTotal - idlDiff;
                        if (busy < 0) busy = 0;
                        float percent = (float)(busy * 100.0 / sysTotal);
                        float clamped = Compatibility.Clamp(percent, 0, 100);
                        SetCachedValue("CpuLoad", clamped);

                        _lastIdleTime = idleVal;
                        _lastKernelTime = kernelVal;
                        _lastUserTime = userVal;
                        return clamped;
                    }
                }

                _lastIdleTime = idleVal;
                _lastKernelTime = kernelVal;
                _lastUserTime = userVal;
                _hasLastTimes = true;
            }

            try
            {
                _cpuPerfCounter ??= CreateCpuPerfCounter();
                if (_cpuPerfCounter != null)
                {
                    float load = _cpuPerfCounter.NextValue();
                    if (load >= 0 && load <= 100)
                    {
                        SetCachedValue("CpuLoad", load);
                        return load;
                    }
                }
            }
            catch
            {
            }

            var snapshot = EnsureTemperatureModule().GetCurrentTemperatures();
            if (snapshot.CpuLoad.HasValue && snapshot.CpuLoad.Value >= 0)
            {
                float clamped = Compatibility.Clamp(snapshot.CpuLoad.Value, 0, 100);
                SetCachedValue("CpuLoad", clamped);
                return clamped;
            }

            return 0;
        }
    }

    private static PerformanceCounter? CreateCpuPerfCounter()
    {
        try
        {
            var pc = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            pc.NextValue();
            return pc;
        }
        catch
        {
            try
            {
                var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                pc.NextValue();
                return pc;
            }
            catch
            {
                return null;
            }
        }
    }

    private static long FileTimeToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        return ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    public float? TryGetVramUsagePercentage()
    {
        return null;
    }

    public float? TryGetGpuLoad()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            if (TryGetCachedValue("GpuLoad", TimeSpan.FromMilliseconds(400), out float cached))
            {
                return cached;
            }

            var snapshot = EnsureTemperatureModule().GetCurrentTemperatures();
            if (snapshot.GpuLoad.HasValue && snapshot.GpuLoad.Value >= 0)
            {
                float clamped = Compatibility.Clamp(snapshot.GpuLoad.Value, 0, 100);
                SetCachedValue("GpuLoad", clamped);
                return clamped;
            }

            return 0;
        }
    }

    internal static string FormatTemperature(float? temperature)
    {
        return temperature is > 0
            ? $"{Math.Round(temperature.Value, MidpointRounding.AwayFromZero):0}°C"
            : "--°C";
    }

    internal static TemperatureSensorReading? SelectPrimaryGpuTemperature(IReadOnlyList<TemperatureSensorReading> readings)
    {
        if (readings == null || readings.Count == 0) return null;

        var valid = readings.Where(r => r.Origin == TemperatureOrigin.GpuCore && r.IsValid && r.Celsius.HasValue).ToList();
        if (valid.Count == 0) return null;

        var coreSensor = valid.FirstOrDefault(r => r.SensorName.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));
        if (coreSensor != null) return coreSensor;

        var genericSensor = valid.FirstOrDefault(r => r.SensorName.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase));
        if (genericSensor != null) return genericSensor;

        var anyCore = valid.FirstOrDefault(r => r.SensorName.Contains("Core", StringComparison.OrdinalIgnoreCase));
        if (anyCore != null) return anyCore;

        var nonHotspot = valid.FirstOrDefault(r => !r.SensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) && !r.SensorName.Contains("Memory", StringComparison.OrdinalIgnoreCase));
        if (nonHotspot != null) return nonHotspot;

        return valid[0];
    }

    internal static float? SelectValidatedGpuTemperature(
        IReadOnlyList<TemperatureSensorReading> readings,
        TemperatureSensorReading? alternativeReading,
        float? lastAcceptedTemperature,
        TimeSpan previousAge)
    {
        var primary = SelectPrimaryGpuTemperature(readings);
        if (primary != null && primary.Celsius.HasValue)
        {
            float candidate = primary.Celsius.Value;
            if (alternativeReading != null && alternativeReading.Celsius.HasValue && primary.SensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
            {
                return alternativeReading.Celsius.Value;
            }

            if (lastAcceptedTemperature.HasValue && previousAge < TimeSpan.FromSeconds(10))
            {
                if (Math.Abs(candidate - lastAcceptedTemperature.Value) > MaxUncorroboratedGpuJumpCelsius)
                {
                    if (alternativeReading != null && alternativeReading.Celsius.HasValue)
                    {
                        return alternativeReading.Celsius.Value;
                    }
                    return lastAcceptedTemperature.Value;
                }
            }

            return candidate;
        }

        if (alternativeReading != null && alternativeReading.Celsius.HasValue)
        {
            return alternativeReading.Celsius.Value;
        }

        return null;
    }

    private TemperatureSensorReading? TryGetAlternativeGpuTemperature(DateTimeOffset capturedAtUtc)
    {
        if (capturedAtUtc < _nextAlternativeGpuProbeUtc)
        {
            return null;
        }

        _nextAlternativeGpuProbeUtc = capturedAtUtc.Add(_altGpuProbeCooldown);

        try
        {
            EnsureScopesConnected();
            if (!_cimv2Scope.IsConnected) return null;

            using var searcher = new ManagementObjectSearcher(_cimv2Scope, new ObjectQuery("SELECT CurrentTemperature, Name FROM Win32_VideoController"));
            foreach (ManagementObject obj in searcher.Get().Cast<ManagementObject>())
            {
                if (obj["CurrentTemperature"] != null)
                {
                    float val = Convert.ToSingle(obj["CurrentTemperature"]);
                    if (val > 0 && val < 120)
                    {
                        string name = obj["Name"]?.ToString() ?? "GPU";
                        return new TemperatureSensorReading(
                            "gpu/wmi",
                            name,
                            "GPU Temperature",
                            TemperatureOrigin.GpuCore,
                            val,
                            capturedAtUtc,
                            TemperatureReadStatus.Ok);
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static void RunInternalDiagnostics()
    {
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _cpuPerfCounter?.Dispose();
            _cpuPerfCounter = null;
            _temperatureModule?.Dispose();
        }
    }
}
