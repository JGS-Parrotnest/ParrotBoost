using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;

namespace ParrotBoost.Tests;

public sealed class TemperatureMonitoringModuleTests
{
    [Fact]
    public void GetCurrentTemperatures_Returns_AllCpuAndGpuSensors()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
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

        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow);
        module.PollNow();

        var snapshot = module.GetCurrentTemperatures();
        Assert.Equal(3, snapshot.CpuSensors.Count);
        Assert.Equal(2, snapshot.GpuSensors.Count);
        Assert.Empty(snapshot.Errors);
    }

    [Fact]
    public void GetTemperatureHistory_Keeps_AtLeast_60Seconds_And_Trims_OlderSamples()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(start);
        using var provider = new QueueTemperatureProvider();
        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow);

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
        Assert.True(history.ContainsKey("cpu/package"));
        Assert.InRange(history["cpu/package"].Count, 60, 61);
        Assert.All(history["cpu/package"], point => Assert.True(clock.UtcNow - point.TimestampUtc <= TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void GetCriticalEvents_Registers_Crossing_Only_On_Threshold_Transitions()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new QueueTemperatureProvider(
        [
            BuildResult(clock.UtcNow, [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 84, clock.UtcNow)]),
            BuildResult(clock.UtcNow.AddSeconds(1), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 86, clock.UtcNow.AddSeconds(1))]),
            BuildResult(clock.UtcNow.AddSeconds(2), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 87, clock.UtcNow.AddSeconds(2))]),
            BuildResult(clock.UtcNow.AddSeconds(3), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 82, clock.UtcNow.AddSeconds(3))]),
            BuildResult(clock.UtcNow.AddSeconds(4), [Sensor("cpu/package", "CPU", "Package", TemperatureOrigin.CpuPackage, 88, clock.UtcNow.AddSeconds(4))])
        ]);

        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow);

        for (int i = 0; i < 5; i++)
        {
            module.PollNow();
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var events = module.GetCriticalEvents();
        Assert.Equal(2, events.Count);
        Assert.All(events, ev => Assert.True(ev.Value >= 85));
    }

    [Fact]
    public void ProviderFailure_Is_Exposed_In_SnapshotErrors()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new ThrowingTemperatureProvider(new UnauthorizedAccessException("permission denied"));
        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow);

        module.PollNow();
        var snapshot = module.GetCurrentTemperatures();

        Assert.True(snapshot.HasErrors);
        Assert.Contains(snapshot.Errors, message => message.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.CpuSensors, reading => reading.Status == TemperatureReadStatus.PermissionDenied);
        Assert.Contains(snapshot.GpuSensors, reading => reading.Status == TemperatureReadStatus.PermissionDenied);
    }

    [Fact]
    public void Performance_Polling_With_FakeProvider_Remains_Lightweight()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new QueueTemperatureProvider();
        using var module = new TemperatureMonitoringModule(provider, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), () => clock.UtcNow);

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

        Assert.True(stopwatch.ElapsedMilliseconds < 250, $"Polling took too long: {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void SelectPrimaryGpuTemperature_Prefers_Core_Reading_Over_Hotspot()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var selected = HardwareTelemetryService.SelectPrimaryGpuTemperature(
        [
            Sensor("gpu/hotspot", "AMD Radeon RX 7800 XT", "Hot Spot", TemperatureOrigin.GpuCore, 88, timestamp),
            Sensor("gpu/core", "AMD Radeon RX 7800 XT", "GPU Core", TemperatureOrigin.GpuCore, 71, timestamp),
            Sensor("gpu/memory", "AMD Radeon RX 7800 XT", "Memory Junction", TemperatureOrigin.GpuCore, 82, timestamp)
        ]);

        Assert.NotNull(selected);
        Assert.Equal("GPU Core", selected!.SensorName);
        Assert.Equal(71, selected.Celsius);
    }

    [Fact]
    public void SelectPrimaryGpuTemperature_Falls_Back_To_Generic_Gpu_Sensor_When_Core_Is_Missing()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var selected = HardwareTelemetryService.SelectPrimaryGpuTemperature(
        [
            Sensor("gpu/generic", "NVIDIA GeForce RTX 4070", "GPU Temperature", TemperatureOrigin.GpuCore, 63, timestamp),
            Sensor("gpu/hotspot", "NVIDIA GeForce RTX 4070", "Hot Spot", TemperatureOrigin.GpuCore, 75, timestamp)
        ]);

        Assert.NotNull(selected);
        Assert.Equal("GPU Temperature", selected!.SensorName);
        Assert.Equal(63, selected.Celsius);
    }

    [Fact]
    public void SelectValidatedGpuTemperature_Prefers_Alternative_Source_When_Hotspot_Is_Far_Higher()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        float? validated = HardwareTelemetryService.SelectValidatedGpuTemperature(
        [
            Sensor("gpu/hotspot", "AMD Radeon RX 7800 XT", "Hot Spot", TemperatureOrigin.GpuCore, 96, timestamp)
        ],
        Sensor("gpu/nvidia-smi", "AMD Radeon RX 7800 XT", "GPU Temperature", TemperatureOrigin.GpuCore, 71, timestamp),
        null,
        TimeSpan.MaxValue);

        Assert.Equal(71, validated);
    }

    [Fact]
    public void SelectValidatedGpuTemperature_Falls_Back_To_Previous_Value_When_New_Value_Jumps_Too_Far()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        float? validated = HardwareTelemetryService.SelectValidatedGpuTemperature(
        [
            Sensor("gpu/core", "NVIDIA GeForce RTX 4070", "GPU Core", TemperatureOrigin.GpuCore, 95, timestamp)
        ],
        null,
        64,
        TimeSpan.FromSeconds(2));

        Assert.Equal(64, validated);
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

    private sealed class TestClock
    {
        public TestClock(DateTimeOffset utcNow)
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

        public string ProviderName => "TestProvider";

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

        public string ProviderName => "ThrowingProvider";

        public TemperatureProviderReadResult Read(DateTimeOffset timestampUtc)
        {
            throw _exception;
        }

        public void Dispose()
        {
        }
    }
}
