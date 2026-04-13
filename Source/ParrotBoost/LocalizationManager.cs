using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace ParrotBoost;

public class LocalizationManager
{
    private static LocalizationManager? _instance;
    public static LocalizationManager Instance => _instance ??= new LocalizationManager();

    private Dictionary<string, object>? _currentLocalization;

    public void SetLanguage(string languageCode)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = $"ParrotBoost.Resources.Locales.{languageCode}.json";
            
            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string json = reader.ReadToEnd();
                        _currentLocalization = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        UpdateResources();
                    }
                }
                else
                {
                    // Fallback to default if not found
                    if (languageCode != "en-US") SetLanguage("en-US");
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Error loading localization from embedded resource: {ex.Message}");
        }
    }

    private void UpdateResources()
    {
        if (_currentLocalization == null) return;

        foreach (var category in _currentLocalization)
        {
            if (category.Value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in element.EnumerateObject())
                {
                    string key = $"{category.Key}.{item.Name}";
                    System.Windows.Application.Current.Resources[key] = item.Value.GetString();
                }
            }
        }
    }

    public string GetString(string key)
    {
        return System.Windows.Application.Current.Resources[key] as string ?? key;
    }
}
