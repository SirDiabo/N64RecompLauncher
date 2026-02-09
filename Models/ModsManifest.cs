using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace N64RecompLauncher.Models
{
    /// <summary>
    /// Tracks installed mods for a game
    /// </summary>
    public class InstalledModInfo
    {
        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("installedDate")]
        public DateTime InstalledDate { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("files")]
        public List<string> Files { get; set; } = new();

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();
    }


    public class ModsManifest
    {
        [JsonPropertyName("mods")]
        public List<InstalledModInfo> Mods { get; set; } = new();

        [JsonPropertyName("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
