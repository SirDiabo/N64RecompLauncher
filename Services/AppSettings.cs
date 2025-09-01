using System;
using System.IO;
using System.Text.Json;

namespace N64RecompLauncher
{
    public class AppSettings
    {
        public bool IsPortable { get; set; } = false;
        public bool IconFill { get; set; } = false;
        public bool PortraitFrame { get; set; } = false;
        public float IconOpacity { get; set; } = 1.0f;
        public int IconSize { get; set; } = 112;
        public int IconMargin { get; set; } = 8;
        public int SlotTextMargin { get; set; } = 112;
        public int SlotSize { get; set; } = 120;

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "settings.json"
        );

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
                throw;
            }
        }
    }
}