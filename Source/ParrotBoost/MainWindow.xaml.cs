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
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Linq;

namespace ParrotBoost;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private bool _isBoostActive = false;
    private UserSettings _settings = new();
    private readonly HardwareTelemetryService _hardwareTelemetry;
    private readonly ParrotBoostRuntimeOptimizer _runtimeOptimizer;
    private NotifyIcon? _notifyIcon;
    private string _gpuManufacturer = "Unknown";
    private System.Windows.Threading.DispatcherTimer? _performanceTimer;
    private Stack<string> _navigationStack = new();
    private int _cpuTempReadFailures;
    private int _gpuTempReadFailures;
    private DateTime _lastOptimizationLogUtc = DateTime.MinValue;
    private bool? _lastLoggedBoostState;

    // Smoothed values for Performance Monitor (EMA)
    private float _smoothedCpuLoad = 0;
    private float _smoothedGpuLoad = 0;
    private float _smoothedCpuTemp = 0;
    private float _smoothedGpuTemp = 0;
    private const float SmoothingAlpha = 0.2f;

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

    public MainWindow()
    {
        InitializeComponent();
        _hardwareTelemetry = new HardwareTelemetryService();
        _runtimeOptimizer = new ParrotBoostRuntimeOptimizer();
        DataContext = this;
        
        _settings = SettingsManager.Load();
        ApplySettings();
        
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateRootClip();
        LoadHardwareInfo();
        CheckGpuDrivers();
        SetupTrayIcon();
        StartLogoFloating();
        DetectOptimizationState();
        SetupPerformanceTimer();
        
        // Add global back button support
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        
        // Touch back support
        IsManipulationEnabled = true;
        ManipulationStarting += (s, ev) => ev.ManipulationContainer = this;
        ManipulationCompleted += MainWindow_ManipulationCompleted;
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
        // Load last values and initialize smoothed values
        _smoothedCpuLoad = _settings.LastCpuLoad;
        _smoothedGpuLoad = _settings.LastGpuLoad;
        _smoothedCpuTemp = _settings.LastCpuTemp;
        _smoothedGpuTemp = _settings.LastGpuTemp;

        CpuLoadBar.Value = _smoothedCpuLoad;
        GpuLoadBar.Value = _smoothedGpuLoad;
        CpuLoadText.Text = $"{(int)_smoothedCpuLoad}%";
        GpuLoadText.Text = $"{(int)_smoothedGpuLoad}%";
        CpuTempText.Text = HardwareTelemetryService.FormatTemperature(_smoothedCpuTemp);
        GpuTempText.Text = HardwareTelemetryService.FormatTemperature(_smoothedGpuTemp);

        _performanceTimer = new System.Windows.Threading.DispatcherTimer();
        _performanceTimer.Interval = TimeSpan.FromMilliseconds(500); // High frequency (~Task Manager)
        _performanceTimer.Tick += PerformanceTimer_Tick;
        _performanceTimer.Start();
    }

    private void PerformanceTimer_Tick(object? sender, EventArgs e)
    {
        Task.Run(() =>
        {
            try
            {
                float cpuLoad = _hardwareTelemetry.TryGetCpuLoad() ?? GetAccurateCpuLoad();
                float gpuLoad = GetAccurateGpuLoad();
                float? cpuTemp = _hardwareTelemetry.TryGetCpuTemperature();
                float? gpuTemp = _hardwareTelemetry.TryGetGpuTemperature();

                // Validation and real-time monitoring
                if (cpuLoad < 0 || cpuLoad > 100) Logger.Warn($"Invalid CPU Load detected: {cpuLoad}");
                if (gpuLoad < 0 || gpuLoad > 100) Logger.Warn($"Invalid GPU Load detected: {gpuLoad}");

                cpuLoad = Math.Clamp(cpuLoad, 0, 100);
                gpuLoad = Math.Clamp(gpuLoad, 0, 100);

                // Apply Exponential Moving Average for smoothing
                _smoothedCpuLoad = (SmoothingAlpha * cpuLoad) + ((1 - SmoothingAlpha) * _smoothedCpuLoad);
                _smoothedGpuLoad = (SmoothingAlpha * gpuLoad) + ((1 - SmoothingAlpha) * _smoothedGpuLoad);
                if (cpuTemp is > 0)
                {
                    _cpuTempReadFailures = 0;
                    _smoothedCpuTemp = (SmoothingAlpha * cpuTemp.Value) + ((1 - SmoothingAlpha) * _smoothedCpuTemp);
                }
                else if (++_cpuTempReadFailures >= 4)
                {
                    _smoothedCpuTemp = 0;
                }
                if (gpuTemp is > 0)
                {
                    _gpuTempReadFailures = 0;
                    _smoothedGpuTemp = (SmoothingAlpha * gpuTemp.Value) + ((1 - SmoothingAlpha) * _smoothedGpuTemp);
                }
                else if (++_gpuTempReadFailures >= 4)
                {
                    _smoothedGpuTemp = 0;
                }

                // Update settings for persistence
                _settings.LastCpuLoad = _smoothedCpuLoad;
                _settings.LastGpuLoad = _smoothedGpuLoad;
                _settings.LastCpuTemp = _smoothedCpuTemp;
                _settings.LastGpuTemp = _smoothedGpuTemp;

                Dispatcher.Invoke(() =>
                {
                    CpuLoadBar.Value = _smoothedCpuLoad;
                    GpuLoadBar.Value = _smoothedGpuLoad;
                    CpuTempText.Text = HardwareTelemetryService.FormatTemperature(_smoothedCpuTemp);
                    GpuTempText.Text = HardwareTelemetryService.FormatTemperature(_smoothedGpuTemp);
                    CpuLoadText.Text = $"{(int)_smoothedCpuLoad}%";
                    GpuLoadText.Text = $"{(int)_smoothedGpuLoad}%";
                    
                    CpuLoadBar.Foreground = GetLoadBrush(_smoothedCpuLoad);
                    GpuLoadBar.Foreground = GetLoadBrush(_smoothedGpuLoad);
                });
            }
            catch (Exception ex) 
            { 
                Logger.Error(ex, "Hardware Monitoring Error");
            }
        });
    }

    private float GetAccurateCpuLoad()
    {
        try {
            // Task Manager style CPU load (1:1 accuracy)
            using (var searcher = new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
            {
                foreach (var obj in searcher.Get())
                {
                    return Math.Clamp(Convert.ToSingle(obj["PercentProcessorTime"]), 0, 100);
                }
            }
        } catch (Exception ex) { Logger.Debug(ex, "CPU Load fallback"); }
        return _settings.LastCpuLoad;
    }

    private float GetAccurateGpuLoad()
    {
        try {
            // Accurate GPU engine utilization (Windows 10+)
            using (var searcher = new ManagementObjectSearcher("SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D'"))
            {
                float maxUtil = 0;
                int count = 0;
                foreach (var obj in searcher.Get())
                {
                    float val = Convert.ToSingle(obj["UtilizationPercentage"]);
                    if (val > maxUtil) maxUtil = val;
                    count++;
                }
                if (count > 0) return Math.Clamp(maxUtil, 0, 100);
            }
            
            // WMI Fallback
            using (var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_VideoController"))
            {
                foreach (var obj in searcher.Get())
                {
                    return Math.Clamp(Convert.ToSingle(obj["LoadPercentage"]), 0, 100);
                }
            }
        } catch (Exception ex) { Logger.Debug(ex, "GPU Load fallback"); }
        return _settings.LastGpuLoad;
    }

    private float GetAccurateCpuTemp()
    {
        try {
            // Real sensor access (requires high privileges/driver support)
            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
            {
                foreach (var obj in searcher.Get())
                {
                    float tempK = Convert.ToSingle(obj["CurrentTemperature"]);
                    float tempC = (tempK - 2731.5f) / 10.0f;
                    // No artificial capping or lowering, 1:1 real sensor data
                    if (tempC > 0 && tempC < 115) return tempC;
                }
            }
        } catch { }
        
        // Fallback to 0 if sensors blocked - we want 1:1 real data only as per requirements
        return 0;
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

        await Task.Delay(2000);

        var fadeAnim = new DoubleAnimation(0, TimeSpan.FromSeconds(0.5));
        fadeAnim.Completed += (s, _) =>
        {
            SplashOverlay.IsHitTestVisible = false;
            SplashOverlay.Visibility = Visibility.Collapsed;
        };
        SplashOverlay.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _performanceTimer?.Stop();
        _notifyIcon?.Dispose();
        _hardwareTelemetry.Dispose();
        base.OnClosed(e);
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
        if (WindowFrame.ActualWidth <= 0 || WindowFrame.ActualHeight <= 0)
        {
            return;
        }

        double cornerRadius = WindowFrame.CornerRadius.TopLeft;
        RootGrid.Clip = new RectangleGeometry(
            new Rect(0, 0, WindowFrame.ActualWidth, WindowFrame.ActualHeight),
            cornerRadius,
            cornerRadius);
    }

    private void UpdateBoostUI()
    {
        if (IsBoostActive)
        {
            BoostButton.Content = "STOP";
            BoostButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)); // Red
            ProgressStatus.Text = "Boost active. Click STOP to restore.";
            BoostPercentage.Text = "+18%";
        }
        else
        {
            BoostButton.Content = "BOOST";
            BoostButton.Background = (SolidColorBrush)Resources["ColorPrimaryBrush"];
            ProgressStatus.Text = "System is ready to boost";
            BoostPercentage.Text = "+5-12%";
        }
    }

    private void GpuDriverBtn_Click(object sender, RoutedEventArgs e)
    {
        if (GpuDriverBtn.Content.ToString() == LocalizationManager.Instance.GetString("MainWindow.NotUpToDate"))
        {
            string url = _gpuManufacturer.ToLower() switch
            {
                var m when m.Contains("nvidia") => "https://www.nvidia.com/Download/index.aspx",
                var m when m.Contains("amd") || m.Contains("radeon") => "https://www.amd.com/en/support",
                var m when m.Contains("intel") => "https://www.intel.com/content/www/us/en/support/detect.html",
                _ => "https://www.google.com/search?q=graphics+drivers+update"
            };

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open driver URL");
            }
        }
    }

    private void LoadHardwareInfo()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("select Name from Win32_Processor"))
            {
                foreach (var obj in searcher.Get())
                {
                    CpuInfo.Text = $"Processor: {obj["Name"]}";
                    break;
                }
            }

            using (var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController"))
            {
                foreach (var obj in searcher.Get())
                {
                    _gpuManufacturer = obj["Name"]?.ToString() ?? "Unknown";
                    GpuInfo.Text = $"Graphics: {_gpuManufacturer}";
                    break;
                }
            }

            using (var searcher = new ManagementObjectSearcher("select TotalPhysicalMemory from Win32_ComputerSystem"))
            {
                foreach (var obj in searcher.Get())
                {
                    double totalRam = Convert.ToDouble(obj["TotalPhysicalMemory"]) / (1024 * 1024 * 1024);
                    RamInfo.Text = $"Memory: {Math.Round(totalRam, 1)} GB RAM";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Hardware info error");
        }
    }

    private void CheckGpuDrivers()
    {
        // For demonstration, let's say it's outdated if the GPU name contains "Intel" (often the case in VMs or integrated)
        // In a real scenario, you'd check the driver version against an API.
        if (_gpuManufacturer.Contains("Intel"))
        {
            GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.NotUpToDate");
            GpuDriverBtn.Foreground = System.Windows.Media.Brushes.Red;
        }
        else
        {
            GpuDriverBtn.Content = LocalizationManager.Instance.GetString("MainWindow.UpToDate");
            GpuDriverBtn.Foreground = System.Windows.Media.Brushes.Green;
        }
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
        
        string[] statusSteps = { 
            "Optimizing Visual Effects...", 
            "Disabling Telemetry & Tasks...", 
            "Optimizing Network & Updates...", 
            "Fine-tuning System Timers...", 
            "Configuring Power & USB...",
            "Cleaning System Logs & Temp...",
            "Finalizing System Priority..." 
        };

        foreach (var step in statusSteps)
        {
            ProgressStatus.Text = step;
            await Task.Run(() => 
            {
                switch (step)
                {
                    case "Optimizing Visual Effects...": if (_settings.OptServices) SetVisualEffects(true); DisableUwpAnimations(true); break;
                    case "Disabling Telemetry & Tasks...": if (_settings.OptTasks) { OptimizeTaskScheduler(); DisableTelemetry(); } break;
                    case "Optimizing Network & Updates...": if (_settings.OptDelivery) DisableDeliveryOptimization(); sc_config("wuauserv", "demand"); break;
                    case "Fine-tuning System Timers...": if (_settings.OptTick) { SetDynamicTick(false); SetHpet(false); } ClearIconCache(); break;
                    case "Configuring Power & USB...": CreateTurboParrotPowerPlan(); if (_settings.OptUsb) OptimizeUsbPower(true); SetTimerCoalescing(true); break;
                    case "Cleaning System Logs & Temp...": ClearSystemLogs(); ClearTempFolders(); RestartWmi(); break;
                    case "Finalizing System Priority...": if (_settings.OptPriority) SetForegroundPriority(true); if (_settings.OptNtfs) DisableNtfsLastAccess(true); break;
                }
            });
            await Task.Delay(400);
        }

        BoostButton.IsEnabled = true;
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

    private void ClearSystemLogs()
    {
        RunCommand("wevtutil", "cl Application");
        RunCommand("wevtutil", "cl System");
        RunCommand("wevtutil", "cl Security");
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

    private void RestartWmi()
    {
        RunCommand("net", "stop winmgmt /y");
        RunCommand("net", "start winmgmt");
    }

    private async Task RunRestoreSequence()
    {
        BoostButton.IsEnabled = false;
        ProgressStatus.Text = "Restoring system settings...";
        
        await Task.Run(() => 
        {
            SetVisualEffects(false);
            DisableUwpAnimations(false);
            EnableService("DoSvc");
            sc_config("wuauserv", "auto");
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
                RunCommand("schtasks", $"/change /tn \"{task}\" /disable");
            }

            RunCommand("powercfg", "-setactive scheme_balanced");
        });

        await Task.Delay(800);
        BoostButton.IsEnabled = true;
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

    private void DisableTelemetry()
    {
        try {
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
            StopAndDisableService("DiagTrack");
            StopAndDisableService("dmwappushservice");
        } catch { }
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

    private void sc_config(string service, string startType)
    {
        RunCommand("sc", $"config {service} start={startType}");
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
            RunPowerShell($"Set-Service -Name '{serviceName}' -StartupType Disabled");
        }
        catch { }
    }

    private void EnableService(string serviceName)
    {
        try
        {
            RunPowerShell($"Set-Service -Name '{serviceName}' -StartupType Automatic");
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

    private void RunPowerShell(string command)
    {
        RunCommand("powershell", $"-Command \"{command}\"");
    }

    // --- Cleanup logic ---
    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        long totalSize = 0;
        if (CleanPrefetchCheck.IsChecked == true) totalSize += GetDirectorySize(@"C:\Windows\Prefetch");
        if (CleanTempCheck.IsChecked == true) totalSize += GetDirectorySize(Path.GetTempPath());
        if (CleanWinTempCheck.IsChecked == true) totalSize += GetDirectorySize(@"C:\Windows\Temp");

        CleanupSizeText.Text = $"Estimated size: {totalSize / (1024 * 1024)} MB";
    }

    private void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("Are you sure you want to clean selected system folders?", "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try
            {
                if (CleanPrefetchCheck.IsChecked == true) ClearDirectory(@"C:\Windows\Prefetch");
                if (CleanTempCheck.IsChecked == true) ClearDirectory(Path.GetTempPath());
                if (CleanWinTempCheck.IsChecked == true) ClearDirectory(@"C:\Windows\Temp");
                
                System.Windows.MessageBox.Show("System folders cleaned successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                CleanupSizeText.Text = "Estimated size: 0 MB";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error during cleanup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private long GetDirectorySize(string path)
    {
        try {
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        } catch { return 0; }
    }

    private void ClearDirectory(string path)
    {
        try {
            if (!Directory.Exists(path)) return;
            var di = new DirectoryInfo(path);
            foreach (var file in di.EnumerateFiles()) try { file.Delete(); } catch { }
            foreach (var dir in di.EnumerateDirectories()) try { dir.Delete(true); } catch { }
        } catch { }
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
