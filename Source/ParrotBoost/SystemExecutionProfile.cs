using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using NLog;

namespace ParrotBoost;

internal static class SystemExecutionProfile
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly Lazy<int> PhysicalCoreCount = new(ResolvePhysicalCoreCount, isThreadSafe: true);
    private const uint ProcessModeBackgroundBegin = 0x00100000;
    private const uint ProcessModeBackgroundEnd = 0x00200000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr handle, uint priorityClass);

    public static int GetPhysicalCoreCount()
    {
        return PhysicalCoreCount.Value;
    }

    public static IDisposable? TryEnterBackgroundProcessingMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            var process = Process.GetCurrentProcess();
            if (!SetPriorityClass(process.Handle, ProcessModeBackgroundBegin))
            {
                Logger.Debug("Background processing mode is unavailable. Win32Error={0}", Marshal.GetLastWin32Error());
                return null;
            }

            return new BackgroundProcessingScope(process.Handle);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to enter background processing mode.");
            return null;
        }
    }

    private static int ResolvePhysicalCoreCount()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Math.Max(1, Environment.ProcessorCount);
        }

        try
        {
            int total = 0;
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            foreach (ManagementObject processor in searcher.Get())
            {
                total += Convert.ToInt32(processor["NumberOfCores"]);
            }

            if (total > 0)
            {
                return total;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to resolve physical core count. Falling back to logical processor count.");
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private sealed class BackgroundProcessingScope : IDisposable
    {
        private readonly IntPtr _processHandle;
        private bool _disposed;

        public BackgroundProcessingScope(IntPtr processHandle)
        {
            _processHandle = processHandle;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                SetPriorityClass(_processHandle, ProcessModeBackgroundEnd);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to exit background processing mode.");
            }
        }
    }
}
