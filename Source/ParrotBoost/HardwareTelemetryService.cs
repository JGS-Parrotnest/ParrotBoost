using LibreHardwareMonitor.Hardware;

namespace ParrotBoost;

internal sealed class HardwareTelemetryService : IDisposable
{
    private static readonly UpdateVisitor UpdateSensorsVisitor = new();
    private readonly object _syncRoot = new();
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true
    };

    private bool _isOpen;
    private bool _isDisposed;

    public float? TryGetGpuTemperature()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            try
            {
                EnsureOpen();
                _computer.Accept(UpdateSensorsVisitor);

                var readings = EnumerateHardware(_computer.Hardware)
                    .Where(IsGpuHardware)
                    .SelectMany(hardware => hardware.Sensors)
                    .Where(sensor => sensor.SensorType == SensorType.Temperature)
                    .Select(sensor => new GpuTemperatureReading(sensor.Name ?? string.Empty, sensor.Value))
                    .ToArray();

                return SelectPreferredGpuTemperature(readings);
            }
            catch
            {
                return null;
            }
        }
    }

    public float? TryGetCpuTemperature()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            try
            {
                EnsureOpen();
                _computer.Accept(UpdateSensorsVisitor);

                var readings = EnumerateHardware(_computer.Hardware)
                    .Where(IsCpuHardware)
                    .SelectMany(hardware => hardware.Sensors)
                    .Where(sensor => sensor.SensorType == SensorType.Temperature)
                    .Select(sensor => new CpuReading(sensor.Name ?? string.Empty, sensor.Value))
                    .ToArray();

                return SelectPreferredCpuTemperature(readings);
            }
            catch
            {
                return null;
            }
        }
    }

    public float? TryGetCpuLoad()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return null;
            }

            try
            {
                EnsureOpen();
                _computer.Accept(UpdateSensorsVisitor);

                var readings = EnumerateHardware(_computer.Hardware)
                    .Where(IsCpuHardware)
                    .SelectMany(hardware => hardware.Sensors)
                    .Where(sensor => sensor.SensorType == SensorType.Load)
                    .Select(sensor => new CpuReading(sensor.Name ?? string.Empty, sensor.Value))
                    .ToArray();

                return SelectPreferredCpuLoad(readings);
            }
            catch
            {
                return null;
            }
        }
    }

    internal static float? SelectPreferredGpuTemperature(IEnumerable<GpuTemperatureReading> readings)
    {
        return readings
            .Where(reading => reading.Value is > 0)
            .OrderBy(reading => GetSensorPriority(reading.Name))
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reading => reading.Value)
            .FirstOrDefault();
    }

    internal static string FormatTemperature(float? temperature)
    {
        return temperature is > 0
            ? $"{Math.Round(temperature.Value, MidpointRounding.AwayFromZero):0}°C"
            : "--°C";
    }

    internal static float? SelectPreferredCpuTemperature(IEnumerable<CpuReading> readings)
    {
        return readings
            .Where(reading => reading.Value is > 0)
            .OrderBy(reading => GetCpuTemperaturePriority(reading.Name))
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reading => reading.Value)
            .FirstOrDefault();
    }

    internal static float? SelectPreferredCpuLoad(IEnumerable<CpuReading> readings)
    {
        return readings
            .Where(reading => reading.Value is >= 0)
            .OrderBy(reading => GetCpuLoadPriority(reading.Name))
            .ThenBy(reading => reading.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reading => reading.Value)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_isOpen)
            {
                _computer.Close();
            }

            _isDisposed = true;
        }
    }

    private void EnsureOpen()
    {
        if (_isOpen)
        {
            return;
        }

        _computer.Open();
        _isOpen = true;
    }

    private static bool IsGpuHardware(IHardware hardware)
    {
        return hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;
    }

    private static bool IsCpuHardware(IHardware hardware)
    {
        return hardware.HardwareType == HardwareType.Cpu;
    }

    private static int GetSensorPriority(string name)
    {
        var normalizedName = name.Trim().ToLowerInvariant();

        if (normalizedName.Contains("gpu core") || normalizedName == "core" || normalizedName == "temperature" || normalizedName.Contains("edge"))
        {
            return 0;
        }

        if (normalizedName.Contains("hotspot") || normalizedName.Contains("hot spot") || normalizedName.Contains("junction"))
        {
            return 1;
        }

        if (normalizedName.Contains("memory") || normalizedName.Contains("mem"))
        {
            return 2;
        }

        return 3;
    }

    private static int GetCpuTemperaturePriority(string name)
    {
        var normalizedName = name.Trim().ToLowerInvariant();

        if (normalizedName.Contains("package"))
        {
            return 0;
        }

        if (normalizedName.Contains("tctl") || normalizedName.Contains("tdie"))
        {
            return 1;
        }

        if (normalizedName.Contains("core max"))
        {
            return 2;
        }

        if (normalizedName.Contains("ccd") || normalizedName.Contains("core"))
        {
            return 3;
        }

        return 4;
    }

    private static int GetCpuLoadPriority(string name)
    {
        var normalizedName = name.Trim().ToLowerInvariant();

        if (normalizedName.Contains("cpu total") || normalizedName == "total")
        {
            return 0;
        }

        if (normalizedName.Contains("package"))
        {
            return 1;
        }

        if (normalizedName.Contains("core max"))
        {
            return 2;
        }

        return 3;
    }

    private static IEnumerable<IHardware> EnumerateHardware(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;

            foreach (var subHardware in EnumerateHardware(item.SubHardware))
            {
                yield return subHardware;
            }
        }
    }

    internal readonly record struct GpuTemperatureReading(string Name, float? Value);
    internal readonly record struct CpuReading(string Name, float? Value);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

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
