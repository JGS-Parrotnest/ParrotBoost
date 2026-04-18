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
        StartLogoFloating();
        
        // Add global back button support
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        
        // Touch back support
        IsManipulationEnabled = true;
        ManipulationStarting += (s, ev) => ev.ManipulationContainer = this;
        ManipulationCompleted += MainWindow_ManipulationCompleted;
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
                    GpuDriverBtn.Content = $"Aktualizacja: {msg}";
                    GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E67E22")); // Orange for warning
                    GpuDriverBtn.FontWeight = FontWeights.Bold;
                    
                    GpuDriverBtn.ToolTip = "Dostępne nowe sterowniki:\n" + 
                        string.Join("\n", availableUpdates.Select(u => 
                        $"{u.Manufacturer}: {u.InstalledVersion} -> {u.LatestVersion}"));
                });
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _driverDownloadUrl = null;
                    GpuDriverBtn.Content = "Sterowniki aktualne";
                    GpuDriverBtn.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2ECC71")); // Green
                    GpuDriverBtn.FontWeight = FontWeights.SemiBold;
                    GpuDriverBtn.ToolTip = "Wszystkie sterowniki GPU są w najnowszej wersji.";
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to check for driver updates.");
        }
        finally
        {
            Logger.Debug("Driver update check completed in {0} ms", stopwatch.ElapsedMilliseconds);
        }
    }

    private void MainWindow_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
    {
        // Simple back swipe check (from left to right)
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
        // Handle Mouse Back button (XButton1)
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
        // Load last values
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
            if (_compatibilityProfile.StartupTelemetryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_compatibilityProfile.StartupTelemetryDelay, cancellationToken);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                _performanceTimer?.Start();
                Logger.Debug("Performance timer started after compatibility delay: {0}", _compatibilityProfile.StartupTelemetryDelay);
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

            CpuLoadBar.Value = sample.cpuLoad;
            GpuLoadBar.Value = sample.gpuLoad;
            CpuTempText.Text = HardwareTelemetryService.FormatTemperature(sample.displayCpuTemp);
            GpuTempText.Text = HardwareTelemetryService.FormatTemperature(sample.displayGpuTemp);
            CpuLoadText.Text = $"{(int)sample.cpuLoad}%";
            GpuLoadText.Text = $"{(int)sample.gpuLoad}%";
            CpuLoadBar.Foreground = GetLoadBrush(sample.cpuLoad);
            GpuLoadBar.Foreground = GetLoadBrush(sample.gpuLoad);

            _settings.LastCpuLoad = sample.cpuLoad;
            _settings.LastGpuLoad = sample.gpuLoad;
            _settings.LastCpuTemp = sample.displayCpuTemp;
            _settings.LastGpuTemp = sample.displayGpuTemp;
            _latestBoostPrediction = sample.boostPrediction;
            RefreshBoostPresentation();

            var now = DateTime.UtcNow;
            if (now - _lastOptimizationLogUtc >= _compatibilityProfile.OptimizationLogInterval)
            {
                _lastOptimizationLogUtc = now;
                Logger.Debug(
                    "Runtime profile: Windows10Compat={0}, Cpu={1:F1}%, Gpu={2:F1}%, Priority={3}, Workers={4}, CachedPayloads={5}",
                    _compatibilityProfile.IsWindows10,
                    sample.cpuLoad,
                    sample.gpuLoad,
                    sample.optimizationSnapshot.PriorityClass,
                    sample.optimizationSnapshot.WorkerThreads,
                    sample.optimizationSnapshot.CachedPayloads);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Hardware Monitoring Error");
        }
        finally
        {
            Interlocked.Exchange(ref _performanceRefreshInFlight, 0);
        }
    }

    private SolidColorBrush GetLoadBrush(float load)
    {
        if (load > 80) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)); // Red
        if (load > 50) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 196, 15)); // Yellow
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 204, 113)); // Green
    }

    private void ApplySettings()
    {
        LocalizationManager.Instance.SetLanguage(_settings.Language);
        ApplyTheme(_settings.IsDarkMode);
        
        // Bind UI
        LaunchAtStartupCheck.IsChecked = _settings.LaunchAtStartup;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        OptServicesCheck.IsChecked = _settings.OptServices;
        OptMemoryCheck.IsChecked = _settings.OptMemory;
        OptTasksCheck.IsChecked = _settings.OptTasks;
        OptNtfsCheck.IsChecked = _settings.OptNtfs;
        OptPriorityCheck.IsChecked = _settings.OptPriority;
        OptUsbCheck.IsChecked = _settings.OptUsb;
        OptDeliveryCheck.IsChecked = _settings.OptDelivery;
        OptTickCheck.IsChecked = _settings.OptTick;
        GameModeCheck.IsChecked = _settings.EnableGameMode;
        CleanUpdateCacheCheck.IsChecked = _settings.CleanUpdateCache;
        CleanRecycleBinCheck.IsChecked = _settings.CleanRecycleBin;

        // Set Language ComboBox
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
        // Performance monitor persistence is already in _settings
        _settings.LaunchAtStartup = LaunchAtStartupCheck.IsChecked ?? false;
        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked ?? true;
        _settings.OptServices = OptServicesCheck.IsChecked ?? true;
        _settings.OptMemory = OptMemoryCheck.IsChecked ?? true;
        _settings.OptTasks = OptTasksCheck.IsChecked ?? true;
        _settings.OptNtfs = OptNtfsCheck.IsChecked ?? true;
        _settings.OptPriority = OptPriorityCheck.IsChecked ?? true;
        _settings.OptUsb = OptUsbCheck.IsChecked ?? true;
        _settings.OptDelivery = OptDeliveryCheck.IsChecked ?? true;
        _settings.OptTick = OptTickCheck.IsChecked ?? true;
        _settings.EnableGameMode = GameModeCheck.IsChecked ?? false;
        _settings.CleanUpdateCache = CleanUpdateCacheCheck.IsChecked ?? true;
        _settings.CleanRecycleBin = CleanRecycleBinCheck.IsChecked ?? true;
        // Theme and Language are updated immediately on change

        SettingsManager.Save(_settings);
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

    private void StartLogoFloating()
    {
        if (LogoFloat == null) return;
        var floatAnim = new DoubleAnimation
        {
            From = 0,
            To = -15,
            Duration = TimeSpan.FromSeconds(2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        LogoFloat.BeginAnimation(TranslateTransform.YProperty, floatAnim);
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
            if (ThemeBtn != null) 
            {
                var textBlock = ThemeBtn.Content as TextBlock;
                if (textBlock != null) textBlock.Text = ""; // Sun/Light icon
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
            if (ThemeBtn != null) 
            {
                var textBlock = ThemeBtn.Content as TextBlock;
                if (textBlock != null) textBlock.Text = ""; // Moon/Dark icon
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
        contextMenu.Items.Add("Show", null, (s, e) => { Show(); WindowState = WindowState.Normal; });
        contextMenu.Items.Add("Boost ON/OFF", null, async (s, e) => await Dispatcher.InvokeAsync(ToggleBoostAsync));
        contextMenu.Items.Add("-");
        contextMenu.Items.Add("Exit", null, (s, e) => { System.Windows.Application.Current.Shutdown(); });
        
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            Hide();

        base.OnStateChanged(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateRootClip();

        var scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromSeconds(1.5))
        {
            EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 5 }
        };
        SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        var startupTimer = Stopwatch.StartNew();
        using var backgroundScope = SystemExecutionProfile.TryEnterBackgroundProcessingMode();

        await InitializeDeferredStartupAsync();

        var minimumSplash = TimeSpan.FromMilliseconds(350);
        if (startupTimer.Elapsed < minimumSplash)
        {
            await Task.Delay(minimumSplash - startupTimer.Elapsed);
        }

        var fadeAnim = new DoubleAnimation(0, TimeSpan.FromSeconds(0.5));
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
            await DelayWithDefenderAwarenessAsync(_compatibilityProfile.HardwareInventoryDelay, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                await LoadHardwareInfoAsync();
            }

            await DelayWithDefenderAwarenessAsync(_compatibilityProfile.DriverUpdateDelay, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                await CheckForDriverUpdatesAsync();
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
            await Task.Delay(delay, cancellationToken);
        }

        while (!cancellationToken.IsCancellationRequested && ShouldThrottleForWindowsDefender())
        {
            await Task.Delay(_compatibilityProfile.DefenderProbeInterval, cancellationToken);
        }
    }

    private bool ShouldThrottleForWindowsDefender()
    {
        if (!_compatibilityProfile.SuspendTelemetryDuringDefenderScans)
        {
            return false;
        }

        bool defenderBusy = _securityActivityMonitor.IsDefenderBusy(
            _compatibilityProfile.DefenderBusyCpuThresholdPercent,
            _compatibilityProfile.DefenderProbeInterval);

        if (defenderBusy)
        {
            Logger.Debug("Windows Defender activity detected. Deferring telemetry-heavy work for Windows 10 compatibility.");
        }

        return defenderBusy;
    }

    private void OnCriticalTemperatureDetected(CriticalTemperatureEvent ev)
    {
        // Minimal-spam notification: max 1 toast every 30 seconds.
        var now = DateTime.UtcNow;
        if ((now - _lastCriticalToastUtc).TotalSeconds < 30)
        {
            return;
        }

        _lastCriticalToastUtc = now;

        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.BalloonTipTitle = "ParrotBoost: Critical Temperature";
                    _notifyIcon.BalloonTipText = ev.Message;
                    _notifyIcon.ShowBalloonTip(5000);
                }
            });
        }
        catch
        {
        }
    }
    
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _navigationStack.Push("MainDashboard");
        SetSettingsOverlayVisible(true);
    }

    private void Settings_Back_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationStack.Count > 0)
        {
            string previous = _navigationStack.Pop();
            if (previous == "MainDashboard")
            {
                SaveSettings();
                SetSettingsOverlayVisible(false);
            }
            // Add other screens here if needed
        }
        else
        {
            // If stack is empty, ensure we are at main settings view
            // In our case, that's just the overlay being visible.
            // But usually 'back' from the top of settings should go back to the app.
            SaveSettings();
            SetSettingsOverlayVisible(false);
        }
    }

    private void SetSettingsOverlayVisible(bool isVisible)
    {
        SettingsOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.IsHitTestVisible = isVisible;
        MainDashboard.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateRootClip()
    {
        // Usunięto zaokrąglanie rogów dla pełnego wypełnienia okna
        RootGrid.Clip = null;
    }

    private void UpdateBoostUI()
    {
        if (IsBoostActive)
        {
            BoostButton.Content = "STOP";
            BoostButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)); // Red
        }
        else
        {
            BoostButton.Content = "BOOST";
            BoostButton.Background = (SolidColorBrush)Resources["ColorPrimaryBrush"];
        }

        RefreshBoostPresentation();
    }

    private void GpuDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_driverDownloadUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_driverDownloadUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open driver URL");
            }
        }
    }

    private async Task LoadHardwareInfoAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var snapshot = await Task.Run(() =>
            {
                string? cpuName = null;
                string? gpuName = null;
                double? totalRam = null;

                using (var searcher = new ManagementObjectSearcher("select Name from Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        cpuName = obj["Name"]?.ToString();
                        break;
                    }
                }

                using (var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        gpuName = obj["Name"]?.ToString();
                        break;
                    }
                }

                using (var searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        totalRam = Convert.ToDouble(obj["TotalPhysicalMemory"]) / (1024 * 1024 * 1024);
                        break;
                    }
                }

                return (cpuName, gpuName, totalRam);
            });

            _gpuManufacturer = snapshot.gpuName ?? "Unknown";
            CpuInfo.Text = $"Processor: {snapshot.cpuName ?? "Unknown"}";
            GpuInfo.Text = $"Graphics: {_gpuManufacturer}";
            RamInfo.Text = snapshot.totalRam.HasValue
                ? $"Memory: {Math.Round(snapshot.totalRam.Value, 1)} GB RAM"
                : "Memory: Unknown";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Hardware info error");
        }
        finally
        {
            Logger.Debug("Hardware inventory completed in {0} ms", stopwatch.ElapsedMilliseconds);
        }
    }

    private BoostPlanConfiguration CaptureBoostPlanConfiguration()
    {
        return new BoostPlanConfiguration(
            OptServicesCheck.IsChecked ?? _settings.OptServices,
            OptMemoryCheck.IsChecked ?? _settings.OptMemory,
            OptTasksCheck.IsChecked ?? _settings.OptTasks,
            OptNtfsCheck.IsChecked ?? _settings.OptNtfs,
            OptPriorityCheck.IsChecked ?? _settings.OptPriority,
            OptUsbCheck.IsChecked ?? _settings.OptUsb,
            OptDeliveryCheck.IsChecked ?? _settings.OptDelivery,
            OptTickCheck.IsChecked ?? _settings.OptTick,
            GameModeCheck.IsChecked ?? _settings.EnableGameMode);
    }

    private void RefreshBoostPresentation()
    {
        BoostPercentage.Text = _latestBoostPrediction.RangeLabel;
        BoostPredictionDetails.Text = GetBoostOutcomeText();

        if (_boostWorkflowStatusActive)
        {
            return;
        }

        ProgressStatus.Text = GetBoostActivityText();
    }

    private string GetBoostActivityText()
    {
        if (IsBoostActive)
        {
            return "Boost active";
        }

        if (_latestBoostPrediction.MaximumGainPercent <= 0)
        {
            return "Boost on standby";
        }

        return "Boost ready";
    }

    private string GetBoostOutcomeText()
    {
        if (_latestBoostPrediction.MinimumGainPercent < 0 || _latestBoostPrediction.MaximumGainPercent < 0)
        {
            return "Analyzing system";
        }

        if (_latestBoostPrediction.MaximumGainPercent <= 0)
        {
            return "No meaningful gain";
        }

        if (_latestBoostPrediction.MaximumGainPercent <= 3)
        {
            return "Small gain likely";
        }

        if (_latestBoostPrediction.MaximumGainPercent <= 8)
        {
            return "Moderate gain likely";
        }

        return "Strong gain likely";
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
                    case "Tasks": if (_settings.OptTasks) OptimizeTaskScheduler(); if (_settings.OptDelivery) DisableDeliveryOptimization(); break;
                    case "Timers": if (_settings.OptTick) { SetDynamicTick(false); SetHpet(false); } ClearIconCache(); break;
                    case "Power": CreateTurboParrotPowerPlan(); if (_settings.OptUsb) OptimizeUsbPower(true); SetTimerCoalescing(true); break;
                    case "Cleanup": ClearTempFolders(); break;
                    case "Priority": if (_settings.OptPriority) SetForegroundPriority(true); if (_settings.OptNtfs) DisableNtfsLastAccess(true); break;
                }
            });
            await Task.Delay(400);
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
        _boostWorkflowStatusActive = true;
        ProgressStatus.Text = "Restoring boost";
        
        await Task.Run(() => 
        {
            SetVisualEffects(false);
            DisableUwpAnimations(false);
            _gameModeService.Restore();
            EnableService("DoSvc");
            SetDynamicTick(true);
            SetHpet(true);
            OptimizeUsbPower(false);
            SetTimerCoalescing(false);
            SetForegroundPriority(false);
            DisableNtfsLastAccess(false);
            
            // Re-enable tasks
            string[] tasks = {
                @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                @"\Microsoft\Windows\Autochk\Proxy"
            };
            foreach (var task in tasks)
            {
                RunCommand("schtasks", $"/change /tn \"{task}\" /enable");
            }

            RunCommand("powercfg", "-setactive scheme_balanced");
        });

        await Task.Delay(800);
        _boostWorkflowStatusActive = false;
        BoostButton.IsEnabled = true;
        RefreshBoostPresentation();
    }

    // --- Optimization Helpers ---

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

    private void DisableDeliveryOptimization()
    {
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

    // --- Original Logic ---

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
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                }
            }
            RunCommand("sc.exe", $"config {serviceName} start= disabled");
        }
        catch { }
    }

    private void EnableService(string serviceName)
    {
        try
        {
            RunCommand("sc.exe", $"config {serviceName} start= auto");
            using (ServiceController sc = new ServiceController(serviceName))
            {
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    sc.Start();
                }
            }
        }
        catch { }
    }

    private void CreateTurboParrotPowerPlan()
    {
        try {
            string guid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
            string newGuid = "77777777-7777-7777-7777-777777777777";
            RunCommand("powercfg", $"-duplicatescheme {guid} {newGuid}");
            RunCommand("powercfg", $"-changename {newGuid} \"Turbo Parrot\"");
            RunCommand("powercfg", $"-setactive {newGuid}");
        } catch {}
    }

    private void RunCommand(string filename, string arguments)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = filename,
                Arguments = arguments,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit();
        }
        catch { }
    }

    // --- Cleanup logic ---
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
                ? $"Estimated size: {FormatBytes(result.TotalBytes)} ({result.WarningCount} skipped)"
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
                BoostButton.IsEnabled = false;
                SetCleanupControlsEnabled(false);
                CleanupSizeText.Text = "Cleaning selected locations...";

                var result = await Task.Run(() => ExecuteCleanupPlan(plan));
                
                string message = result.WarningCount > 0
                    ? $"Cleanup completed with {result.WarningCount} skipped items."
                    : "System folders cleaned successfully!";
                System.Windows.MessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                CleanupSizeText.Text = "Estimated size: 0 B";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Cleanup execution failed.");
                System.Windows.MessageBox.Show($"Error during cleanup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BoostButton.IsEnabled = true;
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
            CleanRecycleBinCheck.IsChecked == true);
    }

    private void SetCleanupControlsEnabled(bool isEnabled)
    {
        ScanCleanupButton.IsEnabled = isEnabled;
        CleanNowButton.IsEnabled = isEnabled;
        CleanPrefetchCheck.IsEnabled = isEnabled;
        CleanTempCheck.IsEnabled = isEnabled;
        CleanWinTempCheck.IsEnabled = isEnabled;
        CleanUpdateCacheCheck.IsEnabled = isEnabled;
        CleanRecycleBinCheck.IsEnabled = isEnabled;
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

        return new CleanupExecutionResult(0, warningCount);
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
                        File.SetAttributes(entry, FileAttributes.Normal);
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
            if (string.Equals(visitedDirectories[i], path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(visitedDirectories[i], recursive: false);
            }
            catch
            {
                warningCount++;
            }
        }
    }

    private static long EstimateRecycleBinSize(ref int warningCount)
    {
        long totalBytes = 0;
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string recycleBinPath = Path.Combine(systemDrive, "$Recycle.Bin");
        try
        {
            totalBytes = MeasureDirectorySize(recycleBinPath, ref warningCount);
        }
        catch
        {
            warningCount++;
        }

        return totalBytes;
    }

    private static long SafeAddBytes(long totalBytes, long value)
    {
        if (value <= 0)
        {
            return totalBytes;
        }

        long remaining = long.MaxValue - totalBytes;
        return value > remaining ? long.MaxValue : totalBytes + value;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int suffixIndex = 0;
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return $"{value:0.#} {suffixes[suffixIndex]}";
    }

    private static void EmptyRecycleBin(ref int warningCount)
    {
        try
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, RecycleBinNoConfirmation | RecycleBinNoProgressUi | RecycleBinNoSound);
        }
        catch
        {
            warningCount++;
        }
    }

    private readonly struct CleanupPlan
    {
        public CleanupPlan(bool cleanPrefetch, bool cleanTemp, bool cleanWindowsTemp, bool cleanUpdateCache, bool cleanRecycleBin)
        {
            CleanPrefetch = cleanPrefetch;
            CleanTemp = cleanTemp;
            CleanWindowsTemp = cleanWindowsTemp;
            CleanUpdateCache = cleanUpdateCache;
            CleanRecycleBin = cleanRecycleBin;
        }

        public bool CleanPrefetch { get; }
        public bool CleanTemp { get; }
        public bool CleanWindowsTemp { get; }
        public bool CleanUpdateCache { get; }
        public bool CleanRecycleBin { get; }
        public bool HasSelections => CleanPrefetch || CleanTemp || CleanWindowsTemp || CleanUpdateCache || CleanRecycleBin;
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
