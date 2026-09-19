using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NLog;

namespace ParrotBoost;

internal static class TurboParrotPowerPlanManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public const string SchemeName = "Turbo Parrot";
    public const string SchemeDescription = "ParrotBoost Maximum Performance Power Plan - Zero Core Parking, 100% Min/Max CPU, Low Latency";

    public const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string BalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public static bool ApplyTurboParrotPlan()
    {
        try
        {
            string? existingGuid = FindTurboParrotSchemeGuid();
            string targetGuid;

            if (!string.IsNullOrEmpty(existingGuid))
            {
                targetGuid = existingGuid;
                Logger.Info("Existing Turbo Parrot scheme found: {0}", targetGuid);
            }
            else
            {
                targetGuid = CreateTurboParrotScheme();
                Logger.Info("Created new Turbo Parrot scheme: {0}", targetGuid);
            }

            RunPowercfg($"-changename {targetGuid} \"{SchemeName}\" \"{SchemeDescription}\"");
            RunPowercfg($"-setactive {targetGuid}");

            ApplyAdvancedPowerTweaks();
            _lastStatusCheck = DateTime.MinValue;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to apply Turbo Parrot power plan.");
            return false;
        }
    }

    public static bool RestoreDefaultPlan()
    {
        try
        {
            RunPowercfg($"-setactive {BalancedGuid}");
            _lastStatusCheck = DateTime.MinValue;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to restore default power plan.");
            return false;
        }
    }

    private static bool _cachedIsActive;
    private static DateTime _lastStatusCheck = DateTime.MinValue;

    public static bool IsTurboParrotActive()
    {
        if (DateTime.UtcNow - _lastStatusCheck < TimeSpan.FromSeconds(3))
        {
            return _cachedIsActive;
        }

        try
        {
            string? activeGuid = RegistryHelper.GetString(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", 
                "ActivePowerScheme");

            if (!string.IsNullOrEmpty(activeGuid))
            {
                string? friendlyName = RegistryHelper.GetString(
                    $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeGuid}", 
                    "FriendlyName");

                if (!string.IsNullOrEmpty(friendlyName) && friendlyName.Contains(SchemeName, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedIsActive = true;
                    _lastStatusCheck = DateTime.UtcNow;
                    return true;
                }
            }

            string output = RunPowercfgAndGetOutput("-getactivescheme");
            _cachedIsActive = output.Contains(SchemeName, StringComparison.OrdinalIgnoreCase);
            _lastStatusCheck = DateTime.UtcNow;
            return _cachedIsActive;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindTurboParrotSchemeGuid()
    {
        string listOutput = RunPowercfgAndGetOutput("/list");
        var match = Regex.Match(listOutput, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\s+\((?:Turbo Parrot)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return null;
    }

    private static string CreateTurboParrotScheme()
    {
        string dupOutput = RunPowercfgAndGetOutput($"-duplicatescheme {UltimatePerformanceGuid}");
        var match = Regex.Match(dupOutput, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        string fallbackOutput = RunPowercfgAndGetOutput($"-duplicatescheme {HighPerformanceGuid}");
        var matchFallback = Regex.Match(fallbackOutput, @"([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})", RegexOptions.IgnoreCase);
        if (matchFallback.Success)
        {
            return matchFallback.Groups[1].Value;
        }

        return HighPerformanceGuid;
    }

    private static void ApplyAdvancedPowerTweaks()
    {
        try
        {
            SetBothPowerValues("SUB_PROCESSOR", "PROCTHROTTLEMIN", 100);
            SetBothPowerValues("SUB_PROCESSOR", "PROCTHROTTLEMAX", 100);
            SetBothPowerValues("SUB_PROCESSOR", "CPMINCORES", 100);
            SetBothPowerValues("SUB_PROCESSOR", "CPMAXCORES", 100);
            SetBothPowerValues("SUB_PROCESSOR", "CPCONCURRENCY", 0);
            SetBothPowerValues("SUB_PROCESSOR", "PERFBOOSTMODE", 2);
            SetBothPowerValues("SUB_PROCESSOR", "SYSCOOLPOL", 1);
            SetBothPowerValues("SUB_PROCESSOR", "PERFINCPOL", 2);
            SetBothPowerValues("SUB_PROCESSOR", "HETEROPOL", 0);
            SetBothPowerValues("SUB_PROCESSOR", "HETEROMINREFS", 0);

            SetBothPowerValues("SUB_PCIEXPRESS", "ASPM", 0);
            SetBothPowerValues("SUB_DISK", "DISKIDLE", 0);
            SetBothPowerValues("SUB_USB", "USBSELECTIVE", 0);
            SetBothPowerValues("SUB_SLEEP", "STANDBYIDLE", 0);
            SetBothPowerValues("SUB_SLEEP", "HIBERNATEIDLE", 0);

            RunPowercfg("-setactive SCHEME_CURRENT");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to apply full Turbo Parrot parameters.");
        }
    }

    private static void SetBothPowerValues(string subgroup, string setting, int value)
    {
        RunPowercfg($"-setacvalueindex SCHEME_CURRENT {subgroup} {setting} {value}");
        RunPowercfg($"-setdcvalueindex SCHEME_CURRENT {subgroup} {setting} {value}");
    }

    private static void RunPowercfg(string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error executing powercfg {0}", arguments);
        }
    }

    private static string RunPowercfgAndGetOutput(string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error executing powercfg {0}", arguments);
            return string.Empty;
        }
    }
}
