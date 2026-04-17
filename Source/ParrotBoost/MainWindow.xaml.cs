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
        _hardwareTelemetry = new HardwareTelemetryService();
        _hardwareTelemetry.CriticalTemperatureDetected += OnCriticalTemperatureDetected;
        _runtimeOptimizer = new ParrotBoostRuntimeOptimizer();
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

        _performanceTimer = new System.Windows.Threading.DispatcherTimer();
        _performanceTimer.Interval = TimeSpan.FromSeconds(1); // Odświeżanie 1Hz zgodnie z wymogami 1:1
        _performanceTimer.Tick += PerformanceTimer_Tick;
        _performanceTimer.Start();
    }

    private async void PerformanceTimer_Tick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _performanceRefreshInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            var sample = await Task.Run(() =>
            {
                float cpuLoad = _hardwareTelemetry.TryGetCpuLoad() ?? 0;
                float gpuLoad = _hardwareTelemetry.TryGetGpuLoad() ?? 0;
                float? cpuTemp = _hardwareTelemetry.TryGetCpuTemperature();
                float? gpuTemp = _hardwareTelemetry.TryGetGpuTemperature();

                cpuLoad = Compatibility.Clamp(cpuLoad, 0, 100);
                gpuLoad = Compatibility.Clamp(gpuLoad, 0, 100);

                float displayCpuTemp = cpuTemp ?? 0;
                float displayGpuTemp = gpuTemp ?? 0;

                if (cpuTemp.HasValue && cpuTemp.Value > 0) _cpuTempReadFailures = 0;
                else if (++_cpuTempReadFailures >= 3) displayCpuTemp = 0;

                if (gpuTemp.HasValue && gpuTemp.Value > 0) _gpuTempReadFailures = 0;
                else if (++_gpuTempReadFailures >= 3) displayGpuTemp = 0;

                return (cpuLoad, gpuLoad, displayCpuTemp, displayGpuTemp);
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
        CleanComponentStoreCheck.IsChecked = _settings.CleanComponentStore;
        CleanMinidumpsCheck.IsChecked = _settings.CleanMinidumps;
        OptimizeBootFilesCheck.IsChecked = _settings.OptimizeBootFiles;

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
        _settings.CleanComponentStore = CleanComponentStoreCheck.IsChecked ?? false;
        _settings.CleanMinidumps = CleanMinidumpsCheck.IsChecked ?? false;
        _settings.OptimizeBootFiles = OptimizeBootFilesCheck.IsChecked ?? false;
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
        DetectOptimizationState();
        SetupPerformanceTimer();
        SetupTrayIcon();

        await LoadHardwareInfoAsync();
        await CheckForDriverUpdatesAsync();
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
        _hardwareTelemetry.CriticalTemperatureDetected -= OnCriticalTemperatureDetected;
        _hardwareTelemetry.Dispose();
        base.OnClosed(e);
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
            ProgressStatus.Text = "Boost active. Click STOP to restore.";
            BoostPercentage.Text = "+18%";
            SetBoostCpuNoticeVisible(true);
        }
        else
        {
            BoostButton.Content = "BOOST";
            BoostButton.Background = (SolidColorBrush)Resources["ColorPrimaryBrush"];
            ProgressStatus.Text = "System is ready to boost";
            BoostPercentage.Text = "+5-12%";
            SetBoostCpuNoticeVisible(false);
        }
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
    }

    private void SetBoostCpuNoticeVisible(bool isVisible)
    {
        BoostCpuNoticeBorder.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
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

        SetBoostCpuNoticeVisible(true);
        string[] statusSteps = {
            "Preparing Game Mode...",
            "Optimizing Visual Effects...",
            "Optimizing Background Tasks...",
            "Fine-tuning System Timers...",
            "Configuring Power & USB...",
            "Cleaning Temporary Files...",
            "Finalizing System Priority..."
        };

        foreach (var step in statusSteps)
        {
            ProgressStatus.Text = step;
            await Task.Run(() => 
            {
                switch (step)
                {
                    case "Preparing Game Mode...": if (_settings.EnableGameMode) _gameModeService.Activate(); break;
                    case "Optimizing Visual Effects...": if (_settings.OptServices) SetVisualEffects(true); DisableUwpAnimations(true); break;
                    case "Optimizing Background Tasks...": if (_settings.OptTasks) OptimizeTaskScheduler(); if (_settings.OptDelivery) DisableDeliveryOptimization(); break;
                    case "Fine-tuning System Timers...": if (_settings.OptTick) { SetDynamicTick(false); SetHpet(false); } ClearIconCache(); break;
                    case "Configuring Power & USB...": CreateTurboParrotPowerPlan(); if (_settings.OptUsb) OptimizeUsbPower(true); SetTimerCoalescing(true); break;
                    case "Cleaning Temporary Files...": ClearTempFolders(); break;
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
        ProgressStatus.Text = "Restoring system settings...";
        
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
        BoostButton.IsEnabled = true;
        SetBoostCpuNoticeVisible(false);
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
        long totalSize = await Task.Run(() =>
        {
            long size = 0;
            if (CleanPrefetchCheck.IsChecked == true) size += GetDirectorySize(@"C:\Windows\Prefetch");
            if (CleanTempCheck.IsChecked == true) size += GetDirectorySize(Path.GetTempPath());
            if (CleanWinTempCheck.IsChecked == true) size += GetDirectorySize(@"C:\Windows\Temp");
            if (CleanUpdateCacheCheck.IsChecked == true) size += GetDirectorySize(@"C:\Windows\SoftwareDistribution\Download");
            if (CleanMinidumpsCheck.IsChecked == true) size += GetDirectorySize(@"C:\Windows\Minidump");
            return size;
        });

        CleanupSizeText.Text = $"Estimated size: {totalSize / (1024 * 1024)} MB";
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("Are you sure you want to clean selected system folders?", "Confirm Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try
            {
                BoostButton.IsEnabled = false;
                SetBoostCpuNoticeVisible(true);

                await Task.Run(() =>
                {
                    if (CleanPrefetchCheck.IsChecked == true) ClearDirectory(@"C:\Windows\Prefetch");
                    if (CleanTempCheck.IsChecked == true) ClearDirectory(Path.GetTempPath());
                    if (CleanWinTempCheck.IsChecked == true) ClearDirectory(@"C:\Windows\Temp");
                    if (CleanUpdateCacheCheck.IsChecked == true) ClearDirectory(@"C:\Windows\SoftwareDistribution\Download");
                    if (CleanRecycleBinCheck.IsChecked == true) EmptyRecycleBin();
                    if (CleanMinidumpsCheck.IsChecked == true) ClearDirectory(@"C:\Windows\Minidump");
                    if (CleanComponentStoreCheck.IsChecked == true) RunCommand("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup");
                    if (OptimizeBootFilesCheck.IsChecked == true) RunCommand("defrag.exe", $"{Path.GetPathRoot(Environment.SystemDirectory)} /B");
                });
                
                System.Windows.MessageBox.Show("System folders cleaned successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                CleanupSizeText.Text = "Estimated size: 0 MB";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error during cleanup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BoostButton.IsEnabled = true;
                SetBoostCpuNoticeVisible(IsBoostActive);
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

    private void EmptyRecycleBin()
    {
        try
        {
            SHEmptyRecycleBin(IntPtr.Zero, null, RecycleBinNoConfirmation | RecycleBinNoProgressUi | RecycleBinNoSound);
        }
        catch
        {
        }
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
