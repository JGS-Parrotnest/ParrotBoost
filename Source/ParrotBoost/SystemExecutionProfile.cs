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
    public static int GetPhysicalCoreCount()
    {
        return PhysicalCoreCount.Value;
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
}
