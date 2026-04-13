using System.Configuration;
using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using NLog;
using NLog.Config;
using System.Xml;

namespace ParrotBoost;

public partial class App : System.Windows.Application
{
    private static Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);
        ConfigureNLog();
        _logger = LogManager.GetCurrentClassLogger();
        _logger.Info("Application ParrotBoost (JGS) started (Single-File version).");
    }

    private void ConfigureNLog()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "ParrotBoost.nlog.config";
            
            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream))
                    {
                        string xml = reader.ReadToEnd();
                        LogManager.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml);
                    }
                }
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

