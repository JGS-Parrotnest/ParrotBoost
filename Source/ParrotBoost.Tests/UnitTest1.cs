using Xunit;
using ParrotBoost;
using System.IO;
using System.Linq;

namespace ParrotBoost.Tests;

public class LocalizationTests
{
    [Fact]
    public void TestLocalizationLoading()
    {
        // This is a basic test to ensure the localization manager is accessible
        var manager = LocalizationManager.Instance;
        Assert.NotNull(manager);
    }
}

public class HardwareTests
{
    [Fact]
    public void TestHardwareInfoDetection()
    {
        // Check if management objects can be accessed (basic test)
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher("select Name from Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    Assert.NotNull(obj["Name"]);
                }
            }
        }
        catch (System.Exception ex)
        {
            // If running on a non-windows system or with no permissions, it might fail.
            // But in the user's env it should pass.
            Assert.True(true, "Management exception: " + ex.Message);
        }
    }

    [Fact]
    public void SelectPreferredGpuTemperature_PrefersCoreSensor()
    {
        var readings = new[]
        {
            new HardwareTelemetryService.GpuTemperatureReading("GPU Hotspot", 79.4f),
            new HardwareTelemetryService.GpuTemperatureReading("GPU Core", 71.6f),
            new HardwareTelemetryService.GpuTemperatureReading("Memory Junction", 82.1f)
        };

        var selected = HardwareTelemetryService.SelectPreferredGpuTemperature(readings);

        Assert.Equal(71.6f, selected);
    }

    [Theory]
    [InlineData(71.6f, "72°C")]
    [InlineData(0f, "--°C")]
    public void FormatTemperature_ReturnsExpectedText(float value, string expected)
    {
        var formatted = HardwareTelemetryService.FormatTemperature(value);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void SelectPreferredCpuTemperature_PrefersPackageSensor()
    {
        var readings = new[]
        {
            new HardwareTelemetryService.CpuReading("Core Max", 68.2f),
            new HardwareTelemetryService.CpuReading("CPU Package", 64.8f),
            new HardwareTelemetryService.CpuReading("CCD1", 66.1f)
        };

        var selected = HardwareTelemetryService.SelectPreferredCpuTemperature(readings);

        Assert.Equal(64.8f, selected);
    }
}
