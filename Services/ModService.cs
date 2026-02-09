using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace N64RecompLauncher.Services
{
    public class ThunderstorePackage
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = string.Empty;

        [JsonPropertyName("owner")]
        public string Owner { get; set; } = string.Empty;

        [JsonPropertyName("package_url")]
        public string PackageUrl { get; set; } = string.Empty;

        [JsonPropertyName("date_created")]
        public string DateCreated { get; set; } = string.Empty;

        [JsonPropertyName("date_updated")]
        public string DateUpdated { get; set; } = string.Empty;

        [JsonPropertyName("rating_score")]
        public int RatingScore { get; set; }

        [JsonPropertyName("is_pinned")]
        public bool IsPinned { get; set; }

        [JsonPropertyName("is_deprecated")]
        public bool IsDeprecated { get; set; }

        [JsonPropertyName("has_nsfw_content")]
        public bool HasNsfwContent { get; set; }

        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = new();

        [JsonPropertyName("versions")]
        public List<ThunderstoreVersion> Versions { get; set; } = new();

        [JsonIgnore]
        public ThunderstoreVersion? LatestVersion => Versions.FirstOrDefault();
    }

    public class ThunderstoreVersion
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        [JsonPropertyName("version_number")]
        public string VersionNumber { get; set; } = string.Empty;

        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();

        [JsonPropertyName("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("downloads")]
        public int Downloads { get; set; }

        [JsonPropertyName("date_created")]
        public string DateCreated { get; set; } = string.Empty;

        [JsonPropertyName("website_url")]
        public string WebsiteUrl { get; set; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }
    }

    public class ThunderstoreService : IDisposable
    {
        private readonly HttpClient _client;
        private bool _disposed = false;
        
        //Add Thunderstore pages and github repository paths here
        private static readonly Dictionary<string, string> CommunityMapping = new()
        {
            { "Zelda64Recomp/Zelda64Recomp", "zelda-64-recompiled" },
            { "BanjoRecomp/BanjoRecomp", "banjo-recompiled" },
            { "sonicdcer/Starfox64Recomp", "starfox-64-recompiled" },

        };

        public ThunderstoreService()
        {
            _client = new HttpClient();
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("N64RecompLauncher/1.0");
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        public static string? GetCommunityForRepository(string repository)
        {
            if (string.IsNullOrEmpty(repository))
                return null;

            return CommunityMapping.TryGetValue(repository, out var community) ? community : null;
        }

        public async Task<List<ThunderstorePackage>> GetPackagesAsync(string community)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(community))
                    throw new ArgumentException("Community identifier cannot be empty", nameof(community));

                string url = $"https://thunderstore.io/c/{community}/api/v1/package/";
                System.Diagnostics.Debug.WriteLine($"Fetching mods from: {url}");

                var response = await _client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var packages = JsonSerializer.Deserialize<List<ThunderstorePackage>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return packages ?? new List<ThunderstorePackage>();
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"HTTP error fetching packages: {ex.Message}");
                throw new Exception($"Failed to fetch mods from Thunderstore: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"JSON parsing error: {ex.Message}");
                throw new Exception($"Failed to parse mod data: {ex.Message}", ex);
            }
        }

        public async Task<ThunderstorePackage?> GetPackageAsync(string community, string owner, string name)
        {
            try
            {
                string url = $"https://thunderstore.io/c/{community}/api/v1/package/{owner}/{name}/";
                System.Diagnostics.Debug.WriteLine($"Fetching package from: {url}");
                
                var response = await _client.GetAsync(url);
                
                System.Diagnostics.Debug.WriteLine($"Response status: {response.StatusCode}");
                
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to fetch package {owner}/{name}: {response.StatusCode} - {response.ReasonPhrase}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"Response JSON length: {json.Length}");
                
                var package = JsonSerializer.Deserialize<ThunderstorePackage>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                
                System.Diagnostics.Debug.WriteLine($"Package deserialized: {package != null}, Versions count: {package?.Versions?.Count ?? 0}");
                
                return package;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching package {owner}/{name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Exception type: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        public async Task DownloadModAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await _client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[8192];
                long downloaded = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloaded += bytesRead;

                    if (progress != null && totalBytes > 0)
                    {
                        progress.Report((double)downloaded / totalBytes * 100);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Download cancelled: {downloadUrl}");
                try
                {
                    if (File.Exists(targetFilePath))
                    {
                        File.Delete(targetFilePath);
                    }
                }
                catch { }
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error downloading mod: {ex.Message}");
                throw new Exception($"Failed to download mod: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _client?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}