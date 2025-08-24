using N64RecompLauncher.Services;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

public enum GameStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Downloading,
    Installing
}

namespace N64RecompLauncher.Models
{
    public class GameVersionCache
    {
        public string Version { get; set; }
        public DateTime LastChecked { get; set; }
        public string ETag { get; set; }
        public GitHubRelease CachedRelease { get; set; }
    }

    public static class GitHubApiCache
    {
        private static readonly ConcurrentDictionary<string, GameVersionCache> _cache = new();
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(20);

        public static bool TryGetCachedVersion(string repository, out GameVersionCache cache)
        {
            if (_cache.TryGetValue(repository, out cache))
            {
                if (DateTime.UtcNow - cache.LastChecked < CacheExpiry)
                {
                    return true;
                }
            }
            cache = null;
            return false;
        }

        public static void SetCache(string repository, string version, string etag, GitHubRelease release = null)
        {
            _cache.AddOrUpdate(repository,
                new GameVersionCache
                {
                    Version = version,
                    LastChecked = DateTime.UtcNow,
                    ETag = etag,
                    CachedRelease = release
                },
                (key, old) => new GameVersionCache
                {
                    Version = version,
                    LastChecked = DateTime.UtcNow,
                    ETag = etag ?? old.ETag,
                    CachedRelease = release ?? old.CachedRelease
                });
        }

        public static string GetETag(string repository)
        {
            return _cache.TryGetValue(repository, out var cache) ? cache.ETag : null;
        }
    }

    public class GameInfo : INotifyPropertyChanged
    {
        private string _latestVersion;
        private string _installedVersion;
        private GameStatus _status = GameStatus.NotInstalled;
        private bool _isLoading;
        private GitHubRelease _cachedRelease;
        public GameManager GameManager { get; set; }

        public string Name { get; set; }
        public string Repository { get; set; }
        public string Branch { get; set; }
        public string ImageRes { get; set; }
        public string FolderName { get; set; }

        private string _customIconPath;
        public string CustomIconPath
        {
            get => _customIconPath;
            set
            {
                if (_customIconPath != value)
                {
                    _customIconPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IconUrl));
                    OnPropertyChanged(nameof(HasCustomIcon));
                }
            }
        }
        public bool HasCustomIcon => !string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath);

        public string IconUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath))
                {
                    return CustomIconPath;
                }
                return $"https://raw.githubusercontent.com/{Repository}/{Branch}/icons/{ImageRes}.png";
            }
        }

        public string LatestVersion
        {
            get => _latestVersion;
            set { _latestVersion = value; OnPropertyChanged(); }
        }

        public string InstalledVersion
        {
            get => _installedVersion;
            set { _installedVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
        }

        public GameStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(ButtonText)); OnPropertyChanged(nameof(ButtonColor)); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public string ButtonText
        {
            get
            {
                return Status switch
                {
                    GameStatus.NotInstalled => "Download",
                    GameStatus.Installed => "Launch",
                    GameStatus.UpdateAvailable => "Update",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => "Download"
                };
            }
        }

        public Brush ButtonColor
        {
            get
            {
                return Status switch
                {
                    GameStatus.NotInstalled => new SolidColorBrush(Color.FromRgb(0, 122, 255)),
                    GameStatus.Installed => new SolidColorBrush(Color.FromRgb(52, 199, 89)),
                    GameStatus.UpdateAvailable => new SolidColorBrush(Color.FromRgb(255, 149, 0)),
                    GameStatus.Downloading or GameStatus.Installing => new SolidColorBrush(Color.FromRgb(142, 142, 147)),
                    _ => new SolidColorBrush(Color.FromRgb(0, 122, 255))
                };
            }
        }

        public string StatusText
        {
            get
            {
                if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion))
                    return $"Installed: {InstalledVersion}";
                if (Status == GameStatus.UpdateAvailable && !string.IsNullOrEmpty(LatestVersion))
                    return $"Update available: {LatestVersion}";
                return Status switch
                {
                    GameStatus.NotInstalled => "Not installed",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => ""
                };
            }
        }

        public async Task CheckStatusAsync(HttpClient httpClient, string gamesFolder)
        {
            IsLoading = true;

            try
            {
                var gamePath = Path.Combine(gamesFolder, FolderName);
                var versionFile = Path.Combine(gamePath, "version.txt");

                bool directoryExists = Directory.Exists(gamePath);
                bool versionFileExists = File.Exists(versionFile);

                if (directoryExists)
                {
                    if (versionFileExists)
                    {
                        try
                        {
                            InstalledVersion = await File.ReadAllTextAsync(versionFile).ConfigureAwait(false);
                        }
                        catch
                        {
                            InstalledVersion = "Unknown";
                        }

                        Status = GameStatus.Installed;
                    }
                    else
                    {
                        Status = GameStatus.Installed;
                        InstalledVersion = "Unknown";
                    }
                }
                else
                {
                    Status = GameStatus.NotInstalled;
                    InstalledVersion = null;
                }

                await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(LatestVersion) &&
                    !string.IsNullOrEmpty(InstalledVersion) &&
                    InstalledVersion != LatestVersion)
                {
                    Status = GameStatus.UpdateAvailable;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking status for {Name}: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Status = GameStatus.NotInstalled;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void SetCustomIcon(string sourcePath, string cacheDirectory)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                return;

            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            Directory.CreateDirectory(customIconsDir);

            var extension = Path.GetExtension(sourcePath);
            var fileName = $"{FolderName}_custom{extension}";
            var destinationPath = Path.Combine(customIconsDir, fileName);

            try
            {
                if (!string.IsNullOrEmpty(CustomIconPath))
                {
                    ClearImageFromMemory();

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    RemoveAllCachedIcons(cacheDirectory);
                }

                File.Copy(sourcePath, destinationPath, true);
                CustomIconPath = destinationPath;

                OnPropertyChanged(nameof(CustomIconPath));
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to set custom icon: {ex.Message}", ex);
            }
        }

        public void RemoveCustomIcon()
        {
            if (string.IsNullOrEmpty(CustomIconPath))
                return;

            try
            {
                ClearImageFromMemory();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                if (File.Exists(CustomIconPath))
                {
                    TryDeleteFileWithRetry(CustomIconPath);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to remove custom icon: {ex.Message}", ex);
            }
            finally
            {
                CustomIconPath = null;

                OnPropertyChanged(nameof(CustomIconPath));
            }
        }

        public void LoadCustomIcon(string cacheDirectory)
        {
            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            if (!Directory.Exists(customIconsDir))
                return;

            var possibleExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" };
            foreach (var ext in possibleExtensions)
            {
                var fileName = $"{FolderName}_custom{ext}";
                var iconPath = Path.Combine(customIconsDir, fileName);
                if (File.Exists(iconPath))
                {
                    CustomIconPath = iconPath;
                    break;
                }
            }
        }

        private void RemoveAllCachedIcons(string cacheDirectory)
        {
            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            if (!Directory.Exists(customIconsDir))
                return;

            var possibleExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" };
            foreach (var ext in possibleExtensions)
            {
                var fileName = $"{FolderName}_custom{ext}";
                var iconPath = Path.Combine(customIconsDir, fileName);
                if (File.Exists(iconPath))
                {
                    try
                    {
                        TryDeleteFileWithRetry(iconPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Warning: Failed to delete cached icon {iconPath}: {ex.Message}");
                    }
                }
            }
        }

        private void ClearImageFromMemory()
        {
            var tempPath = CustomIconPath;
            CustomIconPath = null;

            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }

            System.Threading.Thread.Sleep(100);
        }

        private void TryDeleteFileWithRetry(string filePath, int maxRetries = 5, int delayMs = 200)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Delete(filePath);
                    return;
                }
                catch (IOException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs);
                }
                catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs);
                }
            }

            throw new IOException($"Unable to delete file after {maxRetries} attempts: {filePath}");
        }

        private async Task CheckLatestVersionAsync(HttpClient httpClient)
        {
            try
            {
                if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache))
                {
                    LatestVersion = cache.Version;
                    _cachedRelease = cache.CachedRelease;

                    if (Status == GameStatus.Installed && InstalledVersion != LatestVersion)
                    {
                        Status = GameStatus.UpdateAvailable;
                    }
                    return;
                }

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");

                string etag = GitHubApiCache.GetETag(Repository);
                if (!string.IsNullOrEmpty(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                var response = await httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var existingCache))
                    {
                        LatestVersion = existingCache.Version;
                        _cachedRelease = existingCache.CachedRelease;

                        GitHubApiCache.SetCache(Repository, existingCache.Version, existingCache.ETag, existingCache.CachedRelease);
                    }
                    return;
                }

                response.EnsureSuccessStatusCode();

                string responseContent = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GitHubRelease>(responseContent);

                if (release != null && !string.IsNullOrWhiteSpace(release.tag_name))
                {
                    LatestVersion = release.tag_name;
                    _cachedRelease = release;

                    string newETag = response.Headers.ETag?.Tag;
                    GitHubApiCache.SetCache(Repository, release.tag_name, newETag, release);

                    if (Status == GameStatus.Installed && InstalledVersion != LatestVersion)
                    {
                        Status = GameStatus.UpdateAvailable;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching latest version for {Repository}: {ex.Message}");
            }
        }

        public async Task PerformActionAsync(HttpClient httpClient, string gamesFolder, bool isPortable)
        {
            string gamePath = Path.Combine(gamesFolder, FolderName);
            string portableFilePath = Path.Combine(gamePath, "portable.txt");
            string disabledPortableFilePath = Path.Combine(gamePath, "portable_disabled.txt");

            switch (Status)
            {
                case GameStatus.NotInstalled:
                case GameStatus.UpdateAvailable:
                    await DownloadAndInstallAsync(httpClient, gamesFolder);

                    if (File.Exists(portableFilePath))
                    {
                        if (!isPortable)
                        {
                            File.Move(portableFilePath, disabledPortableFilePath, true);
                        }
                    }
                    break;

                case GameStatus.Installed:
                    if (File.Exists(portableFilePath) && !isPortable)
                    {
                        File.Move(portableFilePath, disabledPortableFilePath, true);
                    }
                    else if (File.Exists(disabledPortableFilePath) && isPortable)
                    {
                        File.Move(disabledPortableFilePath, portableFilePath, true);
                    }
                    else if (!File.Exists(portableFilePath) && !File.Exists(disabledPortableFilePath) && isPortable)
                    {
                        Directory.CreateDirectory(gamePath);
                        File.Create(portableFilePath).Close();
                    }

                    Launch(gamesFolder);
                    break;
            }
        }

        private async Task DownloadAndInstallAsync(HttpClient httpClient, string gamesFolder)
        {
            try
            {
                Status = GameStatus.Downloading;

                string platformIdentifier = GetPlatformIdentifier();

                var gamePath = Path.Combine(gamesFolder, FolderName);
                var versionFile = Path.Combine(gamePath, "version.txt");

                if (File.Exists(versionFile))
                {
                    var existingVersion = await File.ReadAllTextAsync(versionFile).ConfigureAwait(false);

                    GitHubRelease release = _cachedRelease;
                    if (release == null)
                    {
                        if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache.CachedRelease != null)
                        {
                            release = cache.CachedRelease;
                        }
                        else
                        {
                            var response = await httpClient.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
                            release = JsonSerializer.Deserialize<GitHubRelease>(response);

                            GitHubApiCache.SetCache(Repository, release.tag_name, null, release);
                        }
                    }

                    LatestVersion = release.tag_name;

                    if (GameManager != null)
                    {
                        await GameManager.LoadGamesAsync();
                    }

                    if (existingVersion == LatestVersion)
                    {
                        Status = GameStatus.Installed;
                        InstalledVersion = existingVersion;
                        return;
                    }

        }

                GitHubRelease latestRelease = _cachedRelease;
                if (latestRelease == null)
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache.CachedRelease != null)
                    {
                        latestRelease = cache.CachedRelease;
                    }
                    else
                    {
                        var releaseResponse = await httpClient.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
                        latestRelease = JsonSerializer.Deserialize<GitHubRelease>(releaseResponse);

                        GitHubApiCache.SetCache(Repository, latestRelease.tag_name, null, latestRelease);
                    }
                }

                var asset = latestRelease.assets.FirstOrDefault(a =>
                    a.name.Contains(platformIdentifier, StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                {
                    MessageBox.Show($"No downloadable release found for {Name} on {platformIdentifier}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    Status = GameStatus.NotInstalled;
                    return;
                }

                var downloadPath = Path.Combine(Path.GetTempPath(), asset.name);
                using (var downloadResponse = await httpClient.GetAsync(asset.browser_download_url))
                {
                    downloadResponse.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(downloadPath, FileMode.Create))
                    {
                        await downloadResponse.Content.CopyToAsync(fs);
                    }
                }

                Status = GameStatus.Installing;

                if (Directory.Exists(gamePath))
                {
                    Directory.Delete(gamePath, true);
                }
                Directory.CreateDirectory(gamePath);

                if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(downloadPath, gamePath);
                }
                else if (asset.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractTarGz(downloadPath, gamePath);
                }

                var portableFilePath = Path.Combine(gamePath, "portable.txt");
                await File.WriteAllTextAsync(portableFilePath, string.Empty).ConfigureAwait(false);

                await File.WriteAllTextAsync(versionFile, latestRelease.tag_name).ConfigureAwait(false);

                File.Delete(downloadPath);

                InstalledVersion = latestRelease.tag_name;
                LatestVersion = latestRelease.tag_name;
                Status = GameStatus.Installed;
            }
            catch (HttpRequestException ex)
            {
                MessageBox.Show($"Network error installing {Name}: {ex.Message}",
                    "Network Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Status = GameStatus.NotInstalled;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error installing {Name}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Status = GameStatus.NotInstalled;
            }
        }

        private void ExtractTarGz(string sourceFilePath, string destinationDirectoryPath)
        {
            Directory.CreateDirectory(destinationDirectoryPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ExtractTarGzWindows(sourceFilePath, destinationDirectoryPath);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                     RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                ExtractTarGzUnix(sourceFilePath, destinationDirectoryPath);
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported operating system for tar.gz extraction");
            }
        }

        private void ExtractTarGzWindows(string sourceFilePath, string destinationDirectoryPath)
        {
            try
            {
                using (var inputStream = File.OpenRead(sourceFilePath))
                using (var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress))
                {
                    ExtractTarFromStream(gzipStream, destinationDirectoryPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting tar.gz: {ex.Message}",
                    "Extraction Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private void ExtractTarFromStream(Stream tarStream, string destinationDirectoryPath)
        {
            using (var reader = new BinaryReader(tarStream))
            {
                while (true)
                {
                    var headerBytes = reader.ReadBytes(512);
                    if (headerBytes.Length < 512) break;

                    var fileName = Encoding.ASCII.GetString(headerBytes, 0, 100).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(fileName)) break;

                    var fileSizeStr = Encoding.ASCII.GetString(headerBytes, 124, 12).TrimEnd('\0');
                    var fileSize = Convert.ToInt64(fileSizeStr, 8);

                    var fileType = headerBytes[156];

                    var destPath = Path.Combine(destinationDirectoryPath, fileName);

                    if (fileType == '5')
                    {
                        Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));

                        using (var fileStream = File.Create(destPath))
                        {
                            int blocksToRead = (int)Math.Ceiling((double)fileSize / 512);

                            byte[] fileBytes = new byte[blocksToRead * 512];
                            reader.Read(fileBytes, 0, fileBytes.Length);

                            fileStream.Write(fileBytes, 0, (int)fileSize);
                        }
                    }

                    int paddingBytes = 512 - (int)(fileSize % 512);
                    if (paddingBytes < 512)
                    {
                        reader.ReadBytes(paddingBytes);
                    }
                }
            }
        }

        private void ExtractTarGzUnix(string sourceFilePath, string destinationDirectoryPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xzf \"{sourceFilePath}\" -C \"{destinationDirectoryPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        string errorOutput = process.StandardError.ReadToEnd();
                        throw new Exception($"Tar extraction failed: {errorOutput}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting tar.gz: {ex.Message}",
                    "Extraction Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private string GetPlatformIdentifier()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "Windows";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "macOS";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var arch = RuntimeInformation.OSArchitecture;
                return arch switch
                {
                    Architecture.Arm64 => "ARM64",
                    Architecture.X64 => Environment.GetEnvironmentVariable("FLATPAK_ID") != null
                        ? "X64-Flatpak"
                        : "Linux-X64",
                    _ => "Linux-X64"
                };
            }

            throw new PlatformNotSupportedException("Unsupported operating system");
        }

        private async void Launch(string gamesFolder)
        {
            try
            {
                var gamePath = Path.Combine(gamesFolder, FolderName);

                var executablePath = Directory.GetFiles(gamePath, "*.exe")
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(executablePath))
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        executablePath = Directory.GetFiles(gamePath)
                            .FirstOrDefault(f =>
                                !f.EndsWith(".dll") &&
                                !f.EndsWith(".so") &&
                                File.GetAttributes(f).HasFlag(FileAttributes.Normal));
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        executablePath = Directory.GetFiles(gamePath, "*")
                            .FirstOrDefault(f =>
                                !f.EndsWith(".dll") &&
                                !f.EndsWith(".so"));
                    }
                }

                if (string.IsNullOrEmpty(executablePath))
                {
                    MessageBox.Show($"No executable found in {gamePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = gamePath,
                    UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                };

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    startInfo.UseShellExecute = false;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                        RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        var chmodProcess = new ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{executablePath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        Process.Start(chmodProcess).WaitForExit();
                    }
                }

                UpdateLastPlayedTime(gamePath);

                Process.Start(startInfo);

                if (GameManager != null)
                {
                    await GameManager.LoadGamesAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error launching {Name}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateLastPlayedTime(string gamePath)
        {
            try
            {
                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");
                var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(lastPlayedPath, currentTime);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update LastPlayed.txt for {Name}: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}