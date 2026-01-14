using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using N64RecompLauncher.Services;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace N64RecompLauncher.Models
{
    public class GameVersionCache
    {
        public string Version { get; set; } = string.Empty;
        public DateTime LastChecked { get; set; }
        public string ETag { get; set; } = string.Empty;
        public GitHubRelease? CachedRelease { get; set; }
    }

    public static class GitHubApiCache
    {
        private static readonly ConcurrentDictionary<string, GameVersionCache> _cache = new();
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(20);

        public static bool TryGetCachedVersion(string repository, out GameVersionCache? cache)
        {
            if (_cache.TryGetValue(repository, out var foundCache))
            {
                if (DateTime.UtcNow - foundCache.LastChecked < CacheExpiry)
                {
                    cache = foundCache;
                    return true;
                }
            }
            cache = null;
            return false;
        }

        public static void SetCache(string repository, string version, string etag, GitHubRelease? release = null)
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
            return _cache.TryGetValue(repository, out var cache) ? cache.ETag : "";
        }
    }

    public class GameInfo : INotifyPropertyChanged
    {
        private string? _latestVersion;
        private string? _installedVersion;
        private GameStatus _status = GameStatus.NotInstalled;
        private bool _isLoading;
        private GitHubRelease? _cachedRelease;
        public GameManager? GameManager { get; set; }

        public string? Name { get; set; }
        public string? Repository { get; set; }
        public string? Branch { get; set; }
        public string? ImageRes { get; set; }
        public string? FolderName { get; set; }
        public string? PlatformOverride { get; set; }
        public bool IsExperimental { get; set; }
        private string? _customIconPath { get; set; }
        public string? CustomIconPath
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

                if (string.IsNullOrEmpty(Repository) || string.IsNullOrEmpty(Branch) || string.IsNullOrEmpty(ImageRes))
                {
                    return "/Assets/DefaultGame.png";
                }

                return $"https://raw.githubusercontent.com/{Repository}/{Branch}/icons/{ImageRes}.png";
            }
        }

        public string DefaultIconUrl
        {
            get
            {
                if (string.IsNullOrEmpty(Repository) || string.IsNullOrEmpty(Branch) || string.IsNullOrEmpty(ImageRes))
                {
                    return "/Assets/DefaultGame.png";
                }

                return $"https://raw.githubusercontent.com/{Repository}/{Branch}/icons/{ImageRes}.png";
            }
        }

        public bool IsInstalled
        {
            get
            {
                return Status == GameStatus.Installed ||
                       Status == GameStatus.UpdateAvailable;
            }
        }
        public string? LatestVersion
        {
            get => _latestVersion;
            set
            {
                if (_latestVersion != value)
                {
                    _latestVersion = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public string? InstalledVersion
        {
            get => _installedVersion;
            set
            {
                if (_installedVersion != value)
                {
                    _installedVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public GameStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(ButtonText));
                    DispatchPropertyChanged(nameof(ButtonImage));
                    DispatchPropertyChanged(nameof(ButtonColor));
                    DispatchPropertyChanged(nameof(StatusText));
                    DispatchPropertyChanged(nameof(IsInstalled));
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    DispatchPropertyChanged();
                }
            }
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

        private Avalonia.Media.Imaging.Bitmap? _buttonImageCache;

        public Avalonia.Media.Imaging.Bitmap ButtonImage
        {
            get
            {
                var imagePath = Status switch
                {
                    GameStatus.NotInstalled => "avares://N64RecompLauncher/Assets/Icons/button_download.png",
                    GameStatus.Installed => "avares://N64RecompLauncher/Assets/Icons/button_launch.png",
                    GameStatus.UpdateAvailable => "avares://N64RecompLauncher/Assets/Icons/button_update.png",
                    GameStatus.Downloading => "avares://N64RecompLauncher/Assets/Icons/button_loading.png",
                    GameStatus.Installing => "avares://N64RecompLauncher/Assets/Icons/button_loading.png",
                    _ => "avares://N64RecompLauncher/Assets/Icons/button_loading.png"
                };

                _buttonImageCache = new Avalonia.Media.Imaging.Bitmap(
                    Avalonia.Platform.AssetLoader.Open(new Uri(imagePath)));

                return _buttonImageCache;
            }
        }

        public IBrush ButtonColor
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
                    return $"Update available!: {InstalledVersion} -> {LatestVersion}";
                return Status switch
                {
                    GameStatus.NotInstalled => "Not installed",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => ""
                };
            }
        }

        public void SetGameManager(GameManager gameManager)
        {
            GameManager = gameManager;
        }

        private void DispatchPropertyChanged([CallerMemberName] string propertyName = "")
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                OnPropertyChanged(propertyName);
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(propertyName));
            }
        }

        static async Task ShowMessageBoxAsync(string message, string title)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = title,
                        Width = 400,
                        Height = 150,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) },
                        new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Center }
                    }
                        }
                    };

                    var okButton = ((StackPanel)messageBox.Content).Children[1] as Button;
                    okButton.Click += (s, e) => messageBox.Close();

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });
        }

        public async Task CheckStatusAsync(HttpClient httpClient, string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
            {
                System.Diagnostics.Debug.WriteLine($"Warning: FolderName is null or empty for game {Name}");
                Status = GameStatus.NotInstalled;
                return;
            }

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
                            InstalledVersion = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();
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
                    InstalledVersion = "";
                }

                await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(LatestVersion) &&
                    !string.IsNullOrEmpty(InstalledVersion) &&
                    InstalledVersion != LatestVersion &&
                    InstalledVersion != "Unknown")
                {
                    Status = GameStatus.UpdateAvailable;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking status for {Name}: {ex.Message}");
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
                throw new ArgumentException("Source file does not exist or path is invalid.");

            if (string.IsNullOrEmpty(FolderName))
                throw new InvalidOperationException("FolderName is required for custom icon operations.");

            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            Directory.CreateDirectory(customIconsDir);

            var extension = Path.GetExtension(sourcePath);
            var fileName = $"{FolderName}_custom{extension}";
            var destinationPath = Path.Combine(customIconsDir, fileName);

            try
            {
                if (!string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath))
                {
                    ClearImageFromMemory();

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    TryDeleteFileWithRetry(CustomIconPath, maxRetries: 3, delayMs: 100);
                }

                File.Copy(sourcePath, destinationPath, true);
                CustomIconPath = destinationPath;

                OnPropertyChanged(nameof(CustomIconPath));
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasCustomIcon));
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

            var pathToDelete = CustomIconPath;

            try
            {
                CustomIconPath = "";

                OnPropertyChanged(nameof(CustomIconPath));
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasCustomIcon));

                ClearImageFromMemory();

                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await Task.Delay(100);

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    try
                    {
                        if (File.Exists(pathToDelete))
                        {
                            TryDeleteFileWithRetry(pathToDelete, maxRetries: 5, delayMs: 200);
                        }
                    }
                    catch (Exception deleteEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Warning: Failed to delete custom icon file {pathToDelete}: {deleteEx.Message}");
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to remove custom icon: {ex.Message}", ex);
            }
        }

        public void LoadCustomIcon(string cacheDirectory)
        {
            if (string.IsNullOrEmpty(FolderName))
                return;

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
            if (string.IsNullOrEmpty(FolderName))
                return;

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
                        TryDeleteFileWithRetry(iconPath, maxRetries: 3, delayMs: 100);
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
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(HasCustomIcon));
            }, DispatcherPriority.Render);
        }

        private static void TryDeleteFileWithRetry(string filePath, int maxRetries = 5, int delayMs = 200)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        var attributes = File.GetAttributes(filePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.Delete(filePath);
                        return;
                    }
                    else
                    {
                        return;
                    }
                }
                catch (IOException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs);

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs);
                }
            }

            System.Diagnostics.Debug.WriteLine($"Unable to delete file after {maxRetries} attempts: {filePath}. File may be in use.");
        }

        private async Task CheckLatestVersionAsync(HttpClient httpClient)
        {
            if (string.IsNullOrEmpty(Repository))
            {
                System.Diagnostics.Debug.WriteLine($"Warning: Repository is null or empty for game {Name}");
                return;
            }

            try
            {
                if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache != null)
                {
                    LatestVersion = cache.Version;
                    _cachedRelease = cache.CachedRelease;

                    if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion) &&
                        InstalledVersion != LatestVersion && InstalledVersion != "Unknown")
                    {
                        Status = GameStatus.UpdateAvailable;
                    }
                    return;
                }

                var releaseRequestUrl = $"https://api.github.com/repos/{Repository}/releases";
                var request = new HttpRequestMessage(HttpMethod.Get, releaseRequestUrl);

                string etag = GitHubApiCache.GetETag(Repository);
                if (!string.IsNullOrEmpty(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                var token = GetGitHubApiToken();
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var response = await httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var existingCache) && existingCache != null)
                    {
                        LatestVersion = existingCache.Version;
                        _cachedRelease = existingCache.CachedRelease;

                        GitHubApiCache.SetCache(Repository, existingCache.Version, existingCache.ETag, existingCache.CachedRelease);
                    }
                    return;
                }

                response.EnsureSuccessStatusCode();

                string responseContent = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<IEnumerable<GitHubRelease>>(responseContent);

                if (releases == null || !releases.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"No releases found for {Repository}");
                    return;
                }

                // Try to find the latest release
                var latestRelease = releases.FirstOrDefault(r => !r.prerelease);
                if (latestRelease != null && !string.IsNullOrWhiteSpace(latestRelease.tag_name))
                {
                    LatestVersion = latestRelease.tag_name;
                    _cachedRelease = latestRelease;

                    string? newETag = response.Headers.ETag?.Tag;
                    GitHubApiCache.SetCache(Repository, latestRelease.tag_name, newETag ?? string.Empty, latestRelease);

                    if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion) &&
                        InstalledVersion != LatestVersion && InstalledVersion != "Unknown")
                    {
                        Status = GameStatus.UpdateAvailable;
                    }
                    return;
                }

                // If no latest release found, check for a pre-release
                var prerelease = releases.FirstOrDefault(r => r.prerelease);

                if (prerelease != null && !string.IsNullOrWhiteSpace(prerelease.tag_name))
                {
                    LatestVersion = prerelease.tag_name;
                    _cachedRelease = prerelease;

                    string? newETag = response.Headers.ETag?.Tag;
                    GitHubApiCache.SetCache(Repository, prerelease.tag_name, newETag ?? string.Empty, prerelease);

                    if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion) &&
                        InstalledVersion != LatestVersion && InstalledVersion != "Unknown")
                    {
                        Status = GameStatus.UpdateAvailable;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Network error fetching latest version for {Repository}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching latest version for {Repository}: {ex.Message}");
            }
        }

        private string GetGitHubApiToken()
        {
            try
            {
                var settings = AppSettings.Load();
                return settings?.GitHubApiToken ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task PerformActionAsync(HttpClient httpClient, string gamesFolder, bool isPortable, AppSettings settings)
        {
            string gamePath = Path.Combine(gamesFolder, FolderName);
            string portableFilePath = Path.Combine(gamePath, "portable.txt");
            string disabledPortableFilePath = Path.Combine(gamePath, "portable_disabled.txt");

            switch (Status)
            {
                case GameStatus.NotInstalled:
                case GameStatus.UpdateAvailable:
                    await DownloadAndInstallAsync(httpClient, gamesFolder, GetLatestRelease(), settings);

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

        private GitHubRelease? GetLatestRelease()
        {
            return _cachedRelease;
        }

        private async Task DownloadAndInstallAsync(HttpClient httpClient, string gamesFolder, GitHubRelease? latestRelease, AppSettings settings)
        {
            if (string.IsNullOrEmpty(FolderName))
            {
                await ShowMessageBoxAsync("Game configuration is invalid (missing folder name).", "Configuration Error");
                return;
            }

            if (string.IsNullOrEmpty(Repository))
            {
                await ShowMessageBoxAsync("Game configuration is invalid (missing repository).", "Configuration Error");
                return;
            }

            try
            {
                Status = GameStatus.Downloading;

                // Determine platform identifier
                string platformIdentifier = GetPlatformIdentifier(settings);
                var gamePath = Path.Combine(gamesFolder, FolderName);
                var versionFile = Path.Combine(gamePath, "version.txt");

                // Check for a cached release first
                if (latestRelease == null)
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache?.CachedRelease != null)
                    {
                        latestRelease = cache.CachedRelease;
                    }
                    else
                    {
                        // Check for the latest release first
                        var releaseRequestUrl = $"https://api.github.com/repos/{Repository}/releases";
                        var releaseResponse = await httpClient.GetStringAsync(releaseRequestUrl);
                        var deserializedReleases = JsonSerializer.Deserialize<IEnumerable<GitHubRelease>>(releaseResponse);

                        if (deserializedReleases == null || !deserializedReleases.Any())
                        {
                            await ShowMessageBoxAsync($"No releases found for {Name}.", "No Releases");
                            Status = GameStatus.NotInstalled;
                            return;
                        }

                        // Prioritize latest releases
                        latestRelease = deserializedReleases.FirstOrDefault(r => !r.prerelease) ??
                                        deserializedReleases.FirstOrDefault(r => r.prerelease);

                        if (latestRelease == null)
                        {
                            await ShowMessageBoxAsync($"No valid releases found for {Name}.", "No Releases");
                            Status = GameStatus.NotInstalled;
                            return;
                        }

                        GitHubApiCache.SetCache(Repository, latestRelease.tag_name, "", latestRelease);
                    }
                }

                // Check if the installed version is already the latest
                if (File.Exists(versionFile))
                {
                    var existingVersion = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();
                    if (existingVersion == latestRelease.tag_name)
                    {
                        Status = GameStatus.Installed;
                        InstalledVersion = existingVersion;
                        LatestVersion = latestRelease.tag_name;

                        if (GameManager != null)
                        {
                            await Dispatcher.UIThread.InvokeAsync(async () =>
                            {
                                await GameManager.LoadGamesAsync();
                            });
                        }
                        return;
                    }
                }

                // Find the appropriate asset for the platform
                var asset = latestRelease.assets?.FirstOrDefault(a =>
                    (!string.IsNullOrEmpty(PlatformOverride) && a.name.Contains(PlatformOverride, StringComparison.OrdinalIgnoreCase)) ||
                    (string.IsNullOrEmpty(PlatformOverride) && a.name.Contains(platformIdentifier, StringComparison.OrdinalIgnoreCase)));

                // If no asset found, show error and return
                if (asset == null)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync(
                            $"No compatible download found for {Name} on platform '{platformIdentifier}'.\n\n" +
                            $"Available assets: {string.Join(", ", latestRelease.assets?.Select(a => a.name) ?? new[] { "none" })}",
                            "Platform Not Supported");
                    });
                    Status = GameStatus.NotInstalled;
                    return;
                }

                // Download the asset
                var downloadPath = Path.Combine(Path.GetTempPath(), asset.name);

                try
                {
                    using (var downloadResponse = await httpClient.GetAsync(asset.browser_download_url))
                    {
                        downloadResponse.EnsureSuccessStatusCode();
                        using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await downloadResponse.Content.CopyToAsync(fs);
                    }

                    // Install or update the game
                    Status = GameStatus.Installing;
                    await InstallOrUpdateGame(downloadPath, gamePath, asset.name, latestRelease.tag_name);

                    // Update status
                    InstalledVersion = latestRelease.tag_name;
                    LatestVersion = latestRelease.tag_name;
                    Status = GameStatus.Installed;
                }
                finally
                {
                    // Clean up download file
                    if (File.Exists(downloadPath))
                    {
                        try
                        {
                            File.Delete(downloadPath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to delete temp file {downloadPath}: {ex.Message}");
                        }
                    }
                }

                // Refresh game list
                if (GameManager != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await GameManager.LoadGamesAsync();
                    });
                }
            }
            catch (HttpRequestException ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Network error installing {Name}: {ex.Message}\n\nPlease check your internet connection.", "Network Error");
                });
                Status = GameStatus.NotInstalled;
            }
            catch (UnauthorizedAccessException ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Permission error installing {Name}: {ex.Message}\n\nPlease check folder permissions.", "Permission Error");
                });
                Status = GameStatus.NotInstalled;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error installing {Name}: {ex.Message}", "Installation Error");
                });
                Status = GameStatus.NotInstalled;
            }
        }

        static async Task InstallOrUpdateGame(string downloadPath, string gamePath, string assetName, string version)
        {
            Directory.CreateDirectory(gamePath);

            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    ZipFile.ExtractToDirectory(downloadPath, gamePath, overwriteFiles: true);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var tempExtractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempExtractPath);

                    try
                    {
                        ZipFile.ExtractToDirectory(downloadPath, tempExtractPath, overwriteFiles: true);

                        var appBundle = Directory.GetDirectories(tempExtractPath, "*.app", SearchOption.AllDirectories)
                            .FirstOrDefault();

                        if (!string.IsNullOrEmpty(appBundle))
                        {
                            var appName = Path.GetFileName(appBundle);
                            var destAppPath = Path.Combine(gamePath, appName);

                            if (Directory.Exists(destAppPath))
                            {
                                Directory.Delete(destAppPath, true);
                            }

                            CopyDirectory(appBundle, destAppPath);
                        }
                        else
                        {
                            CopyDirectory(tempExtractPath, gamePath);
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(tempExtractPath))
                        {
                            Directory.Delete(tempExtractPath, true);
                        }
                    }
                }
                else
                {
                    var tempExtractPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempExtractPath);

                    try
                    {
                        ZipFile.ExtractToDirectory(downloadPath, tempExtractPath, overwriteFiles: true);

                        var tarGzFile = Directory.GetFiles(tempExtractPath, "*.tar.gz", SearchOption.AllDirectories)
                            .FirstOrDefault();

                        if (!string.IsNullOrEmpty(tarGzFile))
                        {
                            ExtractTarGz(tarGzFile, gamePath);
                        }
                        else
                        {
                            CopyDirectory(tempExtractPath, gamePath);
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(tempExtractPath))
                        {
                            Directory.Delete(tempExtractPath, true);
                        }
                    }
                }
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                ExtractTarGz(downloadPath, gamePath);
            }

            try
            {
                TryEnsureExecutableAtRoot(gamePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Warning: EnsureExecutableAtRoot failed: {ex.Message}");
            }

            var versionFile = Path.Combine(gamePath, "version.txt");
            await File.WriteAllTextAsync(versionFile, version).ConfigureAwait(false);

            var portableFilePath = Path.Combine(gamePath, "portable.txt");
            if (!File.Exists(portableFilePath))
            {
                await File.WriteAllTextAsync(portableFilePath, string.Empty).ConfigureAwait(false);
            }
        }

        static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        static void CopyDirectoryContentsInto(string sourceDir, string destDir)
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var destFile = Path.Combine(destDir, relative);
                var destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);
                File.Copy(file, destFile, true);
            }
        }

        static bool HasTopLevelExecutable(string path)
        {
            if (!Directory.Exists(path))
                return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly).Any();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                if (Directory.GetDirectories(path, "*.app", SearchOption.TopDirectoryOnly).Any())
                    return true;
                return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Any(f => !Path.HasExtension(f) && new FileInfo(f).Length > 1024);
            }
            else
            {
                return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Any(f => !Path.HasExtension(f) && new FileInfo(f).Length > 1024);
            }
        }

        static void TryEnsureExecutableAtRoot(string gamePath)
        {
            if (!Directory.Exists(gamePath))
                return;

            if (HasTopLevelExecutable(gamePath))
                return;

            string? candidateAppBundle = null;
            string? candidateFile = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidateAppBundle = Directory.GetDirectories(gamePath, "*.app", SearchOption.AllDirectories).FirstOrDefault();
            }

            if (candidateAppBundle == null)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    candidateFile = Directory.GetFiles(gamePath, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                }
                else
                {
                    candidateFile = Directory.GetFiles(gamePath, "*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            var name = Path.GetFileName(f);
                            if (string.IsNullOrEmpty(name)) return false;
                            if (name.Contains("recomp", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("recompiled", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("drmario", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("drmario64", StringComparison.OrdinalIgnoreCase))
                                return true;
                            if (!Path.HasExtension(name))
                            {
                                try
                                {
                                    return new FileInfo(f).Length > 1024;
                                }
                                catch { return false; }
                            }
                            return false;
                        })
                        .OrderByDescending(f => {
                            try { return new FileInfo(f).Length; } catch { return 0L; }
                        })
                        .FirstOrDefault();
                }
            }

            string? candidateDir = null;
            if (!string.IsNullOrEmpty(candidateAppBundle))
            {
                candidateDir = Path.GetDirectoryName(candidateAppBundle);
                if (candidateDir != null && !candidateDir.Equals(gamePath, StringComparison.OrdinalIgnoreCase))
                {
                    var destAppPath = Path.Combine(gamePath, Path.GetFileName(candidateAppBundle));
                    if (!Directory.Exists(destAppPath))
                    {
                        Directory.Move(candidateAppBundle, destAppPath);
                    }
                    else
                    {
                        CopyDirectoryContentsInto(candidateAppBundle, destAppPath);
                        try
                        {
                            Directory.Delete(candidateAppBundle, true);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Warning deleting original .app bundle: {ex.Message}");
                        }
                    }
                }
                return;
            }

            if (!string.IsNullOrEmpty(candidateFile))
            {
                candidateDir = Path.GetDirectoryName(candidateFile);
            }

            if (string.IsNullOrEmpty(candidateDir))
            {
                var childDirs = Directory.GetDirectories(gamePath, "*", SearchOption.TopDirectoryOnly);
                if (childDirs.Length == 1)
                {
                    candidateDir = childDirs[0];
                }
                else
                {
                    return;
                }
            }

            if (string.IsNullOrEmpty(candidateDir))
                return;

            if (Path.GetFullPath(candidateDir).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar))
                return;

            try
            {
                CopyDirectoryContentsInto(candidateDir, gamePath);

                if (HasTopLevelExecutable(gamePath))
                {
                    try
                    {
                        Directory.Delete(candidateDir, true);

                        var parent = Path.GetDirectoryName(candidateDir);
                        while (!string.IsNullOrEmpty(parent) &&
                               !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        {
                            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                            {
                                var nextParent = Path.GetDirectoryName(parent);
                                Directory.Delete(parent, false);
                                parent = nextParent;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to delete original extracted folder '{candidateDir}': {ex.Message}");
                    }
                }
                else
                {
                    TryDeleteDirectoryIfEmpty(candidateDir, stopAt: gamePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to move subfolder contents to game root: {ex.Message}");
            }
        }

        static void TryDeleteDirectoryIfEmpty(string dir, string stopAt)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return;

                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, false);
                    var parent = Path.GetDirectoryName(dir);
                    if (!string.IsNullOrEmpty(parent) && !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteDirectoryIfEmpty(parent, stopAt);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed while cleaning up directory '{dir}': {ex.Message}");
            }
        }

        static void ExtractTarGz(string sourceFilePath, string destinationDirectoryPath)
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

        static async void ExtractTarGzWindows(string sourceFilePath, string destinationDirectoryPath)
        {
            try
            {
                using var inputStream = File.OpenRead(sourceFilePath);
                using var gzipStream = new System.IO.Compression.GZipStream(inputStream, System.IO.Compression.CompressionMode.Decompress);
                ExtractTarFromStream(gzipStream, destinationDirectoryPath);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error extracting tar.gz: {ex.Message}", "Extraction Error");
                });
                throw;
            }
        }
        
        static void ExtractTarFromStream(Stream tarStream, string destinationDirectoryPath)
        {
            using var reader = new BinaryReader(tarStream);
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

                    using var fileStream = File.Create(destPath);
                    int blocksToRead = (int)Math.Ceiling((double)fileSize / 512);

                    byte[] fileBytes = new byte[blocksToRead * 512];
                    reader.Read(fileBytes, 0, fileBytes.Length);

                    fileStream.Write(fileBytes, 0, (int)fileSize);
                }

                int paddingBytes = 512 - (int)(fileSize % 512);
                if (paddingBytes < 512)
                {
                    reader.ReadBytes(paddingBytes);
                }
            }
        }

        static async void ExtractTarGzUnix(string sourceFilePath, string destinationDirectoryPath)
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

                using var process = Process.Start(startInfo);
                process?.WaitForExit();

                if (process?.ExitCode != 0)
                {
                    string errorOutput = process.StandardError.ReadToEnd();
                    throw new Exception($"Tar extraction failed: {errorOutput}");
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error extracting tar.gz: {ex.Message}", "Extraction Error");
                });
                throw;
            }
        }

        static string GetPlatformIdentifier(AppSettings settings)
        {
            if (settings.Platform == TargetOS.Auto)
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
                        Architecture.Arm64 => "Linux-ARM64",
                        Architecture.X64 => Environment.GetEnvironmentVariable("FLATPAK_ID") != null
                            ? "Linux-Flatpak-X64"
                            : "Linux-X64",
                        _ => "Linux-X64"
                    };
                }

                throw new PlatformNotSupportedException("Unsupported operating system");
            }
            else
            {
                return settings.Platform switch
                {
                    TargetOS.Windows => "Windows",
                    TargetOS.MacOS => "macOS",
                    TargetOS.LinuxX64 => "Linux-X64",
                    TargetOS.LinuxARM64 => "Linux-ARM64",
                    TargetOS.Flatpak => "Linux-Flatpak-X64",
                    _ => throw new PlatformNotSupportedException("Unsupported target OS in settings")
                };
            }
        }

        private async void Launch(string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
            {
                await ShowMessageBoxAsync("Cannot launch game: folder name is not configured.", "Configuration Error");
                return;
            }

            try
            {
                string gamePath = Path.Combine(gamesFolder, FolderName);

                if (!Directory.Exists(gamePath))
                {
                    await ShowMessageBoxAsync($"Game directory not found: {gamePath}", "Directory Not Found");
                    return;
                }

                string? executablePath = null;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var exeFiles = Directory.GetFiles(gamePath, "*.exe", SearchOption.AllDirectories);
                    executablePath = exeFiles.FirstOrDefault();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var appBundles = Directory.GetDirectories(gamePath, "*.app", SearchOption.AllDirectories);
                    if (appBundles.Length > 0)
                    {
                        executablePath = appBundles[0];
                    }
                    else
                    {
                        executablePath = FindExecutableInPath(gamePath);
                    }
                }
                else // Linux
                {
                    executablePath = FindExecutableInPath(gamePath);
                }

                if (string.IsNullOrEmpty(executablePath))
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync(
                            $"No executable found for {Name} in:\n{gamePath}\n\n" +
                            $"The game may not have installed correctly.",
                            "Executable Not Found");
                    });
                    return;
                }

                // Make executable on Unix systems
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    !executablePath.EndsWith(".app"))
                {
                    await MakeExecutableAsync(executablePath);
                }

                // Launch the game
                var startInfo = new ProcessStartInfo();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && executablePath.EndsWith(".app"))
                {
                    startInfo.FileName = "open";
                    startInfo.Arguments = $"\"{executablePath}\"";
                    startInfo.UseShellExecute = false;
                    startInfo.WorkingDirectory = gamePath;
                }
                else
                {
                    startInfo.FileName = executablePath;
                    startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? gamePath;
                    startInfo.UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                }

                UpdateLastPlayedTime(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && executablePath.EndsWith(".app")
                    ? gamePath
                    : (Path.GetDirectoryName(executablePath) ?? gamePath));

                Process.Start(startInfo);

                if (GameManager != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await GameManager.LoadGamesAsync();
                    });
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error launching {Name}: {ex.Message}", "Launch Error");
                });
            }
        }

        private string? FindExecutableInPath(string gamePath)
        {
            var allFiles = Directory.GetFiles(gamePath, "*", SearchOption.AllDirectories);

            // First pass: look for files matching game name or "recomp"
            foreach (var file in allFiles)
            {
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(fileName)) continue;

                // Skip non-executable extensions
                if (fileName.Contains('.') &&
                    (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (fileName.Contains("Recompiled", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(Name) && fileName.Contains(Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                {
                    return file;
                }
            }

            // Second pass: look for any file without extension that's large enough
            return allFiles.FirstOrDefault(f =>
            {
                try
                {
                    return !Path.GetFileName(f).Contains('.') && new FileInfo(f).Length > 1024;
                }
                catch
                {
                    return false;
                }
            });
        }

        private async Task MakeExecutableAsync(string executablePath)
        {
            try
            {
                var chmodProcess = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{executablePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(chmodProcess);
                if (process != null)
                {
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        string errorOutput = await process.StandardError.ReadToEndAsync();
                        System.Diagnostics.Debug.WriteLine($"chmod failed for {executablePath}: {errorOutput}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to make file executable {executablePath}: {ex.Message}");
            }
        }

        private void UpdateLastPlayedTime(string gamePath)
        {
            if (string.IsNullOrEmpty(gamePath))
                return;

            try
            {
                if (!Directory.Exists(gamePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Cannot update LastPlayed: directory does not exist: {gamePath}");
                    return;
                }

                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");
                var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(lastPlayedPath, currentTime);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Permission denied updating LastPlayed.txt for {Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to update LastPlayed.txt for {Name}: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}