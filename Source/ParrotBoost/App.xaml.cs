using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using NLog;
using NLog.Config;

namespace ParrotBoost;

public partial class App : System.Windows.Application
{
    private static Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
#if NET5_0_OR_GREATER
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
#endif
        base.OnStartup(e);
        ConfigureNLog();
        _logger = LogManager.GetCurrentClassLogger();
        _logger.Info("Application ParrotBoost (JGS) started (Single-File version).");

        if (e.Args.Contains("--self-diagnostics", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    ParrotBoostRuntimeOptimizer.RunSelfDiagnostics();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Self-diagnostics failed.");
                }
            });
        }
    }

    private static void ConfigureNLog()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "ParrotBoost.nlog.config";
            
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                string xml = reader.ReadToEnd();
                LogManager.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error initializing NLog from embedded resource: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("Application ParrotBoost (JGS) exiting.");
        LogManager.Shutdown();
        base.OnExit(e);
    }
}

