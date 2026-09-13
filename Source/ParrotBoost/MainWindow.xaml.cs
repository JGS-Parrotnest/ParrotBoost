using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.CompilerServices;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ParrotBoost;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const uint RecycleBinNoConfirmation = 0x00000001;
    private const uint RecycleBinNoProgressUi = 0x00000002;
    private const uint RecycleBinNoSound = 0x00000004;

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private bool _isBoostActive = false;
    private UserSettings _settings = new();
    private readonly HardwareTelemetryService _hardwareTelemetry;
    private readonly ParrotBoostRuntimeOptimizer _runtimeOptimizer;
    private readonly BoostImpactPredictor _boostImpactPredictor = new();
    private readonly WindowsSecurityActivityMonitor _securityActivityMonitor = new();
    private readonly WindowsCompatibilityProfile _compatibilityProfile = WindowsCompatibilityProfile.Current;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _syncRoot = new();
#else
    private readonly object _syncRoot = new();
#endif
    private NotifyIcon? _notifyIcon;
    private string _gpuManufacturer = "Unknown";
    private string? _driverDownloadUrl;
    private System.Windows.Threading.DispatcherTimer? _performanceTimer;
    private Stack<string> _navigationStack = new();
    private int _cpuTempReadFailures;
    private int _gpuTempReadFailures;
    private DateTime _lastOptimizationLogUtc = DateTime.MinValue;
    private DateTime _lastCriticalToastUtc = DateTime.MinValue;
    private readonly GameModeService _gameModeService = new();
    private int _performanceRefreshInFlight;
    private BoostImpactPrediction _latestBoostPrediction = BoostImpactPrediction.Pending;
    private bool _boostWorkflowStatusActive;
    private int _cleanupOperationInFlight;
    private readonly CancellationTokenSource _startupWorkCancellation = new();
    private UpdateCheckResult? _latestUpdateResult;

    public bool IsBoostActive
    {
        get => _isBoostActive;
        set
        {
            if (_isBoostActive != value)
            {
                _isBoostActive = value;
                OnPropertyChanged();
                UpdateBoostUI();
            }
        }
    }

    private readonly GpuDriverUpdateService _driverService = new();

    public MainWindow()
    {
        InitializeComponent();
        _hardwareTelemetry = new HardwareTelemetryService(_compatibilityProfile);
        _hardwareTelemetry.CriticalTemperatureDetected += OnCriticalTemperatureDetected;
        _runtimeOptimizer = new ParrotBoostRuntimeOptimizer(_compatibilityProfile);
        DataContext = this;
        
        _settings = SettingsManager.Load();
        ApplySettings();
        
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateRootClip();
        
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        
        IsManipulationEnabled = true;
        ManipulationStarting += (s, ev) => ev.ManipulationContainer = this;
        ManipulationCompleted += MainWindow_ManipulationCompleted;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int preference = DwmwcpRound;
                DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            }
        }
        catch
        {
        }
    }

    private async Task CheckForDriverUpdatesAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var updates = await _driverService.CheckForUpdatesAsync();
            var availableUpdates = updates.Where(u => u.UpdateAvailable).ToList();

            if (availableUpdates.Any())
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    string msg = string.Join(", ", availableUpdates.Select(u => u.Manufacturer));
                    _driverDownloadUrl = availableUpdates[0].DownloadUrl;
                    GpuDriverBtn.Content = $"Update GPU Drivers: {msg}";
                    GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E67E22"));
                    GpuDriverBtn.FontWeight = FontWeights.Bold;
                    
                    GpuDriverBtn.ToolTip = "New GPU drivers available:\n" + 
                        string.Join("\n", availableUpdates.Select(u => 
                        $"{u.Manufacturer}: {u.InstalledVersion} -> {u.LatestVersion}"));
                });
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _driverDownloadUrl = null;
                    GpuDriverBtn.Content = "GPU drivers up to date";
                    GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2ECC71"));
                    GpuDriverBtn.FontWeight = FontWeights.SemiBold;
                    GpuDriverBtn.ToolTip = "All GPU drivers are up to date.";
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for driver updates.");
            await Dispatcher.InvokeAsync(() =>
            {
                GpuDriverBtn.Content = "Check GPU driver updates";
                GpuDriverBtn.ToolTip = "Click to check for GPU driver updates.";
            });
        }
        finally
        {
            Logger.Debug("Driver update check completed in {0} ms", stopwatch.ElapsedMilliseconds);
        }
    }

    private void MainWindow_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        if (e.TotalManipulation.Translation.X > 100 && Math.Abs(e.TotalManipulation.Translation.Y) < 50)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                Settings_Back_Click(sender!, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                Settings_Back_Click(sender, e);
                e.Handled = true;
            }
        }
    }

    private void SetupPerformanceTimer()
    {
        float lastCpuLoad = _settings.LastCpuLoad;
        float lastGpuLoad = _settings.LastGpuLoad;
        float lastCpuTemp = _settings.LastCpuTemp;
        float lastGpuTemp = _settings.LastGpuTemp;

        CpuLoadBar.Value = lastCpuLoad;
        GpuLoadBar.Value = lastGpuLoad;
        CpuLoadText.Text = $"{(int)lastCpuLoad}%";
        GpuLoadText.Text = $"{(int)lastGpuLoad}%";
        CpuTempText.Text = HardwareTelemetryService.FormatTemperature(lastCpuTemp);
        GpuTempText.Text = HardwareTelemetryService.FormatTemperature(lastGpuTemp);
        RefreshBoostPresentation();

        _performanceTimer = new System.Windows.Threading.DispatcherTimer();
        _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
        _performanceTimer.Tick += PerformanceTimer_Tick;
        _ = StartPerformanceTimerAsync(_startupWorkCancellation.Token);
    }

    private async Task StartPerformanceTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _performanceTimer?.Start();
                PerformanceTimer_Tick(null, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void PerformanceTimer_Tick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _performanceRefreshInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            if (ShouldThrottleForWindowsDefender())
            {
                return;
            }

            var boostPlan = CaptureBoostPlanConfiguration();
            var sample = await Task.Run(() =>
            {
                float cpuLoad = _hardwareTelemetry.TryGetCpuLoad() ?? 0;
                float gpuLoad = _hardwareTelemetry.TryGetGpuLoad() ?? 0;
                float? cpuTemp = _hardwareTelemetry.TryGetCpuTemperature();
                float? gpuTemp = _hardwareTelemetry.TryGetGpuTemperature();
                var optimizationSnapshot = _runtimeOptimizer.UpdateRuntimeProfile(cpuLoad, gpuLoad, cpuTemp, gpuTemp);
                var boostPrediction = _boostImpactPredictor.Predict(boostPlan, cpuLoad, gpuLoad, cpuTemp, gpuTemp);

                cpuLoad = Compatibility.Clamp(cpuLoad, 0, 100);
                gpuLoad = Compatibility.Clamp(gpuLoad, 0, 100);

                float displayCpuTemp = cpuTemp ?? 0;
                float displayGpuTemp = gpuTemp ?? 0;

                if (cpuTemp.HasValue && cpuTemp.Value > 0) _cpuTempReadFailures = 0;
                else if (++_cpuTempReadFailures >= 3) displayCpuTemp = 0;

                if (gpuTemp.HasValue && gpuTemp.Value > 0) _gpuTempReadFailures = 0;
                else if (++_gpuTempReadFailures >= 3) displayGpuTemp = 0;

                return (cpuLoad, gpuLoad, displayCpuTemp, displayGpuTemp, optimizationSnapshot, boostPrediction);
            });

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                return;
            }

            AnimateLoad(CpuLoadBar, CpuLoadText, sample.cpuLoad);
            AnimateLoad(GpuLoadBar, GpuLoadText, sample.gpuLoad);
            CpuTempText.Text = HardwareTelemetryService.FormatTemperature(sample.displayCpuTemp);
            GpuTempText.Text = HardwareTelemetryService.FormatTemperature(sample.displayGpuTemp);
            CpuLoadBar.Foreground = GetLoadBrush(sample.cpuLoad);
            GpuLoadBar.Foreground = GetLoadBrush(sample.gpuLoad);

            _settings.LastCpuLoad = sample.cpuLoad;
            _settings.LastGpuLoad = sample.gpuLoad;
            _settings.LastCpuTemp = sample.displayCpuTemp;
            _settings.LastGpuTemp = sample.displayGpuTemp;

            _latestBoostPrediction = sample.boostPrediction;
            RefreshBoostPresentation();

            if (DateTime.UtcNow - _lastOptimizationLogUtc > TimeSpan.FromSeconds(30))
            {
                _lastOptimizationLogUtc = DateTime.UtcNow;
                Logger.Debug("Telemetry heartbeat: CPU {0:F1}% ({1:F1}C), GPU {2:F1}% ({3:F1}C)",
                    sample.cpuLoad,
                    sample.displayCpuTemp,
                    sample.gpuLoad,
                    sample.displayGpuTemp);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error refreshing performance stats.");
        }
        finally
        {
            Interlocked.Exchange(ref _performanceRefreshInFlight, 0);
        }
    }

    private void RefreshBoostPresentation()
    {
        if (_boostWorkflowStatusActive)
        {
            return;
        }

        if (IsBoostActive)
        {
            BoostPercentage.Text = "ACTIVE";
            BoostPercentage.FontSize = 28;
            BoostPredictionDetails.Text = LocalizationManager.Instance.GetString("MainWindow.BoostActive");
            return;
        }

        BoostPercentage.FontSize = 36;
        BoostPercentage.Text = _latestBoostPrediction.RangeLabel;
        BoostPredictionDetails.Text = _latestBoostPrediction.Details;
    }

    private void AnimateLoad(System.Windows.Controls.ProgressBar bar, TextBlock textBlock, double targetValue)
    {
        double current = bar.Value;
        var anim = new DoubleAnimation
        {
            From = current,
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.CurrentTimeInvalidated += (s, _) =>
        {
            if (bar.Value >= 0)
            {
                textBlock.Text = $"{(int)Math.Round(bar.Value)}%";
            }
        };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    private BoostPlanConfiguration CaptureBoostPlanConfiguration()
    {
        return new BoostPlanConfiguration(
            _settings.OptServices,
            _settings.OptMemory,
            _settings.OptTasks,
            _settings.OptNtfs,
            _settings.OptPriority,
            _settings.OptUsb,
            _settings.OptDelivery,
            _settings.OptTick,
            _settings.EnableGameMode);
    }

    private bool ShouldThrottleForWindowsDefender()
    {
        return _securityActivityMonitor.IsDefenderBusy(15f, TimeSpan.FromSeconds(2));
    }

    private void ApplySettings()
    {
        LocalizationManager.Instance.SetLanguage(_settings.Language);
        ApplyTheme(_settings.IsDarkMode);
        
        LaunchAtStartupCheck.IsChecked = _settings.LaunchAtStartup;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        CheckUpdatesOnStartupCheck.IsChecked = _settings.CheckForUpdatesOnStartup;

        OptBlockDeliveryUploadCheck.IsChecked = _settings.OptBlockDeliveryUpload;
        OptDisableTelemetryTasksCheck.IsChecked = _settings.OptDisableTelemetryTasks;
        OptDisableAdvertisingIdCheck.IsChecked = _settings.OptDisableAdvertisingId;
        OptDisableDiagnosticDataCheck.IsChecked = _settings.OptDisableDiagnosticData;
        OptDisableActivityHistoryCheck.IsChecked = _settings.OptDisableActivityHistory;
        OptDisableLocationTrackingCheck.IsChecked = _settings.OptDisableLocationTracking;
        OptDisableFeedbackNotificationsCheck.IsChecked = _settings.OptDisableFeedbackNotifications;
        OptDisableCopilotCheck.IsChecked = _settings.OptDisableCopilot;
        OptDisableRecallCheck.IsChecked = _settings.OptDisableRecall;
        OptDisableClickToDoCheck.IsChecked = _settings.OptDisableClickToDo;
        OptDisableGenerativeSearchAiCheck.IsChecked = _settings.OptDisableGenerativeSearchAi;
        OptDisableAppAiFeaturesCheck.IsChecked = _settings.OptDisableAppAiFeatures;
        OptDisableEdgeAiCheck.IsChecked = _settings.OptDisableEdgeAi;
        OptServicesCheck.IsChecked = _settings.OptServices;
        OptMemoryCheck.IsChecked = _settings.OptMemory;
        OptTasksCheck.IsChecked = _settings.OptTasks;
        OptNtfsCheck.IsChecked = _settings.OptNtfs;
        OptPriorityCheck.IsChecked = _settings.OptPriority;
        OptUsbCheck.IsChecked = _settings.OptUsb;
        OptDeliveryCheck.IsChecked = _settings.OptDelivery;
        OptTickCheck.IsChecked = _settings.OptTick;
        GameModeCheck.IsChecked = _settings.EnableGameMode;

        CleanPrefetchCheck.IsChecked = _settings.CleanPrefetch;
        CleanTempCheck.IsChecked = _settings.CleanTemp;
        CleanWinTempCheck.IsChecked = _settings.CleanWinTemp;
        CleanUpdateCacheCheck.IsChecked = _settings.CleanUpdateCache;
        CleanRecycleBinCheck.IsChecked = _settings.CleanRecycleBin;
        CleanBrowserCacheCheck.IsChecked = _settings.CleanBrowserCache;
        CleanThumbnailsCheck.IsChecked = _settings.CleanThumbnails;
        CleanErrorReportingCheck.IsChecked = _settings.CleanErrorReporting;
        CleanSystemLogsCheck.IsChecked = _settings.CleanSystemLogs;

        foreach (ComboBoxItem item in LanguageSelector.Items)
        {
            if (item.Tag?.ToString() == _settings.Language)
            {
                LanguageSelector.SelectedItem = item;
                break;
            }
        }
    }

    private void SaveSettings()
    {
        _settings.LaunchAtStartup = LaunchAtStartupCheck.IsChecked ?? false;
        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked ?? true;
        _settings.CheckForUpdatesOnStartup = CheckUpdatesOnStartupCheck.IsChecked ?? true;

        _settings.OptBlockDeliveryUpload = OptBlockDeliveryUploadCheck.IsChecked ?? true;
        _settings.OptDisableTelemetryTasks = OptDisableTelemetryTasksCheck.IsChecked ?? true;
        _settings.OptDisableAdvertisingId = OptDisableAdvertisingIdCheck.IsChecked ?? true;
        _settings.OptDisableDiagnosticData = OptDisableDiagnosticDataCheck.IsChecked ?? true;
        _settings.OptDisableActivityHistory = OptDisableActivityHistoryCheck.IsChecked ?? true;
        _settings.OptDisableLocationTracking = OptDisableLocationTrackingCheck.IsChecked ?? true;
        _settings.OptDisableFeedbackNotifications = OptDisableFeedbackNotificationsCheck.IsChecked ?? true;
        _settings.OptDisableCopilot = OptDisableCopilotCheck.IsChecked ?? true;
        _settings.OptDisableRecall = OptDisableRecallCheck.IsChecked ?? true;
        _settings.OptDisableClickToDo = OptDisableClickToDoCheck.IsChecked ?? true;
        _settings.OptDisableGenerativeSearchAi = OptDisableGenerativeSearchAiCheck.IsChecked ?? true;
        _settings.OptDisableAppAiFeatures = OptDisableAppAiFeaturesCheck.IsChecked ?? true;
        _settings.OptDisableEdgeAi = OptDisableEdgeAiCheck.IsChecked ?? true;
        _settings.OptServices = OptServicesCheck.IsChecked ?? true;
        _settings.OptMemory = OptMemoryCheck.IsChecked ?? true;
        _settings.OptTasks = OptTasksCheck.IsChecked ?? true;
        _settings.OptNtfs = OptNtfsCheck.IsChecked ?? true;
        _settings.OptPriority = OptPriorityCheck.IsChecked ?? true;
        _settings.OptUsb = OptUsbCheck.IsChecked ?? true;
        _settings.OptDelivery = OptDeliveryCheck.IsChecked ?? true;
        _settings.OptTick = OptTickCheck.IsChecked ?? true;
        _settings.EnableGameMode = GameModeCheck.IsChecked ?? false;

        _settings.CleanPrefetch = CleanPrefetchCheck.IsChecked ?? true;
        _settings.CleanTemp = CleanTempCheck.IsChecked ?? true;
        _settings.CleanWinTemp = CleanWinTempCheck.IsChecked ?? true;
        _settings.CleanUpdateCache = CleanUpdateCacheCheck.IsChecked ?? true;
        _settings.CleanRecycleBin = CleanRecycleBinCheck.IsChecked ?? true;
        _settings.CleanBrowserCache = CleanBrowserCacheCheck.IsChecked ?? true;
        _settings.CleanThumbnails = CleanThumbnailsCheck.IsChecked ?? true;
        _settings.CleanErrorReporting = CleanErrorReportingCheck.IsChecked ?? true;
        _settings.CleanSystemLogs = CleanSystemLogsCheck.IsChecked ?? true;

        SettingsManager.Save(_settings);
        ApplyPrivacyTweaks();
        DisableMicrosoftAiSlop();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || IsInteractiveTitleBarElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        DragMove();
    }

    private static bool IsInteractiveTitleBarElement(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.IsDarkMode = !_settings.IsDarkMode;
        ApplyTheme(_settings.IsDarkMode);
        SettingsManager.Save(_settings);
    }

    private void ApplyTheme(bool isDark)
    {
        if (isDark)
        {
            Resources["WindowBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 20, 25));
            Resources["CardBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 30, 35));
            Resources["TextPrimary"] = System.Windows.Media.Brushes.White;
            Resources["TextSecondary"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 170, 180));
            Resources["BorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 50, 55));
            Resources["HeaderBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 20, 25));
            Resources["BoostButtonBorderBrush"] = System.Windows.Media.Brushes.White;
            if (ThemeBtn != null) 
            {
                var textBlock = ThemeBtn.Content as TextBlock;
                if (textBlock != null) textBlock.Text = "";
            }
            if (CloseButton != null) CloseButton.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            Resources["WindowBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 250));
            Resources["CardBackground"] = System.Windows.Media.Brushes.White;
            Resources["TextPrimary"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 52, 54));
            Resources["TextSecondary"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(99, 110, 114));
            Resources["BorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(223, 230, 233));
            Resources["HeaderBackground"] = System.Windows.Media.Brushes.White;
            Resources["BoostButtonBorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 28));
            if (ThemeBtn != null) 
            {
                var textBlock = ThemeBtn.Content as TextBlock;
                if (textBlock != null) textBlock.Text = "";
            }
            if (CloseButton != null) CloseButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 52, 54));
        }
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new NotifyIcon();
        try
        {
            var iconUri = new Uri("pack://application:,,,/logo.ico");
            var iconStream = System.Windows.Application.GetResourceStream(iconUri)?.Stream;
            if (iconStream != null)
            {
                _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load tray icon from resources.");
        }
        
        _notifyIcon.Visible = true;
        _notifyIcon.Text = "ParrotBoost";
        
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Show", null, (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
            if (_performanceTimer != null) _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
            _hardwareTelemetry.SetBackgroundMode(false);
        });
        contextMenu.Items.Add("Boost ON/OFF", null, async (s, e) => await Dispatcher.InvokeAsync(ToggleBoostAsync));
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Exit", null, (s, e) => { System.Windows.Application.Current.Shutdown(); });
        
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
            if (_performanceTimer != null) _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
            _hardwareTelemetry.SetBackgroundMode(false);
        };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (_settings.MinimizeToTray)
            {
                Hide();
            }
            if (_performanceTimer != null)
            {
                _performanceTimer.Interval = TimeSpan.FromSeconds(4);
            }
            _hardwareTelemetry.SetBackgroundMode(true);
        }
        else
        {
            if (_performanceTimer != null)
            {
                _performanceTimer.Interval = _compatibilityProfile.TelemetryRefreshInterval;
            }
            _hardwareTelemetry.SetBackgroundMode(false);
        }

        base.OnStateChanged(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateRootClip();

        var startupTimer = Stopwatch.StartNew();
        using var backgroundScope = SystemExecutionProfile.TryEnterBackgroundProcessingMode();

        await InitializeDeferredStartupAsync();

        var minimumSplash = TimeSpan.FromMilliseconds(300);
        if (startupTimer.Elapsed < minimumSplash)
        {
            await Task.Delay(minimumSplash - startupTimer.Elapsed);
        }

        var fadeAnim = new DoubleAnimation(0, TimeSpan.FromSeconds(0.4));
        fadeAnim.Completed += (s, _) =>
        {
            SplashOverlay.IsHitTestVisible = false;
            SplashOverlay.Visibility = Visibility.Collapsed;
        };
        SplashOverlay.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private async Task InitializeDeferredStartupAsync()
    {
        var startupProfile = Stopwatch.StartNew();
        DetectOptimizationState();
        SetupPerformanceTimer();
        SetupTrayIcon();
        ScheduleDeferredStartupWork();
        Logger.Debug("Deferred startup path initialized in {0} ms (Win10Compat={1})", startupProfile.ElapsedMilliseconds, _compatibilityProfile.IsWindows10);
        await Task.CompletedTask;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _startupWorkCancellation.Cancel();
        _performanceTimer?.Stop();
        _notifyIcon?.Dispose();
        _hardwareTelemetry.CriticalTemperatureDetected -= OnCriticalTemperatureDetected;
        _hardwareTelemetry.Dispose();
        _startupWorkCancellation.Dispose();
        base.OnClosed(e);
    }

    private void ScheduleDeferredStartupWork()
    {
        _ = RunDeferredStartupWorkAsync(_startupWorkCancellation.Token);
    }

    private async Task RunDeferredStartupWorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await LoadHardwareInfoAsync();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await CheckForDriverUpdatesAsync();
            }

            if (_settings.CheckForUpdatesOnStartup && !cancellationToken.IsCancellationRequested)
            {
                await CheckApplicationUpdatesAsync(manualTrigger: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DelayWithDefenderAwarenessAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            if (ShouldThrottleForWindowsDefender())
            {
                delay += TimeSpan.FromSeconds(1);
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task CheckApplicationUpdatesAsync(bool manualTrigger = false)
    {
        try
        {
            var result = await ApplicationUpdateService.CheckForUpdatesAsync();
            if (result.IsUpdateAvailable)
            {
                _latestUpdateResult = result;
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateNotificationText.Text = $"Dostępna nowa wersja ParrotBoost: v{result.LatestVersion}!";
                    UpdateNotificationBar.Visibility = Visibility.Visible;
                });
            }
            else if (manualTrigger)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show($"Posiadasz najnowszą wersję ParrotBoost (v{ApplicationUpdateService.CurrentVersion}).", "Aktualizacje", MessageBoxButton.OK, MessageBoxImage.Information);
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update check failed");
            if (manualTrigger)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show($"Nie udało się sprawdzić aktualizacji: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        }
    }

    private async void UpdateNowBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateNowBtn.IsEnabled = false;
        UpdateNowBtn.Content = "Pobieranie...";
        try
        {
            var progress = new Progress<int>(percent =>
            {
                UpdateNowBtn.Content = $"Pobieranie {percent}%...";
            });

            bool success = await ApplicationUpdateService.DownloadAndInstallUpdateAsync(_latestUpdateResult?.DownloadUrl, progress);
            if (success)
            {
                SaveSettings();
                System.Windows.Application.Current.Shutdown();
            }
            else
            {
                UpdateNowBtn.IsEnabled = true;
                UpdateNowBtn.Content = "Zaktualizuj";
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update launch failed");
            UpdateNowBtn.IsEnabled = true;
            UpdateNowBtn.Content = "Zaktualizuj";
        }
    }

    private void DismissUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateNotificationBar.Visibility = Visibility.Collapsed;
    }

    private async void CheckUpdatesNowButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesNowButton.IsEnabled = false;
        await CheckApplicationUpdatesAsync(manualTrigger: true);
        CheckUpdatesNowButton.IsEnabled = true;
    }

    private async void CleanRam_Click(object sender, RoutedEventArgs e)
    {
        CleanRamButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                var processes = Process.GetProcesses();
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            EmptyWorkingSet(process.Handle);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            });

            System.Windows.MessageBox.Show("Pamięć RAM została pomyślnie wyczyszczona!", "ParrotBoost", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to clean RAM");
        }
        finally
        {
            CleanRamButton.IsEnabled = true;
        }
    }

    private async Task LoadHardwareInfoAsync()
    {
        var inventory = await Task.Run(() =>
        {
            string cpuName = "Unknown CPU";
            List<string> dedicatedGpus = [];
            List<string> integratedGpus = [];
            string gpuVendor = "Unknown";
            string ramDetails = "Unknown RAM";
            List<string> diskDrives = [];

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    string rawName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(rawName))
                    {
                        cpuName = rawName;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading CPU info.");
            }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility FROM Win32_VideoController");
                foreach (var obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                    string compat = obj["AdapterCompatibility"]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("RDP", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("TeamViewer", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isIntegrated = name.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                        (name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Iris", StringComparison.OrdinalIgnoreCase));

                    if (!isIntegrated && (name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("Vega", StringComparison.OrdinalIgnoreCase) ||
                                          name.Contains("APU", StringComparison.OrdinalIgnoreCase)))
                    {
                        isIntegrated = true;
                    }

                    if (isIntegrated)
                    {
                        if (!integratedGpus.Contains(name))
                        {
                            integratedGpus.Add(name);
                        }
                    }
                    else
                    {
                        if (!dedicatedGpus.Contains(name))
                        {
                            dedicatedGpus.Add(name);
                            if (gpuVendor == "Unknown" && !string.IsNullOrWhiteSpace(compat))
                            {
                                gpuVendor = compat;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading GPU info.");
            }

            try
            {
                ulong totalBytes = 0;
                uint maxSpeed = 0;
                string ddr = string.Empty;

                using var searcher = new ManagementObjectSearcher("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber FROM Win32_PhysicalMemory");
                foreach (var obj in searcher.Get())
                {
                    if (obj["Capacity"] != null)
                    {
                        totalBytes += Convert.ToUInt64(obj["Capacity"]);
                    }

                    uint speed = 0;
                    if (obj["ConfiguredClockSpeed"] != null)
                    {
                        speed = Convert.ToUInt32(obj["ConfiguredClockSpeed"]);
                    }
                    else if (obj["Speed"] != null)
                    {
                        speed = Convert.ToUInt32(obj["Speed"]);
                    }

                    if (speed > maxSpeed)
                    {
                        maxSpeed = speed;
                    }

                    if (string.IsNullOrEmpty(ddr))
                    {
                        uint smbiosType = obj["SMBIOSMemoryType"] != null ? Convert.ToUInt32(obj["SMBIOSMemoryType"]) : 0;
                        ddr = smbiosType switch
                        {
                            20 => "DDR",
                            21 => "DDR2",
                            24 => "DDR3",
                            26 => "DDR4",
                            30 => "LPDDR4",
                            34 => "DDR5",
                            35 => "LPDDR5",
                            _ => string.Empty
                        };

                        if (string.IsNullOrEmpty(ddr))
                        {
                            string part = obj["PartNumber"]?.ToString() ?? string.Empty;
                            if (part.Contains("DDR5", StringComparison.OrdinalIgnoreCase)) ddr = "DDR5";
                            else if (part.Contains("DDR4", StringComparison.OrdinalIgnoreCase)) ddr = "DDR4";
                            else if (part.Contains("DDR3", StringComparison.OrdinalIgnoreCase)) ddr = "DDR3";
                        }
                    }
                }

                if (totalBytes == 0)
                {
                    using var csSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                    foreach (var obj in csSearcher.Get())
                    {
                        if (obj["TotalPhysicalMemory"] != null)
                        {
                            totalBytes = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                        }
                        break;
                    }
                }

                double gb = Math.Round((double)totalBytes / (1024.0 * 1024.0 * 1024.0));
                if (string.IsNullOrEmpty(ddr) && maxSpeed > 0)
                {
                    if (maxSpeed >= 4400) ddr = "DDR5";
                    else if (maxSpeed >= 2133) ddr = "DDR4";
                    else if (maxSpeed >= 800) ddr = "DDR3";
                }

                string speedSuffix = maxSpeed > 0 ? $" ({maxSpeed} MHz)" : string.Empty;
                string ddrPart = !string.IsNullOrEmpty(ddr) ? $" {ddr}" : string.Empty;
                ramDetails = $"{gb} GB{ddrPart}{speedSuffix}";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading RAM info.");
            }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Model, Size FROM Win32_DiskDrive");
                foreach (var obj in searcher.Get())
                {
                    string model = obj["Model"]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(model)) continue;

                    string sizeLabel = string.Empty;
                    if (obj["Size"] != null)
                    {
                        ulong bytes = Convert.ToUInt64(obj["Size"]);
                        double gb = bytes / (1000.0 * 1000.0 * 1000.0);
                        if (gb >= 900)
                        {
                            sizeLabel = $"{Math.Round(gb / 1000.0, 0)} TB";
                        }
                        else if (gb >= 1)
                        {
                            sizeLabel = $"{Math.Round(gb, 0)} GB";
                        }
                    }

                    string entry = !string.IsNullOrEmpty(sizeLabel) ? $"{model} ({sizeLabel})" : model;
                    if (!diskDrives.Contains(entry))
                    {
                        diskDrives.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reading Disk info.");
            }

            return (cpuName, dedicatedGpus, integratedGpus, ramDetails, gpuVendor, diskDrives);
        });

        CpuInfo.Text = $"CPU: {inventory.cpuName}";

        if (inventory.dedicatedGpus.Count > 0)
        {
            GpuInfo.Text = $"GPU: {string.Join(", ", inventory.dedicatedGpus)}";
            if (inventory.integratedGpus.Count > 0)
            {
                IgpuInfo.Text = $"iGPU: {string.Join(", ", inventory.integratedGpus)}";
                IgpuInfo.Visibility = Visibility.Visible;
            }
            else
            {
                IgpuInfo.Visibility = Visibility.Collapsed;
            }
        }
        else if (inventory.integratedGpus.Count > 0)
        {
            GpuInfo.Text = $"GPU: {string.Join(", ", inventory.integratedGpus)}";
            IgpuInfo.Visibility = Visibility.Collapsed;
        }
        else
        {
            GpuInfo.Text = "GPU: Unknown";
            IgpuInfo.Visibility = Visibility.Collapsed;
        }

        RamInfo.Text = $"RAM: {inventory.ramDetails}";

        if (inventory.diskDrives.Count == 1)
        {
            DiskInfo.Text = $"Disk: {inventory.diskDrives[0]}";
            DiskInfo.Visibility = Visibility.Visible;
        }
        else if (inventory.diskDrives.Count > 1)
        {
            DiskInfo.Text = string.Join(Environment.NewLine, inventory.diskDrives.Select((d, idx) => $"Disk {idx + 1}: {d}"));
            DiskInfo.Visibility = Visibility.Visible;
        }
        else
        {
            DiskInfo.Visibility = Visibility.Collapsed;
        }

        _gpuManufacturer = inventory.gpuVendor;
    }

    private void UpdateBoostUI()
    {
        if (_isBoostActive)
        {
            BoostButton.Content = LocalizationManager.Instance.GetString("MainWindow.SlowDown");
            BoostButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E74C3C"));
            ProgressStatus.Text = LocalizationManager.Instance.GetString("MainWindow.Optimized");
        }
        else
        {
            BoostButton.Content = LocalizationManager.Instance.GetString("MainWindow.Boost");
            BoostButton.Background = (System.Windows.Media.Brush)FindResource("ColorPrimaryBrush");
            ProgressStatus.Text = LocalizationManager.Instance.GetString("MainWindow.ReadyToBoost");
        }

        RefreshBoostPresentation();
    }

    private async void BoostButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleBoostAsync();
    }

    private async Task ToggleBoostAsync()
    {
        if (!IsBoostActive)
        {
            await RunBoostSequence();
            IsBoostActive = true;
        }
        else
        {
            await RunRestoreSequence();
            IsBoostActive = false;
        }
    }

    private void DetectOptimizationState()
    {
        bool systemEnabled = ParrotBoostSystemConfiguration.IsBoostEnabled() || _settings.ParrotBoostSystemEnabled;
        _settings.ParrotBoostSystemEnabled = systemEnabled;
        ParrotBoostSystemConfiguration.SetBoostEnabled(systemEnabled);
        _runtimeOptimizer.ApplyBoostProfile(systemEnabled);
        _isBoostActive = systemEnabled;
        UpdateBoostUI();
    }

    private async Task RunBoostSequence()
    {
        BoostButton.IsEnabled = false;
        _boostWorkflowStatusActive = true;

        string[] statusSteps = {
            "Game mode",
            "Visuals",
            "Tasks",
            "Timers",
            "Power",
            "Cleanup",
            "Priority"
        };

        foreach (var step in statusSteps)
        {
            ProgressStatus.Text = $"Applying {step}";
            await Task.Run(() => 
            {
                switch (step)
                {
                    case "Game mode": if (_settings.EnableGameMode) _gameModeService.Activate(); break;
                    case "Visuals": if (_settings.OptServices) SetVisualEffects(true); DisableUwpAnimations(true); break;
                    case "Tasks":
                        if (_settings.OptTasks) OptimizeTaskScheduler();
                        if (_settings.OptDelivery || _settings.OptBlockDeliveryUpload) DisableDeliveryOptimization();
                        if (_settings.OptDisableTelemetryTasks) DisableTelemetryScheduledTasks();
                        DisableMicrosoftAiSlop();
                        ApplyPrivacyTweaks();
                        break;
                    case "Timers": if (_settings.OptTick) { SetDynamicTick(false); SetHpet(false); } ClearIconCache(); break;
                    case "Power": CreateTurboParrotPowerPlan(); if (_settings.OptUsb) OptimizeUsbPower(true); SetTimerCoalescing(true); break;
                    case "Cleanup": ClearTempFolders(); break;
                    case "Priority": if (_settings.OptPriority) SetForegroundPriority(true); if (_settings.OptNtfs) DisableNtfsLastAccess(true); break;
                }
            });
            await Task.Delay(300);
        }

        _boostWorkflowStatusActive = false;
        BoostButton.IsEnabled = true;
        RefreshBoostPresentation();
    }

    private void DisableUwpAnimations(bool disable)
    {
        try {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", disable ? 2 : 0, RegistryValueKind.DWord);
        } catch { }
    }

    private void SetHpet(bool enable)
    {
        if (enable) RunCommand("bcdedit", "/deletevalue useplatformclock");
        else RunCommand("bcdedit", "/set useplatformclock false");
    }

    private void SetTimerCoalescing(bool enable)
    {
        RunCommand("powercfg", $"/setacvalueindex scheme_current sub_processor IDLEDISABLE {(enable ? 0 : 1)}");
    }

    private void ClearTempFolders()
    {
        try {
            string tempPath = Path.GetTempPath();
            if (Directory.Exists(tempPath))
            {
                foreach (var file in Directory.GetFiles(tempPath)) try { File.Delete(file); } catch { }
                foreach (var dir in Directory.GetDirectories(tempPath)) try { Directory.Delete(dir, true); } catch { }
            }
        } catch { }
    }

    private async Task RunRestoreSequence()
    {
        BoostButton.IsEnabled = false;
        ProgressStatus.Text = LocalizationManager.Instance.GetString("Boost.Restoring");

        await Task.Run(() =>
        {
            if (_settings.EnableGameMode) _gameModeService.Restore();
            RestoreDefaultPowerPlan();
            RestoreServices();
            SetVisualEffects(false);
            DisableUwpAnimations(false);
            DisableNtfsLastAccess(false);
            SetForegroundPriority(false);
            OptimizeUsbPower(false);
            SetDynamicTick(true);
            SetHpet(true);
            SetTimerCoalescing(false);
            RestoreMicrosoftAiSlop();
        });

        BoostButton.IsEnabled = true;
        RefreshBoostPresentation();
    }

    private void CreateTurboParrotPowerPlan()
    {
        RunCommand("powercfg", "-duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");
        RunCommand("powercfg", "-setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
    }

    private void RestoreDefaultPowerPlan()
    {
        RunCommand("powercfg", "-restoredefaultschemes");
        RunCommand("powercfg", "-setactive 381b4222-f694-41f0-9685-ff5bb260df2e");
    }

    private void OptimizeTaskScheduler()
    {
        string[] tasks = {
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Autochk\Proxy"
        };
        foreach (var task in tasks)
        {
            RunCommand("schtasks", $"/change /tn \"{task}\" /disable");
        }
    }

    private void DisableTelemetryScheduledTasks()
    {
        string[] tasks = {
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"
        };
        foreach (var task in tasks)
        {
            RunCommand("schtasks", $"/change /tn \"{task}\" /disable");
        }
    }

    private void DisableDeliveryOptimization()
    {
        try
        {
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0);
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOMaxUploadBandwidth", 0);
            RegistryHelper.SetDword(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 0);
        }
        catch
        {
        }

        StopAndDisableService("DoSvc");
    }

    private void DisableNtfsLastAccess(bool disable)
    {
        RunCommand("fsutil", $"behavior set disablelastaccess {(disable ? 1 : 0)}");
    }

    private void SetDynamicTick(bool enable)
    {
        RunCommand("bcdedit", $"/set disabledynamictick {(enable ? "no" : "yes")}");
    }

    private void SetForegroundPriority(bool optimize)
    {
        try {
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", optimize ? 26 : 2, RegistryValueKind.DWord);
        } catch { }
    }

    private void OptimizeUsbPower(bool optimize)
    {
        RunCommand("powercfg", $"-SETACVALUEINDEX SCHEME_CURRENT SUB_USB USBSELECTIVE SUSPEND {(optimize ? 0 : 1)}");
        RunCommand("powercfg", $"-SETDCVALUEINDEX SCHEME_CURRENT SUB_USB USBSELECTIVE SUSPEND {(optimize ? 0 : 1)}");
        RunCommand("powercfg", "-SETACTIVE SCHEME_CURRENT");
    }

    private void ClearIconCache()
    {
        RunCommand("ie4uinit.exe", "-ClearIconCache");
    }

    private void SetVisualEffects(bool optimize)
    {
        try {
            string userKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects";
            Registry.SetValue(userKey, "VisualFXSetting", optimize ? 2 : 0, RegistryValueKind.DWord);
        } catch {}
    }

    private void StopAndDisableService(string serviceName)
    {
        try
        {
            using (ServiceController sc = new ServiceController(serviceName))
            {
                if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }

            RunCommand("sc", $"config \"{serviceName}\" start=disabled");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to stop/disable service {serviceName}");
        }
    }

    private void RestoreServices()
    {
        string[] services = { "DiagTrack", "dmwappushservice", "SysMain", "WSearch", "DoSvc" };
        foreach (var svc in services)
        {
            try
            {
                RunCommand("sc", $"config \"{svc}\" start=auto");
                using (ServiceController sc = new ServiceController(svc))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                    }
                }
            }
            catch {}
        }
    }

    private void RunCommand(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Error running {command} {args}");
        }
    }

    private void OnCriticalTemperatureDetected(CriticalTemperatureEvent criticalEvent)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (DateTime.UtcNow - _lastCriticalToastUtc < TimeSpan.FromSeconds(10))
            {
                return;
            }

            _lastCriticalToastUtc = DateTime.UtcNow;
            _notifyIcon?.ShowBalloonTip(3000, "ParrotBoost - Ostrzeżenie", criticalEvent.Message, ToolTipIcon.Warning);
        });
    }

    private SolidColorBrush GetLoadBrush(float load)
    {
        if (load < 50) return (SolidColorBrush)FindResource("ColorPrimaryBrush");
        if (load < 80) return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F1C40F"));
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E74C3C"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("SettingsOverlay");
    }

    private void NavigateTo(string overlayName)
    {
        _navigationStack.Push(overlayName);
        SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsOverlay.Opacity = 1;
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.IsHitTestVisible = true;
        MainDashboard.IsHitTestVisible = false;
    }

    private void Settings_Back_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_navigationStack.Count > 0)
        {
            _navigationStack.Pop();
        }

        SettingsOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsOverlay.Opacity = 0;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.IsHitTestVisible = false;
        MainDashboard.IsHitTestVisible = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void GpuDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TryLaunchVendorGpuApp(_gpuManufacturer))
        {
            return;
        }

        if (!string.IsNullOrEmpty(_driverDownloadUrl))
        {
            Process.Start(new ProcessStartInfo(_driverDownloadUrl) { UseShellExecute = true });
        }
        else
        {
            string searchUrl = _gpuManufacturer.ToLower() switch
            {
                var m when m.Contains("nvidia") => "https://www.nvidia.com/Download/index.aspx",
                var m when m.Contains("amd") || m.Contains("advanced micro") => "https://www.amd.com/en/support",
                var m when m.Contains("intel") => "https://www.intel.com/content/www/us/en/download-center/home.html",
                _ => "https://www.google.com/search?q=" + Uri.EscapeDataString($"{_gpuManufacturer} graphics drivers update")
            };
            Process.Start(new ProcessStartInfo(searchUrl) { UseShellExecute = true });
        }
    }

    private static bool TryLaunchVendorGpuApp(string manufacturer)
    {
        string lower = (manufacturer ?? string.Empty).ToLowerInvariant();
        try
        {
            if (lower.Contains("nvidia"))
            {
                string[] candidates = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvcplui.exe")
                ];

                foreach (var exe in candidates)
                {
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }

                try
                {
                    Process.Start(new ProcessStartInfo("nvidia-app://") { UseShellExecute = true });
                    return true;
                }
                catch
                {
                }
            }
            else if (lower.Contains("amd") || lower.Contains("advanced micro") || lower.Contains("radeon"))
            {
                string[] candidates = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AMD\CNext\CNext\RadeonSoftware.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"AMD\Performance Profile Client\AmdPpc.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"AMD\CNext\CNext\RadeonSoftware.exe")
                ];

                foreach (var exe in candidates)
                {
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }

                try
                {
                    Process.Start(new ProcessStartInfo("amd-software://") { UseShellExecute = true });
                    return true;
                }
                catch
                {
                }
            }
            else if (lower.Contains("intel") || lower.Contains("arc"))
            {
                string[] candidates = [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Intel\Intel Arc Control\ArcControl.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Intel\Arc Control\ArcControl.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Intel\Graphics Command Center\IGCC.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Intel\Graphics Command Center\IGCC.exe")
                ];

                foreach (var exe in candidates)
                {
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                        return true;
                    }
                }

                try
                {
                    Process.Start(new ProcessStartInfo("igcc:") { UseShellExecute = true });
                    return true;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private void DisableMicrosoftAiSlop()
    {
        try
        {
            if (_settings.OptDisableCopilot)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableRecall)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowUserActivityAnalysis", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableClickToDo)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableGenerativeSearchAi)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableAppAiFeatures)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableSuperResolution", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableEdgeAi)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "ComposeInlineEnabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "EdgeEntSearchPageContext", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, RegistryValueKind.DWord);
            }
        }
        catch
        {
        }
    }

    private void ApplyPrivacyTweaks()
    {
        try
        {
            if (_settings.OptDisableAdvertisingId)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableDiagnosticData)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "MaxTelemetryAllowed", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Diagnostics\DiagTrack", "ShowDiagData", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableActivityHistory)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338393Enabled", 0, RegistryValueKind.DWord);
            }

            if (_settings.OptDisableLocationTracking)
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocationScripting", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", RegistryValueKind.String);
            }

            if (_settings.OptDisableFeedbackNotifications)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, RegistryValueKind.DWord);
            }
        }
        catch
        {
        }
    }

    private static void RestoreMicrosoftAiSlop()
    {
        try
        {
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Copilot\BingChat", "IsCopilotAvailable", 1, RegistryValueKind.DWord);

            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0, RegistryValueKind.DWord);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecall");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableRecall");
            RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "SnapshotAnalysisDisabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowUserActivityAnalysis");

            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\ClickToDo", "Enabled", 1, RegistryValueKind.DWord);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo");

            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search", "DeviceHistoryEnabled", 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 1, RegistryValueKind.DWord);
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "ConnectedSearchUseWeb");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableSearchBoxSuggestions");

            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Paint", "DisableCocreator");
            RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Microsoft\Paint", "DisableCocreator");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableGenerativeErase");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableSuperResolution");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Photos", "DisableAI");

            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "CopilotCDPEnabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "ComposeInlineEnabled");
            RegistryHelper.DeleteValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge", "EdgeEntSearchPageContext");
            RegistryHelper.DeleteValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Edge", "HubsSidebarEnabled");
        }
        catch
        {
        }
    }

    private void UpdateRootClip()
    {
        RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _cleanupOperationInFlight, 1) != 0)
        {
            return;
        }

        var plan = CaptureCleanupPlan();
        if (!plan.HasSelections)
        {
            CleanupSizeText.Text = "Select at least one cleanup option.";
            Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
            return;
        }

        SetCleanupControlsEnabled(false);
        CleanupSizeText.Text = "Scanning selected locations...";

        try
        {
            var result = await Task.Run(() => ScanCleanupTargets(plan));
            CleanupSizeText.Text = result.WarningCount > 0
                ? $"Estimated size: {FormatBytes(result.TotalBytes)} ({result.WarningCount} inaccessible items)"
                : $"Estimated size: {FormatBytes(result.TotalBytes)}";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Cleanup scan failed.");
            CleanupSizeText.Text = "Scan failed. Check permissions and try again.";
        }
        finally
        {
            SetCleanupControlsEnabled(true);
            Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
        }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("Are you sure you want to clean selected system folders?", "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            if (Interlocked.Exchange(ref _cleanupOperationInFlight, 1) != 0)
            {
                return;
            }

            var plan = CaptureCleanupPlan();
            if (!plan.HasSelections)
            {
                CleanupSizeText.Text = "Select at least one cleanup option.";
                Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
                return;
            }

            try
            {
                SetCleanupControlsEnabled(false);
                CleanupSizeText.Text = "Cleaning selected locations...";

                var result = await Task.Run(() => ExecuteCleanupPlan(plan));

                string completionMessage = result.WarningCount > 0
                    ? $"Cleanup completed with {result.WarningCount} skipped items."
                    : "System folders cleaned successfully!";

                CleanupSizeText.Text = "Estimated size: 0 B";
                System.Windows.MessageBox.Show(completionMessage, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Cleanup execution failed.");
                System.Windows.MessageBox.Show($"Error during cleanup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetCleanupControlsEnabled(true);
                Interlocked.Exchange(ref _cleanupOperationInFlight, 0);
            }
        }
    }

    private CleanupPlan CaptureCleanupPlan()
    {
        return new CleanupPlan(
            CleanPrefetchCheck.IsChecked == true,
            CleanTempCheck.IsChecked == true,
            CleanWinTempCheck.IsChecked == true,
            CleanUpdateCacheCheck.IsChecked == true,
            CleanRecycleBinCheck.IsChecked == true,
            CleanBrowserCacheCheck.IsChecked == true,
            CleanThumbnailsCheck.IsChecked == true,
            CleanErrorReportingCheck.IsChecked == true,
            CleanSystemLogsCheck.IsChecked == true);
    }

    private void SetCleanupControlsEnabled(bool isEnabled)
    {
        ScanCleanupButton.IsEnabled = isEnabled;
        CleanNowButton.IsEnabled = isEnabled;
        CleanRamButton.IsEnabled = isEnabled;
        CleanPrefetchCheck.IsEnabled = isEnabled;
        CleanTempCheck.IsEnabled = isEnabled;
        CleanWinTempCheck.IsEnabled = isEnabled;
        CleanUpdateCacheCheck.IsEnabled = isEnabled;
        CleanRecycleBinCheck.IsEnabled = isEnabled;
        CleanBrowserCacheCheck.IsEnabled = isEnabled;
        CleanThumbnailsCheck.IsEnabled = isEnabled;
        CleanErrorReportingCheck.IsEnabled = isEnabled;
        CleanSystemLogsCheck.IsEnabled = isEnabled;
    }

    private static CleanupExecutionResult ScanCleanupTargets(CleanupPlan plan)
    {
        long totalBytes = 0;
        int warningCount = 0;

        if (plan.CleanPrefetch) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Prefetch", ref warningCount));
        if (plan.CleanTemp) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.GetTempPath(), ref warningCount));
        if (plan.CleanWindowsTemp) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Temp", ref warningCount));
        if (plan.CleanUpdateCache) totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\SoftwareDistribution\Download", ref warningCount));
        if (plan.CleanRecycleBin) totalBytes = SafeAddBytes(totalBytes, EstimateRecycleBinSize(ref warningCount));

        if (plan.CleanBrowserCache)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"Google\Chrome\User Data\Default\Cache"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Cache"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, @"Opera Software\Opera Stable\Cache"), ref warningCount));
        }

        if (plan.CleanThumbnails)
        {
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectoryFilesMatching(explorerPath, "thumbcache_*.db", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectoryFilesMatching(explorerPath, "iconcache_*.db", ref warningCount));
        }

        if (plan.CleanErrorReporting)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(Path.Combine(localApp, "CrashDumps"), ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\ProgramData\Microsoft\Windows\WER", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Minidump", ref warningCount));
        }

        if (plan.CleanSystemLogs)
        {
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Logs\CBS", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Logs\DISM", ref warningCount));
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(@"C:\Windows\Panther", ref warningCount));
        }

        return new CleanupExecutionResult(totalBytes, warningCount);
    }

    private CleanupExecutionResult ExecuteCleanupPlan(CleanupPlan plan)
    {
        int warningCount = 0;

        if (plan.CleanPrefetch) ClearDirectory(@"C:\Windows\Prefetch", ref warningCount);
        if (plan.CleanTemp) ClearDirectory(Path.GetTempPath(), ref warningCount);
        if (plan.CleanWindowsTemp) ClearDirectory(@"C:\Windows\Temp", ref warningCount);
        if (plan.CleanUpdateCache) ClearDirectory(@"C:\Windows\SoftwareDistribution\Download", ref warningCount);
        if (plan.CleanRecycleBin) EmptyRecycleBin(ref warningCount);

        if (plan.CleanBrowserCache)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            ClearDirectory(Path.Combine(localApp, @"Google\Chrome\User Data\Default\Cache"), ref warningCount);
            ClearDirectory(Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Cache"), ref warningCount);
            ClearDirectory(Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Cache"), ref warningCount);
            ClearDirectory(Path.Combine(localApp, @"Opera Software\Opera Stable\Cache"), ref warningCount);
        }

        if (plan.CleanThumbnails)
        {
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Explorer");
            DeleteMatchingFiles(explorerPath, "thumbcache_*.db", ref warningCount);
            DeleteMatchingFiles(explorerPath, "iconcache_*.db", ref warningCount);
        }

        if (plan.CleanErrorReporting)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            ClearDirectory(Path.Combine(localApp, "CrashDumps"), ref warningCount);
            ClearDirectory(@"C:\ProgramData\Microsoft\Windows\WER", ref warningCount);
            ClearDirectory(@"C:\Windows\Minidump", ref warningCount);
        }

        if (plan.CleanSystemLogs)
        {
            ClearDirectory(@"C:\Windows\Logs\CBS", ref warningCount);
            ClearDirectory(@"C:\Windows\Logs\DISM", ref warningCount);
            ClearDirectory(@"C:\Windows\Panther", ref warningCount);
        }

        return new CleanupExecutionResult(0, warningCount);
    }

    private static long MeasureDirectoryFilesMatching(string path, string pattern, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                try { total = SafeAddBytes(total, new FileInfo(file).Length); }
                catch { warningCount++; }
            }
        }
        catch { warningCount++; }
        return total;
    }

    private static void DeleteMatchingFiles(string path, string pattern, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern))
            {
                try { File.Delete(file); }
                catch { warningCount++; }
            }
        }
        catch { warningCount++; }
    }

    private static long MeasureDirectorySize(string path, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            string currentPath = pending.Pop();
            IEnumerable<string> entries;

            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentPath);
            }
            catch
            {
                warningCount++;
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    totalBytes = SafeAddBytes(totalBytes, new FileInfo(entry).Length);
                }
                catch
                {
                    warningCount++;
                }
            }
        }

        return totalBytes;
    }

    private static void ClearDirectory(string path, ref int warningCount)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var pending = new Stack<string>();
        var visitedDirectories = new List<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            string currentPath = pending.Pop();
            visitedDirectories.Add(currentPath);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentPath);
            }
            catch
            {
                warningCount++;
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch
                {
                    warningCount++;
                }
            }
        }

        for (int i = visitedDirectories.Count - 1; i >= 0; i--)
        {
            string directory = visitedDirectories[i];
            if (string.Equals(directory, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, false);
            }
            catch
            {
                warningCount++;
            }
        }
    }

    private static void EmptyRecycleBin(ref int warningCount)
    {
        try
        {
            int hResult = SHEmptyRecycleBin(IntPtr.Zero, null, RecycleBinNoConfirmation | RecycleBinNoProgressUi | RecycleBinNoSound);
            if (hResult != 0)
            {
                warningCount++;
            }
        }
        catch
        {
            warningCount++;
        }
    }

    private static long EstimateRecycleBinSize(ref int warningCount)
    {
        long totalBytes = 0;
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            warningCount++;
            return 0;
        }

        foreach (var drive in drives.Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            string recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            totalBytes = SafeAddBytes(totalBytes, MeasureDirectorySize(recyclePath, ref warningCount));
        }

        return totalBytes;
    }

    private static long SafeAddBytes(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private readonly struct CleanupPlan
    {
        public CleanupPlan(
            bool cleanPrefetch,
            bool cleanTemp,
            bool cleanWindowsTemp,
            bool cleanUpdateCache,
            bool cleanRecycleBin,
            bool cleanBrowserCache,
            bool cleanThumbnails,
            bool cleanErrorReporting,
            bool cleanSystemLogs)
        {
            CleanPrefetch = cleanPrefetch;
            CleanTemp = cleanTemp;
            CleanWindowsTemp = cleanWindowsTemp;
            CleanUpdateCache = cleanUpdateCache;
            CleanRecycleBin = cleanRecycleBin;
            CleanBrowserCache = cleanBrowserCache;
            CleanThumbnails = cleanThumbnails;
            CleanErrorReporting = cleanErrorReporting;
            CleanSystemLogs = cleanSystemLogs;
        }

        public bool CleanPrefetch { get; }
        public bool CleanTemp { get; }
        public bool CleanWindowsTemp { get; }
        public bool CleanUpdateCache { get; }
        public bool CleanRecycleBin { get; }
        public bool CleanBrowserCache { get; }
        public bool CleanThumbnails { get; }
        public bool CleanErrorReporting { get; }
        public bool CleanSystemLogs { get; }

        public bool HasSelections => CleanPrefetch || CleanTemp || CleanWindowsTemp || CleanUpdateCache || CleanRecycleBin
            || CleanBrowserCache || CleanThumbnails || CleanErrorReporting || CleanSystemLogs;
    }

    private readonly struct CleanupExecutionResult
    {
        public CleanupExecutionResult(long totalBytes, int warningCount)
        {
            TotalBytes = totalBytes;
            WarningCount = warningCount;
        }

        public long TotalBytes { get; }
        public int WarningCount { get; }
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageSelector.SelectedItem is ComboBoxItem item && item.Tag is string langCode)
        {
            _settings.Language = langCode;
            LocalizationManager.Instance.SetLanguage(langCode);
            SettingsManager.Save(_settings);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
