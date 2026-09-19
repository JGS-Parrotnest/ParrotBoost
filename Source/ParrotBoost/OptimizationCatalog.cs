using System;
using System.Collections.Generic;

namespace ParrotBoost;

internal static class OptimizationCatalog
{
    public static List<SystemTweak> CreateTweaks()
    {
        return new List<SystemTweak>
        {
            new() { Id = "block_p2p_update", Name = "Disable P2P Update Uploads", Description = "Prevents Windows from using your internet and PC to upload updates to other computers.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxUploadBandwidth", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DoSvc", "Start", 4);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1);
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxUploadBandwidth");
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DoSvc", "Start", 2);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1) == 0 },

            new() { Id = "telemetry", Name = "Disable Telemetry", Description = "Prevents Windows from collecting and sending telemetry and diagnostic data.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\dmwappushservice", "Start", 4);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 2);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\dmwappushservice", "Start", 3);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1) == 0 },

            new() { Id = "telemetry_tasks", Name = "Disable Background Telemetry Tasks", Description = "Stops Microsoft Compatibility Appraiser and Customer Experience telemetry jobs.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    PowerShellHelper.RunCommand("Disable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser' -ErrorAction SilentlyContinue");
                    PowerShellHelper.RunCommand("Disable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater' -ErrorAction SilentlyContinue");
                    PowerShellHelper.RunCommand("Disable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator' -ErrorAction SilentlyContinue");
                    PowerShellHelper.RunCommand("Disable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip' -ErrorAction SilentlyContinue");
                },
                RevertAction = () =>
                {
                    PowerShellHelper.RunCommand("Enable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser' -ErrorAction SilentlyContinue");
                    PowerShellHelper.RunCommand("Enable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater' -ErrorAction SilentlyContinue");
                    PowerShellHelper.RunCommand("Enable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator' -ErrorAction SilentlyContinue");
                    PowerShellHelper.RunCommand("Enable-ScheduledTask -TaskName '\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip' -ErrorAction SilentlyContinue");
                },
                CheckAction = () => true },

            new() { Id = "errorreporting", Name = "Disable Windows Error Reporting", Description = "Stops sending crash dumps and error logs to Microsoft servers.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "DontSendAdditionalData", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WerSvc", "Start", 4);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "DontSendAdditionalData", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WerSvc", "Start", 3);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 0) == 1 },

            new() { Id = "advertising", Name = "Disable Advertising ID", Description = "Prevents apps from using your ID for targeted ads.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1) == 0 },

            new() { Id = "transparency", Name = "Disable Transparency", Description = "Disables acrylic and glass effects to save GPU resources.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1) == 0 },

            new() { Id = "gamebar", Name = "Disable Game Bar", Description = "Stops Xbox Game Bar from running in the background.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1) == 0 },

            new() { Id = "extensions", Name = "Show File Extensions", Description = "Forces File Explorer to show file extensions.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1) == 0 },

            new() { Id = "bing", Name = "Disable Bing in Start", Description = "Removes web search results from the Start menu.", Category = TweakCategory.System, Safety = TweakSafety.Moderate,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 0) == 1 },

            new() { Id = "cortana", Name = "Disable Cortana", Description = "Prevents Cortana from running in the background.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 1) == 0 },

            new() { Id = "activityhistory", Name = "Disable Activity History", Description = "Stops Windows from tracking apps and files you open.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 1) == 0 },

            new() { Id = "locationtracking", Name = "Disable Location Tracking", Description = "Prevents Windows from tracking your device location.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny"),
                RevertAction = () => RegistryHelper.SetString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Allow"),
                CheckAction = () => RegistryHelper.GetString(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Allow") == "Deny" },

            new() { Id = "feedbackfrequency", Name = "Disable Feedback Requests", Description = "Stops Windows from asking for diagnostic feedback.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 1) == 0 },

            new() { Id = "animationsoff", Name = "Disable Animations", Description = "Turns off window animations for a snappier feel.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetString(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                RevertAction = () => RegistryHelper.SetString(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1"),
                CheckAction = () => RegistryHelper.GetString(@"HKEY_CURRENT_USER\Control Panel\Desktop\WindowMetrics", "MinAnimate", "1") == "0" },

            new() { Id = "startupdelay", Name = "Disable Startup Delay", Description = "Lets startup apps launch immediately.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 1) == 0 },

            new() { Id = "vbs", Name = "Disable VBS", Description = "Turns off virtualization-based security for lower overhead.", Category = TweakCategory.Performance, Safety = TweakSafety.Dangerous,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 1) == 0 },

            new() { Id = "hibernation", Name = "Disable Hibernation", Description = "Frees disk space by removing the hibernation file.", Category = TweakCategory.Performance, Safety = TweakSafety.Moderate,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 0);
                    if (RegistryHelper.KeyExists(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings"))
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", "ShowHibernateOption", 0);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 1);
                    if (RegistryHelper.KeyExists(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings"))
                        RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", "ShowHibernateOption", 1);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", 1) == 0 },

            new() { Id = "fastboot", Name = "Enable Fast Startup", Description = "Speeds up boot time by saving system state on shutdown.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0) == 1 },

            new() { Id = "hiddensysfiles", Name = "Show Hidden Files", Description = "Makes hidden files and folders visible in Explorer.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 2) == 1 },

            new() { Id = "tailoredexperiences", Name = "Disable Tailored Experiences", Description = "Stops Windows from using diagnostic data for personalized tips and ads.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 1) == 0 },

            new() { Id = "copilot", Name = "Disable Windows Copilot", Description = "Turns off Copilot, hides the button, and disables background Copilot runtime.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 0);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 1);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0) == 1 },

            new() { Id = "recall", Name = "Disable Windows Recall & Snapshots", Description = "Blocks Recall snapshot creation, AI data analysis and timeline capture.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowUserActivityAnalysis", 0);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0);
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall");
                    RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowUserActivityAnalysis");
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0) == 1 },

            new() { Id = "clicktodo", Name = "Disable Click to Do AI", Description = "Disables Windows 11 contextual AI action recommendations on screen.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 1);
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo");
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 1) == 0 },

            new() { Id = "generative_search_ai", Name = "Disable Generative Search & Bing AI", Description = "Removes Bing AI, web search suggestions and online highlights from Start.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearchBoxSuggestions", 1);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 1);
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearchBoxSuggestions");
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1) == 0 },

            new() { Id = "app_ai_features", Name = "Disable Paint & Photos AI", Description = "Disables Cocreator, generative fill, and photo super resolution cloud/NPU AI.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableSuperResolution", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI", 1);
                },
                RevertAction = () =>
                {
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator");
                    RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableSuperResolution");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI");
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator", 0) == 1 },

            new() { Id = "edge_copilot", Name = "Disable Edge Copilot & AI Sidebar", Description = "Turns off Copilot button, sidebar AI prompts, and CDP features in Edge.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "ComposeInlineEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "EdgeEntSearchPageContext", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0);
                },
                RevertAction = () =>
                {
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "ComposeInlineEnabled");
                    RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "EdgeEntSearchPageContext");
                    RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled");
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 1) == 0 },

            new() { Id = "speechrecognition", Name = "Disable Online Speech Recognition", Description = "Prevents Windows from sending voice data to Microsoft.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 1) == 0 },

            new() { Id = "inkingtyping", Name = "Disable Inking & Typing Data", Description = "Stops Windows from collecting typing and handwriting patterns.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 0) == 1 },

            new() { Id = "consumerfeatures", Name = "Disable Consumer Features", Description = "Prevents Windows from auto-installing sponsored or suggested apps.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 0) == 1 },

            new() { Id = "searchhighlights", Name = "Disable Search Highlights", Description = "Removes trending and curated content from search.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 1) == 0 },

            new() { Id = "searchboxsuggestions", Name = "Disable Start Menu Suggestions", Description = "Removes app suggestions and recommendations from Start.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 1) == 0 },

            new() { Id = "widgets", Name = "Disable Widgets", Description = "Removes the Widgets button from the taskbar and stops its feed.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 1);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 1) == 0 },

            new() { Id = "stickykeys", Name = "Disable Sticky Keys Prompt", Description = "Stops the Sticky Keys dialog from appearing when Shift is pressed 5 times.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetString(@"HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys", "Flags", "506"),
                RevertAction = () => RegistryHelper.SetString(@"HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys", "Flags", "510"),
                CheckAction = () => RegistryHelper.GetString(@"HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys", "Flags", "510") == "506" },

            new() { Id = "windowstips", Name = "Disable Windows Tips", Description = "Stops Windows from showing tips, ads and recommendations.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContentEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "FeatureManagementEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowOnlineTips", 0);
                },
                RevertAction = () =>
                {
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContentEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "FeatureManagementEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 1);
                    RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowOnlineTips", 1);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "AllowOnlineTips", 1) == 0 },

            new() { Id = "lockscreenads", Name = "Disable Lock Screen Ads", Description = "Prevents Spotlight from showing ads and suggestions on the lock screen.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", 1) == 0 },

            new() { Id = "autoplay", Name = "Disable AutoPlay", Description = "Stops Windows from automatically running programs from inserted media.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 255),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 145),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", 145) == 255 },

            new() { Id = "numlock", Name = "NumLock on at Startup", Description = "Enables NumLock automatically when Windows starts.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetString(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "InitialKeyboardIndicators", "2"),
                RevertAction = () => RegistryHelper.SetString(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "InitialKeyboardIndicators", "0"),
                CheckAction = () => RegistryHelper.GetString(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "InitialKeyboardIndicators", "0") == "2" },

            new() { Id = "noautoreboot", Name = "Prevent Update Auto-Reboot", Description = "Stops Windows from automatically restarting to apply updates while you are logged in.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 0) == 1 },

            new() { Id = "backgroundapps", Name = "Disable Background Apps", Description = "Prevents UWP apps from running or updating in the background.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0) == 1 },

            new() { Id = "remoteassistance", Name = "Disable Remote Assistance", Description = "Prevents others from connecting via Windows Remote Assistance.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 1) == 0 },

            new() { Id = "cloudclipboard", Name = "Disable Cloud Clipboard Sync", Description = "Stops clipboard content from being synced to the cloud.", Category = TweakCategory.Privacy, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Clipboard", "EnableCloudClipboard", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Clipboard", "EnableCloudClipboard", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Clipboard", "EnableCloudClipboard", 1) == 0 },

            new() { Id = "sysmain", Name = "Disable SysMain", Description = "Stops the Superfetch service that preloads apps into RAM.", Category = TweakCategory.Performance, Safety = TweakSafety.Moderate,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SysMain", "Start", 4),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SysMain", "Start", 2),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SysMain", "Start", 2) == 4 },

            new() { Id = "searchindex", Name = "Disable Search Indexing", Description = "Stops Windows from indexing files in the background.", Category = TweakCategory.Performance, Safety = TweakSafety.Moderate,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WSearch", "Start", 4),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WSearch", "Start", 2),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WSearch", "Start", 2) == 4 },

            new() { Id = "deliveryopt", Name = "Disable Delivery Optimization", Description = "Prevents Windows from using your bandwidth to upload updates to other PCs.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1) == 0 },

            new() { Id = "onedrive", Name = "Disable OneDrive", Description = "Stops OneDrive file sync and background integration.", Category = TweakCategory.Performance, Safety = TweakSafety.Moderate,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", 0) == 1 },

            new() { Id = "fileexplorerads", Name = "Disable File Explorer Ads", Description = "Turns off cloud promo banners in File Explorer.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 0),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 1),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSyncProviderNotifications", 1) == 0 },

            new() { Id = "autoexplore", Name = "Disable Explorer Auto Discovery", Description = "Stops Explorer from reclassifying folders and changing their view types.", Category = TweakCategory.System, Safety = TweakSafety.Moderate,
                RunAction = () =>
                {
                    RegistryHelper.DeleteKey(@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags");
                    RegistryHelper.DeleteKey(@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU");
                },
                RevertAction = () =>
                {
                    RegistryHelper.CreateKey(@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags");
                    RegistryHelper.CreateKey(@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU");
                },
                CheckAction = () => !RegistryHelper.KeyExists(@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags") },

            new() { Id = "explorerhomegallery", Name = "Remove Explorer Home & Gallery", Description = "Removes Home and Gallery from Explorer and opens This PC by default.", Category = TweakCategory.System, Safety = TweakSafety.Moderate,
                RunAction = () =>
                {
                    RegistryHelper.DeleteKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}");
                    RegistryHelper.DeleteKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}");
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1);
                },
                RevertAction = () =>
                {
                    RegistryHelper.CreateKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}");
                    RegistryHelper.CreateKey(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}");
                    RegistryHelper.SetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 2);
                },
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 2) == 1 },

            new() { Id = "classiccontextmenu", Name = "Set Classic Right-Click Menu", Description = "Restores the classic Explorer context menu in Windows 11.", Category = TweakCategory.System, Safety = TweakSafety.Safe,
                RunAction = () =>
                {
                    const string keyPath = @"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";
                    RegistryHelper.CreateKey(keyPath);
                    RegistryHelper.SetString(keyPath, "", "");
                },
                RevertAction = () => RegistryHelper.DeleteKey(@"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"),
                CheckAction = () => RegistryHelper.KeyExists(@"HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32") },

            new() { Id = "excludedriverupdates", Name = "Exclude Driver Updates", Description = "Stops Windows Update from pushing driver updates automatically.", Category = TweakCategory.Performance, Safety = TweakSafety.Safe,
                RunAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1),
                RevertAction = () => RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 0),
                CheckAction = () => RegistryHelper.GetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 0) == 1 },
        };
    }

    public static List<DebloatApp> CreateApps()
    {
        return new List<DebloatApp>
        {
            new() { Id = "clipchamp", Name = "Clipchamp", Description = "Microsoft bundled video editor.", Category = AppCategory.Microsoft, PackageName = "Clipchamp.Clipchamp", WinGetId = "9P1J8S7CCWWT", WingetSource = "msstore" },
            new() { Id = "todo", Name = "Microsoft To Do", Description = "Task manager app bundled with Windows.", Category = AppCategory.Microsoft, PackageName = "Microsoft.Todos", WinGetId = "9NBLGGH5R558", WingetSource = "msstore" },
            new() { Id = "bingweather", Name = "MSN Weather", Description = "Bing-powered weather app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.BingWeather", WinGetId = "9WZDNCRFJ3Q2", WingetSource = "msstore" },
            new() { Id = "bingnews", Name = "Microsoft News", Description = "Bing-powered news feed.", Category = AppCategory.Microsoft, PackageName = "Microsoft.BingNews", WinGetId = "9WZDNCRFHVFW", WingetSource = "msstore" },
            new() { Id = "feedback", Name = "Feedback Hub", Description = "Microsoft feedback collection app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.WindowsFeedbackHub", WinGetId = "9NBLGGH4R32N", WingetSource = "msstore" },
            new() { Id = "teams", Name = "Microsoft Teams", Description = "Consumer chat and meeting app.", Category = AppCategory.Microsoft, PackageName = "MSTeams", WinGetId = "XP8BT8DW290MPQ", WingetSource = "msstore" },
            new() { Id = "onenote", Name = "OneNote", Description = "Microsoft note-taking app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.Office.OneNote", WinGetId = "XPFFZHVGQWWLHB", WingetSource = "msstore" },
            new() { Id = "mixedreality", Name = "Mixed Reality Portal", Description = "Windows Mixed Reality launcher app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.MixedReality.Portal", WinGetId = "9NG1H8B3ZC7M", WingetSource = "msstore" },
            new() { Id = "journal", Name = "Microsoft Journal", Description = "Pen-focused note app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.MicrosoftJournal", WinGetId = "9N318R854RHH", WingetSource = "msstore" },
            new() { Id = "stickynotes", Name = "Sticky Notes", Description = "Cloud-synced note app bundled with Windows.", Category = AppCategory.Microsoft, PackageName = "Microsoft.MicrosoftStickyNotes", WinGetId = "9NBLGGH4QGHW", WingetSource = "msstore" },
            new() { Id = "viewer3d", Name = "3D Viewer", Description = "Legacy 3D model viewer.", Category = AppCategory.Microsoft, PackageName = "Microsoft.Microsoft3DViewer", WinGetId = "9NBLGGH42THS", WingetSource = "msstore" },
            new() { Id = "outlook", Name = "Outlook for Windows", Description = "Microsoft consumer email app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.OutlookForWindows", WinGetId = "9NRX63209R7B", WingetSource = "msstore" },
            new() { Id = "whiteboard", Name = "Microsoft Whiteboard", Description = "Collaborative whiteboard app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.Whiteboard", WinGetId = "9MSPC6MP8FM4", WingetSource = "msstore" },
            new() { Id = "maps", Name = "Windows Maps", Description = "Built-in map and location app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.WindowsMaps", WinGetId = "9NBLGGH6JZ60", WingetSource = "msstore" },
            new() { Id = "soundrecorder", Name = "Sound Recorder", Description = "Windows voice recording app.", Category = AppCategory.Microsoft, PackageName = "Microsoft.WindowsSoundRecorder", WinGetId = "9WZDNCRFHWKN", WingetSource = "msstore" },
            new() { Id = "copilotapp", Name = "Copilot", Description = "Microsoft AI assistant embedded into Windows.", Category = AppCategory.AI, PackageName = "Microsoft.Copilot", WinGetId = "XP9CXNGPPJ97XX", WingetSource = "msstore" },
            new() { Id = "paint_ai", Name = "Paint Cocreator", Description = "AI image generation built into Paint.", Category = AppCategory.AI, PackageName = "Microsoft.Paint", WinGetId = "9PCFS5B6T72H", WingetSource = "msstore" },
            new() { Id = "xboxapp", Name = "Xbox App", Description = "Xbox companion app with background services.", Category = AppCategory.Games, PackageName = "Microsoft.GamingApp", WinGetId = "9MV0B5HZVK9Z", WingetSource = "msstore" },
            new() { Id = "spotify", Name = "Spotify", Description = "Music streaming app.", Category = AppCategory.ThirdParty, PackageName = "SpotifyAB.SpotifyMusic", WinGetId = "Spotify.Spotify", WingetSource = "winget" },
        };
    }
}
