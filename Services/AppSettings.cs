using System;
using System.IO;
using System.Text.Json;

namespace N64RecompLauncher.Services
{
    public class AppSettings
    {
        public bool IsPortable { get; set; } = false;

        private static string SettingsPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "settings.json"
        );

        public static AppSettings Load()
        {
            if (!File.Exists(SettingsPath))
            {
                var defaultSettings = new AppSettings();
                Save(defaultSettings);
                return defaultSettings;
            }

            try
            {
                string jsonString = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(jsonString)
                       ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            string jsonString = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(SettingsPath, jsonString);
        }
    }
}
