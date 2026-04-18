using System;
using System.Diagnostics;
using System.Linq;
using NLog;

namespace ParrotBoost;

internal sealed class WindowsSecurityActivityMonitor
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

#if NET9_0_OR_GREATER
    private readonly Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif

    private DateTimeOffset _lastSampleUtc = DateTimeOffset.MinValue;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
    private bool _lastBusyState;

    public bool IsDefenderBusy(float cpuThresholdPercent, TimeSpan sampleInterval)
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastSampleUtc != DateTimeOffset.MinValue && now - _lastSampleUtc < sampleInterval)
            {
                return _lastBusyState;
            }

            TimeSpan currentCpuTime = SampleMsMpEngCpuTime();
            if (_lastSampleUtc == DateTimeOffset.MinValue)
            {
                _lastSampleUtc = now;
                _lastTotalProcessorTime = currentCpuTime;
                _lastBusyState = false;
                return false;
            }

            double elapsedMs = (now - _lastSampleUtc).TotalMilliseconds;
            double cpuMs = (currentCpuTime - _lastTotalProcessorTime).TotalMilliseconds;
            _lastSampleUtc = now;
            _lastTotalProcessorTime = currentCpuTime;

            if (elapsedMs <= 0 || cpuMs <= 0)
            {
                _lastBusyState = false;
                return false;
            }

            double normalizedCpuPercent = cpuMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100d;
            _lastBusyState = normalizedCpuPercent >= cpuThresholdPercent;
            return _lastBusyState;
        }
    }

    private static TimeSpan SampleMsMpEngCpuTime()
    {
        try
        {
            return Process.GetProcessesByName("MsMpEng")
                .Aggregate(TimeSpan.Zero, (total, process) =>
                {
                    try
                    {
                        return total + process.TotalProcessorTime;
                    }
                    catch
                    {
                        return total;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                });
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to sample Windows Defender CPU usage.");
            return TimeSpan.Zero;
        }
    }
}
