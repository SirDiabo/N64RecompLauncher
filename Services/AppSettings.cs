using System.IO;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace N64RecompLauncher.Services
{
    public class AppSettings
    {
        public bool IsPortable { get; set; } = false;
        public bool IconFill { get; set; } = false;
        public float IconOpacity { get; set; } = 0.2f;
        public int IconSize { get; set; } = 112;
        public int IconMargin { get; set; } = 8;
        public int SlotTextMargin { get; set; } = 112;
        public int SlotSize { get; set; } = 120;

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
            try
            {
                string jsonString = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, jsonString);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}