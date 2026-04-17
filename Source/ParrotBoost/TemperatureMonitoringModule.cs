using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using NLog;

namespace ParrotBoost;

internal enum TemperatureOrigin
{
    CpuPackage,
    CpuCore,
    GpuCore
}

internal enum TemperatureReadStatus
{
    Ok,
    Unavailable,
    PermissionDenied,
    Error
}

internal sealed class TemperatureSensorReading
{
    public TemperatureSensorReading(
        string sensorId,
        string deviceName,
        string sensorName,
        TemperatureOrigin origin,
        float? celsius,
        DateTimeOffset timestampUtc,
        TemperatureReadStatus status,
        string? errorMessage = null,
        int? index = null)
    {
        SensorId = sensorId;
        DeviceName = deviceName;
        SensorName = sensorName;
        Origin = origin;
        Celsius = celsius;
        TimestampUtc = timestampUtc;
        Status = status;
        ErrorMessage = errorMessage;
        Index = index;
    }

    public string SensorId { get; }
    public string DeviceName { get; }
    public string SensorName { get; }
    public TemperatureOrigin Origin { get; }
    public float? Celsius { get; }
    public DateTimeOffset TimestampUtc { get; }
    public TemperatureReadStatus Status { get; }
    public string? ErrorMessage { get; }
    public int? Index { get; }

    public bool IsCpu => Origin == TemperatureOrigin.CpuPackage || Origin == TemperatureOrigin.CpuCore;
    public bool IsGpu => Origin == TemperatureOrigin.GpuCore;
    public bool IsValid => Status == TemperatureReadStatus.Ok && Celsius is > 0;

    public TemperatureSensorReading WithTimestamp(DateTimeOffset timestampUtc)
    {
        return new TemperatureSensorReading(
            SensorId,
            DeviceName,
            SensorName,
            Origin,
            Celsius,
            timestampUtc,
            Status,
            ErrorMessage,
            Index);
    }
}

internal sealed class TemperatureSnapshot
{
    public static readonly TemperatureSnapshot Empty = new(DateTimeOffset.MinValue, [], [], []);

    public TemperatureSnapshot(
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<TemperatureSensorReading> cpuSensors,
        IReadOnlyList<TemperatureSensorReading> gpuSensors,
        IReadOnlyList<string> errors)
    {
        CapturedAtUtc = capturedAtUtc;
        CpuSensors = cpuSensors;
        GpuSensors = gpuSensors;
        Errors = errors;
    }

    public DateTimeOffset CapturedAtUtc { get; }
    public IReadOnlyList<TemperatureSensorReading> CpuSensors { get; }
    public IReadOnlyList<TemperatureSensorReading> GpuSensors { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool HasErrors => Errors.Count > 0;
}

internal sealed class CriticalTemperatureEvent
{
    public CriticalTemperatureEvent(
        DateTimeOffset timestampUtc,
        string sensorId,
        string deviceName,
        string sensorName,
        TemperatureOrigin origin,
        float threshold,
        float value)
    {
        TimestampUtc = timestampUtc;
        SensorId = sensorId;
        DeviceName = deviceName;
        SensorName = sensorName;
        Origin = origin;
        Threshold = threshold;
        Value = value;
        Message = $"{deviceName} / {sensorName} reached {value:F1}C (threshold {threshold:F1}C).";
    }

    public DateTimeOffset TimestampUtc { get; }
    public string SensorId { get; }
    public string DeviceName { get; }
    public string SensorName { get; }
    public TemperatureOrigin Origin { get; }
    public float Threshold { get; }
    public float Value { get; }
    public string Message { get; }
}

internal sealed class TemperatureProviderReadResult
{
    public IReadOnlyList<TemperatureSensorReading> Readings { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

internal interface ITemperatureProvider : IDisposable
{
    string ProviderName { get; }
    TemperatureProviderReadResult Read(DateTimeOffset timestampUtc);
}

internal sealed class TemperatureMonitoringModule : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const float CpuCriticalThresholdCelsius = 85f;
    private const float GpuCriticalThresholdCelsius = 83f;
    private const int MaxCriticalEvents = 256;

#if NET9_0_OR_GREATER
    private readonly Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif

    private readonly ITemperatureProvider _provider;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _historyRetention;
    private readonly System.Threading.Timer _pollTimer;
    private readonly Dictionary<string, List<TemperatureSensorReading>> _history = [];
    private readonly List<CriticalTemperatureEvent> _criticalEvents = [];
    private readonly Dictionary<string, bool> _criticalState = [];
    private TemperatureSnapshot _currentSnapshot = TemperatureSnapshot.Empty;
    private bool _pollInProgress;
    private bool _disposed;

    public TemperatureMonitoringModule()
        : this(CreateDefaultProvider(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => DateTimeOffset.UtcNow)
    {
    }

    internal TemperatureMonitoringModule(
        ITemperatureProvider provider,
        TimeSpan samplingInterval,
        TimeSpan historyRetention,
        Func<DateTimeOffset> utcNow)
    {
        _provider = provider;
        _historyRetention = historyRetention < TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : historyRetention;
        _utcNow = utcNow;
        _pollTimer = new System.Threading.Timer(_ => PollNow(), null, TimeSpan.Zero, samplingInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : samplingInterval);
    }

    public event Action<CriticalTemperatureEvent>? CriticalTemperatureDetected;

    public TemperatureSnapshot GetCurrentTemperatures()
    {
        lock (_syncRoot)
        {
            return _currentSnapshot;
        }
    }

    public TemperatureSnapshot getCurrentTemperatures() => GetCurrentTemperatures();

    public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> GetTemperatureHistory()
    {
        lock (_syncRoot)
        {
            return _history.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TemperatureSensorReading>)pair.Value.ToArray());
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<TemperatureSensorReading>> getTemperatureHistory() => GetTemperatureHistory();

    public IReadOnlyList<CriticalTemperatureEvent> GetCriticalEvents()
    {
        lock (_syncRoot)
        {
            return _criticalEvents.ToArray();
        }
    }

    public IReadOnlyList<CriticalTemperatureEvent> getCriticalEvents() => GetCriticalEvents();

    internal void PollNow()
    {
        lock (_syncRoot)
        {
            if (_disposed || _pollInProgress)
            {
                return;
            }

            _pollInProgress = true;
        }

        try
        {
            var timestampUtc = _utcNow();
            TemperatureProviderReadResult providerResult;

            try
            {
                providerResult = _provider.Read(timestampUtc);
            }
            catch (UnauthorizedAccessException ex)
            {
                providerResult = BuildProviderFailure(timestampUtc, TemperatureReadStatus.PermissionDenied, ex.Message);
            }
            catch (Exception ex)
            {
                providerResult = BuildProviderFailure(timestampUtc, TemperatureReadStatus.Error, ex.Message);
            }

            var cpuSensors = providerResult.Readings.Where(r => r.IsCpu).ToArray();
            var gpuSensors = providerResult.Readings.Where(r => r.IsGpu).ToArray();
            var snapshot = new TemperatureSnapshot(timestampUtc, cpuSensors, gpuSensors, providerResult.Errors.ToArray());

            List<CriticalTemperatureEvent> newEvents = [];

            lock (_syncRoot)
            {
                _currentSnapshot = snapshot;
                UpdateHistory(cpuSensors, timestampUtc);
                UpdateHistory(gpuSensors, timestampUtc);
                newEvents.AddRange(RegisterCriticalEvents(cpuSensors, CpuCriticalThresholdCelsius, timestampUtc));
                newEvents.AddRange(RegisterCriticalEvents(gpuSensors, GpuCriticalThresholdCelsius, timestampUtc));
            }

            foreach (var criticalEvent in newEvents)
            {
                Logger.Warn(criticalEvent.Message);
                CriticalTemperatureDetected?.Invoke(criticalEvent);
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _pollInProgress = false;
            }
        }
    }

    private TemperatureProviderReadResult BuildProviderFailure(DateTimeOffset timestampUtc, TemperatureReadStatus status, string message)
    {
        Logger.Warn("Temperature provider failure: {0}", message);
        return new TemperatureProviderReadResult
        {
            Readings =
            [
                new TemperatureSensorReading(
                    "provider.cpu.unavailable",
                    _provider.ProviderName,
                    "CPU",
                    TemperatureOrigin.CpuPackage,
                    null,
                    timestampUtc,
                    status,
                    message),
                new TemperatureSensorReading(
                    "provider.gpu.unavailable",
                    _provider.ProviderName,
                    "GPU",
                    TemperatureOrigin.GpuCore,
                    null,
                    timestampUtc,
                    status,
                    message)
            ],
            Errors = [message]
        };
    }

    private void UpdateHistory(IEnumerable<TemperatureSensorReading> readings, DateTimeOffset timestampUtc)
    {
        foreach (var reading in readings)
        {
            if (!_history.TryGetValue(reading.SensorId, out var bucket))
            {
                bucket = [];
                _history[reading.SensorId] = bucket;
            }

            bucket.Add(reading.WithTimestamp(timestampUtc));
            bucket.RemoveAll(item => timestampUtc - item.TimestampUtc > _historyRetention);
        }

        var threshold = timestampUtc - _historyRetention;
        foreach (var key in _history.Keys.ToArray())
        {
            _history[key].RemoveAll(item => item.TimestampUtc < threshold);
            if (_history[key].Count == 0)
            {
                _history.Remove(key);
            }
        }
    }

    private IEnumerable<CriticalTemperatureEvent> RegisterCriticalEvents(
        IEnumerable<TemperatureSensorReading> readings,
        float threshold,
        DateTimeOffset timestampUtc)
    {
        foreach (var reading in readings)
        {
            if (!reading.IsValid)
            {
                _criticalState[reading.SensorId] = false;
                continue;
            }

            bool isCritical = reading.Celsius >= threshold;
            bool wasCritical = _criticalState.TryGetValue(reading.SensorId, out bool stored) && stored;
            _criticalState[reading.SensorId] = isCritical;

            if (!wasCritical && isCritical)
            {
                var criticalEvent = new CriticalTemperatureEvent(
                    timestampUtc,
                    reading.SensorId,
                    reading.DeviceName,
                    reading.SensorName,
                    reading.Origin,
                    threshold,
                    reading.Celsius!.Value);

                _criticalEvents.Add(criticalEvent);
                if (_criticalEvents.Count > MaxCriticalEvents)
                {
                    _criticalEvents.RemoveAt(0);
                }

                yield return criticalEvent;
            }
        }
    }

    private static ITemperatureProvider CreateDefaultProvider()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsTemperatureProvider();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxTemperatureProvider();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacTemperatureProvider();
        }

        return new UnsupportedTemperatureProvider();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _pollTimer.Dispose();
        _provider.Dispose();
    }
}

internal sealed class WindowsTemperatureProvider : ITemperatureProvider
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _opened;

    public WindowsTemperatureProvider()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };
    }

    public string ProviderName => "Windows/LibreHardwareMonitor";

    public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
    {
        EnsureOpen();
        _computer.Accept(_visitor);

        List<TemperatureSensorReading> readings = [];
        foreach (var hardware in _computer.Hardware)
        {
            CollectHardware(hardware, timestampUtc, readings);
        }

        if (readings.Count == 0)
        {
            string message = IsElevated()
                ? "No temperature sensors were reported by LibreHardwareMonitor."
                : "No temperature sensors were reported. Try running the app as administrator.";
            return new TemperatureProviderReadResult { Errors = [message] };
        }

        return new TemperatureProviderReadResult { Readings = readings };
    }

    private void CollectHardware(IHardware hardware, DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings)
    {
        hardware.Update();
        AppendSensors(hardware, timestampUtc, readings);

        foreach (var subHardware in hardware.SubHardware)
        {
            CollectHardware(subHardware, timestampUtc, readings);
        }
    }

    private static void AppendSensors(IHardware hardware, DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings)
    {
        if (!IsTemperatureCapableHardware(hardware.HardwareType))
        {
            return;
        }

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature)
            {
                continue;
            }

            var origin = ResolveOrigin(hardware.HardwareType, sensor.Name);
            if (origin is null)
            {
                continue;
            }

            readings.Add(new TemperatureSensorReading(
                BuildSensorId(hardware, sensor),
                hardware.Name,
                sensor.Name,
                origin.Value,
                sensor.Value,
                timestampUtc,
                sensor.Value.HasValue ? TemperatureReadStatus.Ok : TemperatureReadStatus.Unavailable,
                sensor.Value.HasValue ? null : "Sensor returned no value.",
                ExtractIndex(sensor.Name)));
        }
    }

    private static bool IsTemperatureCapableHardware(HardwareType hardwareType)
    {
        return hardwareType == HardwareType.Cpu
            || hardwareType == HardwareType.GpuNvidia
            || hardwareType == HardwareType.GpuAmd
            || hardwareType == HardwareType.GpuIntel;
    }

    private static TemperatureOrigin? ResolveOrigin(HardwareType hardwareType, string sensorName)
    {
        if (hardwareType == HardwareType.Cpu)
        {
            if (sensorName.Contains("Core", StringComparison.OrdinalIgnoreCase))
            {
                return TemperatureOrigin.CpuCore;
            }

            return TemperatureOrigin.CpuPackage;
        }

        return TemperatureOrigin.GpuCore;
    }

    private static string BuildSensorId(IHardware hardware, ISensor sensor)
    {
        string sensorName = sensor.Name.Replace(' ', '_').ToLowerInvariant();
        return $"{hardware.Identifier}/{sensorName}";
    }

    private static int? ExtractIndex(string sensorName)
    {
        var match = Regex.Match(sensorName, @"(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private void EnsureOpen()
    {
        if (_opened)
        {
            return;
        }

        try
        {
            _computer.Open();
            _opened = true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to initialize LibreHardwareMonitor.");
            throw;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_opened)
        {
            _computer.Close();
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}

internal sealed class LinuxTemperatureProvider : ITemperatureProvider
{
    public string ProviderName => "Linux/sysfs+nvidia-smi";

    public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
    {
        List<TemperatureSensorReading> readings = [];
        List<string> errors = [];

        try
        {
            ReadHwMon(timestampUtc, readings);
            ReadThermalZones(timestampUtc, readings);
            ReadNvidiaSmi(timestampUtc, readings, errors);
        }
        catch (UnauthorizedAccessException ex)
        {
            errors.Add(ex.Message);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        if (readings.Count == 0 && errors.Count == 0)
        {
            errors.Add("No Linux temperature sensors were detected.");
        }

        return new TemperatureProviderReadResult { Readings = readings, Errors = errors };
    }

    private static void ReadHwMon(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings)
    {
        if (!Directory.Exists("/sys/class/hwmon"))
        {
            return;
        }

        foreach (var hwmonPath in Directory.GetDirectories("/sys/class/hwmon", "hwmon*"))
        {
            string name = TryReadFirstExistingText(Path.Combine(hwmonPath, "name")) ?? "hwmon";
            string deviceName = BuildLinuxDeviceName(name);
            foreach (var tempInputPath in Directory.GetFiles(hwmonPath, "temp*_input"))
            {
                string fileName = Path.GetFileNameWithoutExtension(tempInputPath);
                int suffixIndex = fileName.LastIndexOf("_input", StringComparison.Ordinal);
                string baseName = suffixIndex > 0 ? fileName.Substring(0, suffixIndex) : fileName;
                string label = TryReadFirstExistingText(Path.Combine(hwmonPath, $"{baseName}_label")) ?? baseName;
                string raw = TryReadFirstExistingText(tempInputPath) ?? string.Empty;
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float tempMilli))
                {
                    continue;
                }

                var origin = ResolveLinuxOrigin($"{name} {label}");
                if (origin is null)
                {
                    continue;
                }

                float celsius = tempMilli > 1000 ? tempMilli / 1000f : tempMilli;
                readings.Add(new TemperatureSensorReading(
                    $"linux:hwmon:{Path.GetFileName(hwmonPath)}:{baseName}",
                    deviceName,
                    label,
                    origin.Value,
                    celsius,
                    timestampUtc,
                    TemperatureReadStatus.Ok,
                    index: ExtractIndex(label)));
            }
        }
    }

    private static void ReadThermalZones(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings)
    {
        if (!Directory.Exists("/sys/class/thermal"))
        {
            return;
        }

        foreach (var zone in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
        {
            string typePath = Path.Combine(zone, "type");
            string tempPath = Path.Combine(zone, "temp");
            if (!File.Exists(typePath) || !File.Exists(tempPath))
            {
                continue;
            }

            string type = File.ReadAllText(typePath).Trim();
            string raw = File.ReadAllText(tempPath).Trim();
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float tempMilli))
            {
                continue;
            }

            float celsius = tempMilli > 1000 ? tempMilli / 1000f : tempMilli;
            var origin = ResolveLinuxOrigin(type);
            if (origin is null)
            {
                continue;
            }

            readings.Add(new TemperatureSensorReading(
                $"linux:{Path.GetFileName(zone)}",
                "Linux Thermal Zone",
                type,
                origin.Value,
                celsius,
                timestampUtc,
                TemperatureReadStatus.Ok));
        }
    }

    private static void ReadNvidiaSmi(DateTimeOffset timestampUtc, List<TemperatureSensorReading> readings, List<string> errors)
    {
        var commandResult = CommandTemperatureReader.TryRun("nvidia-smi", "--query-gpu=name,temperature.gpu --format=csv,noheader,nounits", 3000);
        if (!commandResult.Success)
        {
            if (!string.IsNullOrWhiteSpace(commandResult.Error))
            {
                errors.Add(commandResult.Error!);
            }

            return;
        }

        int index = 0;
        foreach (var line in commandResult.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            string name = parts[0].Trim();
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float temp))
            {
                continue;
            }

            readings.Add(new TemperatureSensorReading(
                $"linux:nvidia:{index}",
                name,
                "GPU Core",
                TemperatureOrigin.GpuCore,
                temp,
                timestampUtc,
                TemperatureReadStatus.Ok,
                index: index));

            index++;
        }
    }

    private static TemperatureOrigin? ResolveLinuxOrigin(string sensorType)
    {
        string lowered = sensorType.ToLowerInvariant();
        if (lowered.Contains("gpu") || lowered.Contains("amdgpu"))
        {
            return TemperatureOrigin.GpuCore;
        }

        if (lowered.Contains("cpu") || lowered.Contains("pkg") || lowered.Contains("package") || lowered.Contains("core") || lowered.Contains("x86"))
        {
            return lowered.Contains("core") ? TemperatureOrigin.CpuCore : TemperatureOrigin.CpuPackage;
        }

        return null;
    }

    private static string BuildLinuxDeviceName(string rawName)
    {
        return rawName.ToLowerInvariant() switch
        {
            "amdgpu" => "AMD GPU",
            "nouveau" => "NVIDIA GPU",
            "nvidia" => "NVIDIA GPU",
            "coretemp" => "Intel CPU",
            "k10temp" => "AMD CPU",
            _ => rawName
        };
    }

    private static string? TryReadFirstExistingText(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static int? ExtractIndex(string sensorName)
    {
        var match = Regex.Match(sensorName, @"(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public void Dispose()
    {
    }
}

internal sealed class MacTemperatureProvider : ITemperatureProvider
{
    private static readonly Regex CpuRegex = new(@"CPU.*?temperature:\s*(?<value>\d+(\.\d+)?)\s*C", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GpuRegex = new(@"GPU.*?temperature:\s*(?<value>\d+(\.\d+)?)\s*C", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string ProviderName => "macOS/powermetrics";

    public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
    {
        var result = CommandTemperatureReader.TryRun("powermetrics", "--samplers smc -n 1", 5000);
        if (!result.Success)
        {
            string errorMessage = string.IsNullOrWhiteSpace(result.Error)
                ? "powermetrics is unavailable or requires elevated permissions."
                : result.Error!;

            return new TemperatureProviderReadResult
            {
                Errors = [errorMessage]
            };
        }

        List<TemperatureSensorReading> readings = [];
        AppendMatches(timestampUtc, result.Output, CpuRegex, "mac:cpu", "macOS CPU", TemperatureOrigin.CpuPackage, readings);
        AppendMatches(timestampUtc, result.Output, GpuRegex, "mac:gpu", "macOS GPU", TemperatureOrigin.GpuCore, readings);

        if (readings.Count == 0)
        {
            return new TemperatureProviderReadResult
            {
                Errors = ["powermetrics did not return any parseable temperature values."]
            };
        }

        return new TemperatureProviderReadResult { Readings = readings };
    }

    private static void AppendMatches(
        DateTimeOffset timestampUtc,
        string output,
        Regex regex,
        string idPrefix,
        string deviceName,
        TemperatureOrigin origin,
        List<TemperatureSensorReading> readings)
    {
        int index = 0;
        foreach (Match match in regex.Matches(output))
        {
            if (!float.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float temp))
            {
                continue;
            }

            readings.Add(new TemperatureSensorReading(
                $"{idPrefix}:{index}",
                deviceName,
                origin == TemperatureOrigin.CpuPackage ? "CPU Package" : "GPU Core",
                origin,
                temp,
                timestampUtc,
                TemperatureReadStatus.Ok,
                index: index));
            index++;
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class UnsupportedTemperatureProvider : ITemperatureProvider
{
    public string ProviderName => "Unsupported";

    public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
    {
        return new TemperatureProviderReadResult
        {
            Errors = ["Current operating system is not supported by the temperature monitoring module."]
        };
    }

    public void Dispose()
    {
    }
}

internal static class CommandTemperatureReader
{
    internal sealed class CommandResult
    {
        public bool Success { get; init; }
        public string Output { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    public static CommandResult TryRun(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { }
                return new CommandResult { Error = $"{fileName} timed out while reading temperatures." };
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            return new CommandResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = string.IsNullOrWhiteSpace(error) ? null : error.Trim()
            };
        }
        catch (Exception ex)
        {
            return new CommandResult { Error = ex.Message };
        }
    }
}
