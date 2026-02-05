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
        public DateTime LastUpdateCheck { get; set; }
    }

    public static class GitHubApiCache
    {
        private static readonly ConcurrentDictionary<string, GameVersionCache> _cache = new();
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);
        private static readonly TimeSpan InstalledGameUpdateInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan NotInstalledGameUpdateInterval = TimeSpan.FromHours(24);
        private static string? _cacheFilePath;

        public static void Initialize(string cacheDirectory)
        {
            _cacheFilePath = Path.Combine(cacheDirectory, "version_cache.json");
            LoadFromDisk();
        }

        // Load cache from disk
        private static void LoadFromDisk()
        {
            if (string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath))
                return;

            try
            {
                var json = File.ReadAllText(_cacheFilePath);
                var diskCache = JsonSerializer.Deserialize<Dictionary<string, GameVersionCache>>(json);
                if (diskCache != null)
                {
                    foreach (var kvp in diskCache)
                    {
                        _cache.TryAdd(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load version cache: {ex.Message}");
            }
        }

        // Save cache to disk
        private static void SaveToDisk()
        {
            if (string.IsNullOrEmpty(_cacheFilePath))
                return;

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_cache.ToDictionary(k => k.Key, v => v.Value), options);
                File.WriteAllText(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save version cache: {ex.Message}");
            }
        }

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

        // Check if update check is needed
        public static bool NeedsUpdateCheck(string repository, bool isInstalledGame = true)
        {
            if (!_cache.TryGetValue(repository, out var cache))
                return true;

            var interval = isInstalledGame ? InstalledGameUpdateInterval : NotInstalledGameUpdateInterval;
            return DateTime.UtcNow - cache.LastUpdateCheck >= interval;
        }

        public static void SetCache(string repository, string version, string etag, GitHubRelease? release = null)
        {
            _cache.AddOrUpdate(repository,
                new GameVersionCache
                {
                    Version = version,
                    LastChecked = DateTime.UtcNow,
                    LastUpdateCheck = DateTime.UtcNow,
                    ETag = etag,
                    CachedRelease = release
                },
                (key, old) => new GameVersionCache
                {
                    Version = version,
                    LastChecked = DateTime.UtcNow,
                    LastUpdateCheck = DateTime.UtcNow,
                    ETag = etag ?? old.ETag,
                    CachedRelease = release ?? old.CachedRelease
                });

            SaveToDisk();
        }

        public static string GetETag(string repository)
        {
            return _cache.TryGetValue(repository, out var cache) ? cache.ETag : "";
        }
    }

    public class GameInfo : INotifyPropertyChanged, IDisposable
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
        public string? CustomDefaultIconUrl { get; set; }
        public bool IsExperimental { get; set; }
        public bool IsCustom { get; set; }
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
        private string? _cachedDefaultIconPath;
        public bool HasCustomIcon => !string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath);

        public string IconUrl
        {
            get
            {
                // custom cover image
                if (!string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath))
                {
                    return CustomIconPath;
                }

                // Cached default icon
                if (!string.IsNullOrEmpty(_cachedDefaultIconPath) && File.Exists(_cachedDefaultIconPath))
                {
                    return _cachedDefaultIconPath;
                }

                // Direct URL (will download)
                return DefaultIconUrl;
            }
        }

        public bool HasStoredExecutable
        {
            get
            {
                if (string.IsNullOrEmpty(FolderName) || GameManager == null)
                    return false;

                try
                {
                    var gamePath = Path.Combine(GameManager.GamesFolder, FolderName);
                    var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                    return File.Exists(selectedExePath);
                }
                catch
                {
                    return false;
                }
            }
        }

        public string DefaultIconUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(CustomDefaultIconUrl))
                {
                    return CustomDefaultIconUrl;
                }

                if (string.IsNullOrEmpty(Repository) || string.IsNullOrEmpty(Branch) || string.IsNullOrEmpty(ImageRes))
                {
                    return "/Assets/DefaultGame.png";
                }

                return $"https://raw.githubusercontent.com/{Repository}/{Branch}/icons/{ImageRes}.png";
            }
        }

        private List<string>? _availableExecutables;
        public List<string>? AvailableExecutables
        {
            get => _availableExecutables;
            set
            {
                if (_availableExecutables != value)
                {
                    _availableExecutables = value;
                    DispatchPropertyChanged();
                }
            }
        }

        private string? _selectedExecutable;
        public string? SelectedExecutable
        {
            get => _selectedExecutable;
            set
            {
                if (_selectedExecutable != value)
                {
                    _selectedExecutable = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public bool HasMultipleExecutables => AvailableExecutables?.Count > 1;

        private List<GitHubAsset>? _availableDownloads;
        public List<GitHubAsset>? AvailableDownloads
        {
            get => _availableDownloads;
            set
            {
                if (_availableDownloads != value)
                {
                    _availableDownloads = value;
                    DispatchPropertyChanged();
                }
            }
        }

        private GitHubAsset? _selectedDownload;
        public GitHubAsset? SelectedDownload
        {
            get => _selectedDownload;
            set
            {
                if (_selectedDownload != value)
                {
                    _selectedDownload = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public bool HasMultipleDownloads => AvailableDownloads?.Count > 1;

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

                // Only create new bitmap if image path changed
                if (_buttonImageCache == null || _lastImagePath != imagePath)
                {
                    _buttonImageCache?.Dispose();
                    _buttonImageCache = new Avalonia.Media.Imaging.Bitmap(
                        Avalonia.Platform.AssetLoader.Open(new Uri(imagePath)));
                    _lastImagePath = imagePath;
                }

                return _buttonImageCache;
            }
        }
        public void Dispose()
        {
            _buttonImageCache?.Dispose();
            _buttonImageCache = null;
        }

        private string? _lastImagePath;

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

        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                if (_downloadProgress != value)
                {
                    _downloadProgress = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(IsDownloading));
                    DispatchPropertyChanged(nameof(ProgressBarColor));
                }
            }
        }

        public bool IsDownloading => Status == GameStatus.Downloading || Status == GameStatus.Installing || Status == GameStatus.Updating;

        public IBrush ProgressBarColor
        {
            get
            {
                if (Status == GameStatus.Updating)
                {
                    // Yellow to Green gradient based on progress
                    var progress = DownloadProgress / 100.0;
                    byte r = (byte)(255 - (255 - 52) * progress);
                    byte g = (byte)(149 + (199 - 149) * progress);
                    byte b = (byte)(0 + (89 - 0) * progress);
                    return new SolidColorBrush(Color.FromRgb(r, g, b));
                }
                else
                {
                    // Blue to Green gradient based on progress
                    var progress = DownloadProgress / 100.0;
                    byte r = (byte)(0 + (52 - 0) * progress);
                    byte g = (byte)(122 + (199 - 122) * progress);
                    byte b = (byte)(255 - (255 - 89) * progress);
                    return new SolidColorBrush(Color.FromRgb(r, g, b));
                }
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
                        Width = 800,
                        Height = 400,
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

        public async Task CheckStatusAsync(HttpClient httpClient, string gamesFolder, bool forceUpdateCheck = false)
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

                bool isInstalled = false;
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
                        isInstalled = true;
                    }
                    else
                    {
                        Status = GameStatus.Installed;
                        InstalledVersion = "Unknown";
                        isInstalled = true;
                    }
                }
                else
                {
                    Status = GameStatus.NotInstalled;
                    InstalledVersion = "";
                }

                // Different update check logic for installed vs not-installed games
                if (forceUpdateCheck)
                {
                    // Force check - always check
                    await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);
                }
                else if (isInstalled)
                {
                    // Installed games: check if needs update (more frequent - every 6 hours by default)
                    if (GitHubApiCache.NeedsUpdateCheck(Repository, isInstalledGame: true))
                    {
                        await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache != null)
                    {
                        // Use cached data
                        LatestVersion = cache.Version;
                        _cachedRelease = cache.CachedRelease;
                    }
                }
                else
                {
                    // Not-installed games: check less frequently (once per day)
                    if (GitHubApiCache.NeedsUpdateCheck(Repository, isInstalledGame: false))
                    {
                        await CheckLatestVersionAsync(httpClient).ConfigureAwait(false);
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache != null)
                    {
                        // Use cached data
                        LatestVersion = cache.Version;
                        _cachedRelease = cache.CachedRelease;
                    }
                }

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

                using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destStream);
                }

                if (File.Exists(destinationPath))
                {
                    var attributes = File.GetAttributes(destinationPath);
                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
                    }
                }

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
                    try
                    {
                        var attributes = File.GetAttributes(iconPath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(iconPath, attributes & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to check/modify file attributes for {iconPath}: {ex.Message}");
                    }

                    CustomIconPath = iconPath;
                    break;
                }
            }
        }

        public void SaveSelectedExecutable(string executablePath, string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName) || string.IsNullOrEmpty(executablePath))
                return;

            try
            {
                var gamePath = Path.Combine(gamesFolder, FolderName);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                File.WriteAllText(selectedExePath, executablePath);
                System.Diagnostics.Debug.WriteLine($"Saved selected executable for {Name}: {executablePath}");
                OnPropertyChanged(nameof(HasStoredExecutable));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save selected executable for {Name}: {ex.Message}");
            }
        }

        public string? LoadSelectedExecutable(string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
                return null;

            try
            {
                var gamePath = Path.Combine(gamesFolder, FolderName);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");

                if (File.Exists(selectedExePath))
                {
                    var savedPath = File.ReadAllText(selectedExePath).Trim();
                    if (File.Exists(savedPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"Loaded selected executable for {Name}: {savedPath}");
                        return savedPath;
                    }
                    else
                    {
                        // File no longer exists, delete the preference
                        File.Delete(selectedExePath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load selected executable for {Name}: {ex.Message}");
            }

            return null;
        }

        public void ClearSelectedExecutable(string gamesFolder)
        {
            if (string.IsNullOrEmpty(FolderName))
                return;

            try
            {
                var gamePath = Path.Combine(gamesFolder, FolderName);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");

                if (File.Exists(selectedExePath))
                {
                    File.Delete(selectedExePath);
                    System.Diagnostics.Debug.WriteLine($"Cleared selected executable for {Name}");
                }

                SelectedExecutable = null;
                OnPropertyChanged(nameof(HasStoredExecutable));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear selected executable for {Name}: {ex.Message}");
            }
        }

        public async Task LoadAndCacheDefaultIconAsync(string cacheDirectory)
        {
            if (string.IsNullOrEmpty(FolderName))
                return;

            try
            {
                var iconsDir = Path.Combine(cacheDirectory, "Icons");
                Directory.CreateDirectory(iconsDir);

                var defaultUrl = DefaultIconUrl;

                // Only cache if it's an actual URL (not a local asset path)
                if (!defaultUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !defaultUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Create a safe filename from the URL
                var urlHash = GetUrlHash(defaultUrl);
                var extension = Path.GetExtension(defaultUrl);

                // If no extension in URL or it's too long, default to .png
                if (string.IsNullOrEmpty(extension) || extension.Length > 5 || extension.Contains('?'))
                    extension = ".png";

                var cachedIconPath = Path.Combine(iconsDir, $"{FolderName}_{urlHash}{extension}");

                // If cached icon exists and is valid, use it
                if (File.Exists(cachedIconPath))
                {
                    try
                    {
                        // Verify the file is valid by checking its size
                        var fileInfo = new FileInfo(cachedIconPath);
                        if (fileInfo.Length > 0)
                        {
                            _cachedDefaultIconPath = cachedIconPath;
                            OnPropertyChanged(nameof(IconUrl));
                            System.Diagnostics.Debug.WriteLine($"Using cached icon for {Name}: {cachedIconPath}");
                            return;
                        }
                    }
                    catch
                    {
                        // If file is corrupted, delete it and re-download
                        try { File.Delete(cachedIconPath); } catch { }
                    }
                }

                // Download icon if not cached
                System.Diagnostics.Debug.WriteLine($"Downloading icon for {Name} from {defaultUrl}");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "N64Recomp-Launcher/1.0");

                var iconData = await httpClient.GetByteArrayAsync(defaultUrl);

                // Save to cache
                await File.WriteAllBytesAsync(cachedIconPath, iconData);
                _cachedDefaultIconPath = cachedIconPath;
                OnPropertyChanged(nameof(IconUrl));
                System.Diagnostics.Debug.WriteLine($"Icon cached for {Name}: {cachedIconPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cache icon for {Name}: {ex.Message}");
                // Fallback to direct URL
            }
        }

        private static string GetUrlHash(string url)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            return Convert.ToHexString(hashBytes).Substring(0, 16).ToLowerInvariant();
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
                            File.SetAttributes(filePath, FileAttributes.Normal);
                        }

                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        File.Delete(filePath);
                        System.Diagnostics.Debug.WriteLine($"Successfully deleted file: {filePath}");
                        return;
                    }
                    else
                    {
                        return;
                    }
                }
                catch (IOException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1}/{maxRetries} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs * (i + 1));

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempt {i + 1}/{maxRetries} - Access denied for {filePath}: {ex.Message}");

                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                    }
                    catch { }

                    System.Threading.Thread.Sleep(delayMs * (i + 1));
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
                // Check if we need to update first
                if (!GitHubApiCache.NeedsUpdateCheck(Repository))
                {
                    // Use cached data without making API call
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var cachedData) && cachedData != null)
                    {
                        LatestVersion = cachedData.Version;
                        _cachedRelease = cachedData.CachedRelease;

                        if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion) &&
                            InstalledVersion != LatestVersion && InstalledVersion != "Unknown")
                        {
                            Status = GameStatus.UpdateAvailable;
                        }
                    }
                    return; // no API call needed
                }

                // Only reach here if update check is needed
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

                        // Update the LastUpdateCheck timestamp even though content hasn't changed
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
                    
                    await DownloadAndInstallAsync(httpClient, gamesFolder, GetLatestRelease(), settings, _status);

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

        private async Task<bool> WillDownloadWindowsVersionBasedOnSelection(HttpClient httpClient, AppSettings settings)
        {
            // This is only called on Linux to check if Wine warning is needed
            // Since we let user choose, we can't know in advance, so we check if Windows assets exist
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            try
            {
                var latestRelease = GetLatestRelease();

                if (latestRelease == null)
                {
                    if (GitHubApiCache.TryGetCachedVersion(Repository, out var cache) && cache?.CachedRelease != null)
                    {
                        latestRelease = cache.CachedRelease;
                    }
                    else
                    {
                        var releaseRequestUrl = $"https://api.github.com/repos/{Repository}/releases";

                        var request = new HttpRequestMessage(HttpMethod.Get, releaseRequestUrl);
                        var token = GetGitHubApiToken();
                        if (!string.IsNullOrEmpty(token))
                        {
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                        }

                        var response = await httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                        var releaseResponse = await response.Content.ReadAsStringAsync();
                        var deserializedReleases = JsonSerializer.Deserialize<IEnumerable<GitHubRelease>>(releaseResponse);

                        if (deserializedReleases == null || !deserializedReleases.Any())
                            return false;

                        latestRelease = deserializedReleases.FirstOrDefault(r => !r.prerelease) ??
                                        deserializedReleases.FirstOrDefault(r => r.prerelease);
                    }
                }

                if (latestRelease?.assets == null)
                    return false;

                string platformIdentifier = GetPlatformIdentifier(settings);

                // Check if there's a Linux version
                var linuxAsset = latestRelease.assets.FirstOrDefault(a =>
                    MatchesPlatform(a.name, platformIdentifier));

                // If no Linux version found, check if Windows version exists
                if (linuxAsset == null)
                {
                    var windowsAsset = latestRelease.assets.FirstOrDefault(a =>
                        a.name.ToLowerInvariant().Contains("windows") ||
                        a.name.ToLowerInvariant().Contains("win64") ||
                        a.name.ToLowerInvariant().Contains("win32") ||
                        a.name.ToLowerInvariant().EndsWith(".exe"));
                    return windowsAsset != null;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> ShowWineNotFoundWarning()
        {
            bool userChoice = false;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = "Wine/Proton Not Found",
                        Width = 500,
                        Height = 220,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
            {
                new TextBlock
                {
                    Text = "This game requires Wine or Proton to run, but neither was detected on your system.\n\n" +
                           "Please install Wine or Steam (which includes Proton) to run Windows games on Linux.\n\n" +
                           "Do you want to download anyway? The game will not launch without Wine/Proton.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 20)
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 10,
                    Children =
                    {
                        new Button { Content = "Download Anyway", Width = 140 },
                        new Button { Content = "Cancel", Width = 100 }
                    }
                }
            }
                        }
                    };

                    var buttonPanel = ((StackPanel)messageBox.Content).Children[1] as StackPanel;
                    var yesButton = buttonPanel.Children[0] as Button;
                    var noButton = buttonPanel.Children[1] as Button;

                    yesButton.Click += (s, e) => { userChoice = true; messageBox.Close(); };
                    noButton.Click += (s, e) => { userChoice = false; messageBox.Close(); };

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });

            return userChoice;
        }

        private static async Task<bool> ShowWineDownloadWarning()
        {
            bool userChoice = false;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = "Wine/Proton Required",
                        Width = 500,
                        Height = 200,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                    {
                        new TextBlock
                        {
                            Text = "This game requires Wine/Proton to run. Wine/Proton was detected on your system and will be used to launch the game.\n\nWant to download?",
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 20)
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Spacing = 10,
                            Children =
                            {
                                new Button { Content = "Yes", Width = 100 },
                                new Button { Content = "No", Width = 100 }
                            }
                        }
                    }
                        }
                    };

                    var buttonPanel = ((StackPanel)messageBox.Content).Children[1] as StackPanel;
                    var yesButton = buttonPanel.Children[0] as Button;
                    var noButton = buttonPanel.Children[1] as Button;

                    yesButton.Click += (s, e) => { userChoice = true; messageBox.Close(); };
                    noButton.Click += (s, e) => { userChoice = false; messageBox.Close(); };

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });

            return userChoice;
        }

        private GitHubRelease? GetLatestRelease()
        {
            return _cachedRelease;
        }

        private async Task DownloadAndInstallAsync(HttpClient httpClient, string gamesFolder, GitHubRelease? latestRelease, AppSettings settings, GameStatus status)
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
                Status = (status == GameStatus.UpdateAvailable) ? GameStatus.Updating : GameStatus.Downloading;
                DownloadProgress = 0;

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
                        DownloadProgress = 5;
                        var releaseRequestUrl = $"https://api.github.com/repos/{Repository}/releases";

                        var request = new HttpRequestMessage(HttpMethod.Get, releaseRequestUrl);
                        var token = GetGitHubApiToken();
                        if (!string.IsNullOrEmpty(token))
                        {
                            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                        }

                        var response = await httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                        var releaseResponse = await response.Content.ReadAsStringAsync();
                        var deserializedReleases = JsonSerializer.Deserialize<IEnumerable<GitHubRelease>>(releaseResponse);

                        if (deserializedReleases == null || !deserializedReleases.Any())
                        {
                            await ShowMessageBoxAsync($"No releases found for {Name}.", "No Releases");
                            Status = GameStatus.NotInstalled;
                            DownloadProgress = 0;
                            return;
                        }

                        latestRelease = deserializedReleases.FirstOrDefault(r => !r.prerelease) ??
                                        deserializedReleases.FirstOrDefault(r => r.prerelease);

                        if (latestRelease == null)
                        {
                            await ShowMessageBoxAsync($"No valid releases found for {Name}.", "No Releases");
                            Status = GameStatus.NotInstalled;
                            DownloadProgress = 0;
                            return;
                        }

                        GitHubApiCache.SetCache(Repository, latestRelease.tag_name, "", latestRelease);
                    }
                }

                DownloadProgress = 10;

                // Check if the installed version is already the latest
                if (File.Exists(versionFile))
                {
                    var existingVersion = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();
                    if (existingVersion == latestRelease.tag_name)
                    {
                        Status = GameStatus.Installed;
                        InstalledVersion = existingVersion;
                        LatestVersion = latestRelease.tag_name;
                        DownloadProgress = 0;
                        return;
                    }
                }

                // Get all available assets
                var availableAssets = latestRelease.assets?.ToList() ?? new List<GitHubAsset>();

                // Exclude flatpak files
                availableAssets = availableAssets
                    .Where(a => !a.name.ToLowerInvariant().Contains("flatpak"))
                    .ToList();

                if (availableAssets.Count == 0)
                {
                    await ShowMessageBoxAsync($"No download files found for {Name}.", "No Assets");
                    Status = GameStatus.NotInstalled;
                    DownloadProgress = 0;
                    return;
                }

                // Store available downloads for potential UI display
                AvailableDownloads = availableAssets;

                GitHubAsset? asset = null;

                // If multiple downloads and no selection made, trigger selection UI
                if (availableAssets.Count > 1 && SelectedDownload == null)
                {
                    // Signal to UI that selection is needed
                    OnPropertyChanged(nameof(HasMultipleDownloads));
                    OnPropertyChanged(nameof(AvailableDownloads));
                    Status = GameStatus.NotInstalled;
                    DownloadProgress = 0;
                    return;
                }

                // Use selected download or default to first one
                asset = SelectedDownload ?? availableAssets[0];

                // Check if Wine/Proton is needed on Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Check if the selected asset is a Windows file
                    var assetNameLower = asset.name.ToLowerInvariant();
                    bool isWindowsFile = assetNameLower.Contains("windows") ||
                                         assetNameLower.Contains("win64") ||
                                         assetNameLower.Contains("win32") ||
                                         assetNameLower.EndsWith(".exe") ||
                                         assetNameLower.Contains("-win.") ||
                                         assetNameLower.Contains("_win.");

                    if (isWindowsFile)
                    {
                        if (!IsWineOrProtonAvailable())
                        {
                            bool shouldContinueAnyway = await ShowWineNotFoundWarning();
                            if (!shouldContinueAnyway)
                            {
                                Status = GameStatus.NotInstalled;
                                DownloadProgress = 0;
                                return;
                            }
                        }
                        else
                        {
                            bool shouldContinue = await ShowWineDownloadWarning();
                            if (!shouldContinue)
                            {
                                Status = GameStatus.NotInstalled;
                                DownloadProgress = 0;
                                return;
                            }
                        }
                    }
                }

                // Download the asset
                var downloadPath = Path.Combine(Path.GetTempPath(), asset.name);

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, asset.browser_download_url))
                    {
                        using (var downloadResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                        {
                            downloadResponse.EnsureSuccessStatusCode();

                            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
                            var canReportProgress = totalBytes > 0;

                            using var contentStream = await downloadResponse.Content.ReadAsStreamAsync();
                            using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int bytesRead;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (canReportProgress)
                                {
                                    // Progress from 10% to 90% during download
                                    var downloadPercent = (double)totalRead / totalBytes;
                                    DownloadProgress = 10 + (downloadPercent * 80);
                                }
                            }
                        }
                    }

                    DownloadProgress = 90;

                    // Install or update the game
                    Status = GameStatus.Installing;
                    DownloadProgress = 95;

                    await InstallOrUpdateGame(downloadPath, gamePath, asset.name, latestRelease.tag_name);

                    DownloadProgress = 100;
                    await Task.Delay(500); // Brief pause to show completion

                    // Update status
                    InstalledVersion = latestRelease.tag_name;
                    LatestVersion = latestRelease.tag_name;
                    Status = GameStatus.Installed;
                    DownloadProgress = 0;
                    SelectedDownload = null;
                    AvailableDownloads = null;
                }
                finally
                {
                    // Clean up download file
                    bool wasSingleExecutable = asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                                asset.name.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase);

                    if (!wasSingleExecutable && File.Exists(downloadPath))
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
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        GameManager.OnPropertyChanged(nameof(GameManager.Games));
                    });
                }
            }
            catch (HttpRequestException ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    // Check if it's a rate limit error
                    if (ex.Message.Contains("403") || ex.Message.ToLower().Contains("rate limit"))
                    {
                        await ShowRateLimitErrorAsync();
                    }
                    else
                    {
                        await ShowMessageBoxAsync($"Network error installing {Name}: {ex.Message}\n\nPlease check your internet connection.", "Network Error");
                    }
                });
                Status = GameStatus.NotInstalled;
                DownloadProgress = 0;
            }
            catch (UnauthorizedAccessException ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Permission error installing {Name}: {ex.Message}\n\nPlease check folder permissions.", "Permission Error");
                });
                Status = GameStatus.NotInstalled;
                DownloadProgress = 0;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error installing {Name}: {ex.Message}", "Installation Error");
                });
                Status = GameStatus.NotInstalled;
                DownloadProgress = 0;
            }
        }

        private static async Task ShowRateLimitErrorAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var hyperlinkText = new TextBlock
                    {
                        Text = "https://github.com/settings/tokens",
                        Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10, 0, 0, 0)
                    };

                    // Add click handler to hyperlink
                    hyperlinkText.PointerPressed += (s, e) =>
                    {
                        try
                        {
                            var url = "https://github.com/settings/tokens";
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                            }
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                            {
                                Process.Start("xdg-open", url);
                            }
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                            {
                                Process.Start("open", url);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to open URL: {ex.Message}");
                        }
                    };

                    var okButton = new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 10, 0, 0),
                        MinWidth = 100
                    };

                    var messageBox = new Window
                    {
                        Title = "Rate Limit Exceeded",
                        Width = 600,
                        Height = 450,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new ScrollViewer
                        {
                            Content = new StackPanel
                            {
                                Margin = new Thickness(20),
                                Spacing = 15,
                                Children =
                        {
                            new TextBlock
                            {
                                Text = "GitHub API rate limit exceeded.",
                                FontWeight = FontWeight.Bold,
                                FontSize = 16,
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = "GitHub limits anonymous requests to 60 per hour. The limit resets one hour after depletion.",
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = "To avoid this, add a GitHub API token in Settings:",
                                FontWeight = FontWeight.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 10, 0, 0)
                            },
                            new TextBlock
                            {
                                Text = "1. Click the link below to create a token:",
                                TextWrapping = TextWrapping.Wrap
                            },
                            hyperlinkText,
                            new TextBlock
                            {
                                Text = "2. Click 'Generate new token (classic)'",
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = "3. Give it a name (no special permissions needed)",
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = "4. Click 'Generate token' at the bottom",
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = "5. Copy the token and paste it in the launcher Settings",
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = "⚠️ Do not share your token with anyone!",
                                Foreground = new SolidColorBrush(Color.FromRgb(255, 149, 0)),
                                FontWeight = FontWeight.Bold,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 10, 0, 0)
                            },
                            okButton
                        }
                            }
                        }
                    };

                    okButton.Click += (s, e) => messageBox.Close();

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });
        }

        public static string? GetPlatformIcon(string assetName)
        {
            var assetNameLower = assetName.ToLowerInvariant();

            // Check for Windows
            if (HasAnyOf(assetNameLower, "windows", "win64", "win32", "win-x64", "win-x86", "-win.", "_win.", ".exe", ".msi") ||
                System.Text.RegularExpressions.Regex.IsMatch(assetNameLower, @"[_-]win[_-]|[_-]win\d|^win[_-]"))
            {
                // Exclude false positives
                if (!HasAnyOf(assetNameLower, "linux", "macos", "darwin", ".deb", ".rpm", ".appimage", ".dmg"))
                {
                    return "avares://N64RecompLauncher/Assets/Icons/platform_win.png";
                }
            }

            // Check for macOS
            if (HasAnyOf(assetNameLower, "macos", "osx", "darwin", ".dmg", ".pkg") ||
                (assetNameLower.Contains("mac") && !assetNameLower.Contains("machin")))
            {
                // Exclude false positives
                if (!HasAnyOf(assetNameLower, "linux", "windows", "win32", "win64", ".exe"))
                {
                    return "avares://N64RecompLauncher/Assets/Icons/platform_mac.png";
                }
            }

            // Check for Linux
            if (HasAnyOf(assetNameLower, "linux", ".appimage", ".deb", ".rpm", "tar.gz", "tar.xz"))
            {
                // Exclude false positives
                if (!HasAnyOf(assetNameLower, "windows", "win32", "win64", "macos", "osx", "darwin", ".exe", ".dmg"))
                {
                    return "avares://N64RecompLauncher/Assets/Icons/platform_lin.png";
                }
            }

            return null; // No platform detected
        }

        public static bool MatchesPlatform(string assetName, string platformIdentifier)
        {
            if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(platformIdentifier))
            {
                System.Diagnostics.Debug.WriteLine("Invalid input: assetName or platformIdentifier is null/empty");
                return false;
            }

            var assetNameLower = assetName.ToLowerInvariant();
            var platformLower = platformIdentifier.ToLowerInvariant();

            System.Diagnostics.Debug.WriteLine($"Checking asset: {assetName}");
            System.Diagnostics.Debug.WriteLine($"Platform identifier: {platformIdentifier}");

            // Windows detection
            if (platformLower.Contains("windows"))
            {
                System.Diagnostics.Debug.WriteLine("Checking Windows patterns...");

                // Exclude Non-Windows indicators
                if (HasAnyOf(assetNameLower, "linux", "macos", "osx", "darwin", "apple", ".deb", ".rpm", ".appimage", ".dmg", ".pkg", "switch"))
                {
                    System.Diagnostics.Debug.WriteLine("Excluded: contains non-Windows platform marker");
                    return false;
                }

                // Positive Windows indicators
                bool isWindows = HasAnyOf(assetNameLower,
                    "windows", "win64", "win32", "win-x64", "win-x86",
                    "-win.", "_win.", ".exe", ".msi", "msvc", "mingw") ||
                    
                    System.Text.RegularExpressions.Regex.IsMatch(assetNameLower, @"[_-]win[_-]|[_-]win\d|^win[_-]");

                System.Diagnostics.Debug.WriteLine($"Windows match result: {isWindows}");
                return isWindows;
            }

            // macOS detection
            if (platformLower.Contains("macos") || platformLower.Contains("mac"))
            {
                System.Diagnostics.Debug.WriteLine("Checking macOS patterns...");

                // Exclude non-macOS
                if (HasAnyOf(assetNameLower, "linux", "windows", "win32", "win64", ".exe", ".msi", "switch"))
                {
                    System.Diagnostics.Debug.WriteLine("Excluded: contains non-macOS platform marker");
                    return false;
                }

                bool isMac = HasAnyOf(assetNameLower, "macos", "osx", "darwin", ".dmg", ".pkg") ||
                             (assetNameLower.Contains("mac") && !assetNameLower.Contains("machin"));

                System.Diagnostics.Debug.WriteLine($"macOS match result: {isMac}");
                return isMac;
            }

            // Linux detection
            if (platformLower.Contains("linux"))
            {
                System.Diagnostics.Debug.WriteLine("Checking Linux patterns...");

                // Exclude non-Linux
                if (HasAnyOf(assetNameLower, "windows", "win32", "win64", "macos", "osx", "darwin", ".exe", ".msi", ".dmg", "switch"))
                {
                    System.Diagnostics.Debug.WriteLine("Excluded: contains non-Linux platform marker");
                    return false;
                }

                bool hasLinux = HasAnyOf(assetNameLower, "linux", ".appimage", ".deb", ".rpm", "tar.gz", "tar.xz");

                if (!hasLinux)
                {
                    System.Diagnostics.Debug.WriteLine("No Linux markers found");
                    return false;
                }

                // ARM64 Linux specific
                if (platformLower.Contains("arm64") || platformLower.Contains("arm") || platformLower.Contains("aarch64"))
                {
                    bool isArm = HasAnyOf(assetNameLower, "arm64", "aarch64", "armv7", "armhf", "arm-");
                    System.Diagnostics.Debug.WriteLine($"Linux ARM64 match result: {isArm}");
                    return isArm;
                }

                // Linux x64 - prioritize x86_64/x64/amd64, exclude i686/i386
                if (!platformLower.Contains("arm"))
                {
                    // Exclude 32-bit builds
                    if (HasAnyOf(assetNameLower, "i686", "i386", "i586", "x86-linux", "-i686-"))
                    {
                        System.Diagnostics.Debug.WriteLine("Excluded: 32-bit Linux build");
                        return false;
                    }

                    // Must have x64 indicators for 64-bit Linux
                    bool isLinuxX64 = HasAnyOf(assetNameLower, "x86_64", "x64", "amd64", "x86-64") &&
                                     !HasAnyOf(assetNameLower, "arm64", "aarch64", "armv7", "armhf", "arm-");

                    System.Diagnostics.Debug.WriteLine($"Linux x64 match result: {isLinuxX64}");
                    return isLinuxX64;
                }
            }

            // Fallback
            System.Diagnostics.Debug.WriteLine("Using fallback substring match");
            bool fallbackMatch = assetNameLower.Contains(platformLower);
            System.Diagnostics.Debug.WriteLine($"Fallback match result: {fallbackMatch}");
            return fallbackMatch;
        }

        private static bool HasAnyOf(string input, params string[] substrings)
        {
            foreach (var substring in substrings)
            {
                if (input.Contains(substring))
                {
                    return true;
                }
            }
            return false;
        }

        static async Task InstallOrUpdateGame(string downloadPath, string gamePath, string assetName, string version)
        {
            Directory.CreateDirectory(gamePath);

            // Handle single executable files (no archive)
            if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                assetName.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase))
            {
                var destPath = Path.Combine(gamePath, assetName);
                File.Move(downloadPath, destPath, true);

                // Make executable on Unix
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
                        var chmodProcess = new ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x \"{destPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var process = Process.Start(chmodProcess);
                        process?.WaitForExit();
                    }
                    catch { }
                }
            }
            else if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Extract directly to game path
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
                            // Move contents to game path
                            MoveDirectoryContents(tempExtractPath, gamePath);
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(tempExtractPath))
                        {
                            try { Directory.Delete(tempExtractPath, true); } catch { }
                        }
                    }
                }
                else // Linux
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
                            // Move contents to game path
                            MoveDirectoryContents(tempExtractPath, gamePath);
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(tempExtractPath))
                        {
                            try { Directory.Delete(tempExtractPath, true); } catch { }
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

        static void MoveDirectoryContents(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                var destFile = Path.Combine(destDir, relative);

                var destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);

                File.Move(file, destFile, true);
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

                if (File.Exists(destFile))
                {
                    Debug.WriteLine($"Skipping duplicate file: {destFile}");
                    continue;
                }

                var destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);
                File.Copy(file, destFile, overwrite: false);
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
            else // Linux
            {
                var topLevelFiles = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);

                // Check for common Linux executable extensions
                if (topLevelFiles.Any(f =>
                {
                    var lower = f.ToLowerInvariant();
                    return lower.EndsWith(".appimage") ||
                           lower.EndsWith(".x86_64") ||
                           lower.EndsWith(".arm64") ||
                           lower.EndsWith(".aarch64");
                }))
                    return true;

                // Check for extensionless executables (common on Linux)
                return topLevelFiles.Any(f =>
                {
                    try
                    {
                        var name = Path.GetFileName(f);
                        // Skip known non-executable files
                        if (name.EndsWith(".txt") || name.EndsWith(".json") ||
                            name.EndsWith(".dll") || name.EndsWith(".so") || name.EndsWith(".a"))
                            return false;

                        return !Path.HasExtension(f) && new FileInfo(f).Length > 1024;
                    }
                    catch { return false; }
                });
            }
        }

        static void TryEnsureExecutableAtRoot(string gamePath)
        {
            if (!Directory.Exists(gamePath))
                return;

            if (HasTopLevelExecutable(gamePath))
                return;

            // Check if there's a single subdirectory with all content
            var topLevelDirs = Directory.GetDirectories(gamePath, "*", SearchOption.TopDirectoryOnly);
            var topLevelFiles = Directory.GetFiles(gamePath, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals("version.txt", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("portable.txt", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("portable_disabled.txt", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("LastPlayed.txt", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).Equals("selected_executable.txt", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If only one subdirectory and no other files, move its contents up
            if (topLevelDirs.Length == 1 && topLevelFiles.Count == 0)
            {
                var singleDir = topLevelDirs[0];

                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.Move(singleDir, tempDir);

                    MoveDirectoryContents(tempDir, gamePath);

                    try { Directory.Delete(tempDir, true); } catch { }

                    Debug.WriteLine($"Moved contents from subdirectory to root: {singleDir}");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to flatten directory structure: {ex.Message}");
                }
            }

            // Otherwise, look for executable and move it to root if needed
            string? candidateFile = null;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                candidateFile = Directory.GetFiles(gamePath, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !IsInRootDirectory(f, gamePath));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var appBundle = Directory.GetDirectories(gamePath, "*.app", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !IsInRootDirectory(f, gamePath));

                if (!string.IsNullOrEmpty(appBundle))
                {
                    try
                    {
                        var destPath = Path.Combine(gamePath, Path.GetFileName(appBundle));
                        Directory.Move(appBundle, destPath);
                        Debug.WriteLine($"Moved .app bundle to root: {appBundle}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to move .app bundle: {ex.Message}");
                    }
                    return;
                }
            }
            else // Linux
            {
                var allFiles = Directory.GetFiles(gamePath, "*", SearchOption.AllDirectories)
                    .Where(f => !IsInRootDirectory(f, gamePath))
                    .ToList();

                candidateFile = allFiles.FirstOrDefault(f =>
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    return name.EndsWith(".x86_64") || name.EndsWith(".appimage") ||
                           name.EndsWith(".arm64") || name.EndsWith(".aarch64");
                });

                // Fallback to .exe for Wine/Proton
                if (candidateFile == null)
                {
                    candidateFile = allFiles.FirstOrDefault(f =>
                        f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                }
            }

            // If we found an executable in a subdirectory, check if we should flatten
            if (!string.IsNullOrEmpty(candidateFile))
            {
                var parentDir = Path.GetDirectoryName(candidateFile);
                if (!string.IsNullOrEmpty(parentDir) && topLevelDirs.Contains(parentDir))
                {
                    try
                    {
                        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                        Directory.Move(parentDir, tempDir);

                        MoveDirectoryContents(tempDir, gamePath);

                        try { Directory.Delete(tempDir, true); } catch { }

                        Debug.WriteLine($"Flattened directory containing executable: {parentDir}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to flatten directory: {ex.Message}");
                    }
                }
            }
        }

        static bool IsInRootDirectory(string path, string rootPath)
        {
            var parentDir = Path.GetDirectoryName(path);
            return parentDir != null &&
                   Path.GetFullPath(parentDir).TrimEnd(Path.DirectorySeparatorChar)
                       .Equals(Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase);
        }

        static void SetAttributesNormal(DirectoryInfo dir)
        {
            try
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    SetAttributesNormal(subDir);
                }

                foreach (var file in dir.GetFiles())
                {
                    file.Attributes = FileAttributes.Normal;
                }

                dir.Attributes = FileAttributes.Normal;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Warning setting file attributes: {ex.Message}");
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
                    if (!string.IsNullOrEmpty(parent) &&
                        !Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
                            .Equals(Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar),
                                    StringComparison.OrdinalIgnoreCase))
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

        public static string GetPlatformIdentifier(AppSettings settings)
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
                        Architecture.X64 => "Linux-X64",
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

                // Find all available executables
                var executables = new List<string>();
                bool needsWine = false;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    executables = Directory.GetFiles(gamePath, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var appBundles = Directory.GetDirectories(gamePath, "*.app", SearchOption.TopDirectoryOnly);
                    executables.AddRange(appBundles);

                    // Also add non-app executables
                    executables.AddRange(Directory.GetFiles(gamePath, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => {
                            try
                            {
                                return !Path.HasExtension(f) && new FileInfo(f).Length > 1024;
                            }
                            catch { return false; }
                        }));
                }
                else // Linux
                {
                    var x86_64Files = Directory.GetFiles(gamePath, "*.x86_64", SearchOption.TopDirectoryOnly);
                    executables.AddRange(x86_64Files);

                    // Check for .appimage files
                    var appImages = Directory.GetFiles(gamePath, "*.appimage", SearchOption.TopDirectoryOnly);
                    executables.AddRange(appImages);

                    // Check for .arm64 and .aarch64 files
                    var arm64Files = Directory.GetFiles(gamePath, "*.arm64", SearchOption.TopDirectoryOnly);
                    executables.AddRange(arm64Files);

                    var aarch64Files = Directory.GetFiles(gamePath, "*.aarch64", SearchOption.TopDirectoryOnly);
                    executables.AddRange(aarch64Files);

                    // Then add other executables
                    executables.AddRange(Directory.GetFiles(gamePath, "*", SearchOption.TopDirectoryOnly)
                        .Where(f =>
                        {
                            var fileName = Path.GetFileName(f).ToLowerInvariant();

                            // Skip if already added
                            if (fileName.EndsWith(".appimage") || fileName.EndsWith(".x86_64") ||
                                fileName.EndsWith(".arm64") || fileName.EndsWith(".aarch64") ||
                                fileName.EndsWith(".txt") || fileName.EndsWith(".dll") ||
                                fileName.EndsWith(".so") || fileName.EndsWith(".json"))
                            {
                                return false;
                            }
                            try
                            {
                                return !Path.HasExtension(f) && new FileInfo(f).Length > 1024;
                            }
                            catch { return false; }
                        }));

                    // Check if only Windows .exe files were found
                    if (executables.Count == 0)
                    {
                        var exeFiles = Directory.GetFiles(gamePath, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exeFiles.Length > 0)
                        {
                            if (!IsWineOrProtonAvailable())
                            {
                                await ShowMessageBoxAsync(
                                    "Only a Windows .exe file was found, which requires Wine or Proton.\n\n" +
                                    "Please install Wine or Steam (which includes Proton) to run this game.",
                                    "Wine/Proton Not Found");
                                return;
                            }

                            // Use Wine/Proton to run the .exe
                            executables.AddRange(exeFiles);
                            needsWine = true;
                        }
                    }
                }

                // Check for launch.bat
                var launchBatPath = Path.Combine(gamePath, "launch.bat");
                if (File.Exists(launchBatPath) && !executables.Contains(launchBatPath))
                {
                    executables.Add(launchBatPath);
                }

                if (executables.Count == 0)
                {
                    await ShowMessageBoxAsync(
                        $"No executable found for {Name} in:\n{gamePath}\n\nThe game may not have installed correctly.",
                        "Executable Not Found");
                    return;
                }

                // Store executables for potential UI display
                AvailableExecutables = executables;

                string? executablePath = null;

                // Try to load previously selected executable
                if (string.IsNullOrEmpty(SelectedExecutable))
                {
                    SelectedExecutable = LoadSelectedExecutable(gamesFolder);
                }

                // If multiple executables and no valid selection, trigger selection UI
                if (executables.Count > 1 && (string.IsNullOrEmpty(SelectedExecutable) || !executables.Contains(SelectedExecutable)))
                {
                    SelectedExecutable = null; // Reset if saved exe no longer exists
                    // Signal to UI that selection is needed
                    OnPropertyChanged(nameof(HasMultipleExecutables));
                    OnPropertyChanged(nameof(AvailableExecutables));
                    return;
                }

                // Use selected executable or default to first one
                executablePath = !string.IsNullOrEmpty(SelectedExecutable) && executables.Contains(SelectedExecutable)
                    ? SelectedExecutable
                    : executables[0];

                // Make executable on Unix systems
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    !executablePath.EndsWith(".app") && !needsWine)
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
                else if (needsWine && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Use Wine/Proton to launch
                    var wineCommand = GetWineCommand();
                    if (wineCommand == null)
                    {
                        await ShowMessageBoxAsync("Wine/Proton was detected earlier but is no longer available.", "Wine/Proton Error");
                        return;
                    }

                    if (wineCommand.Contains("proton"))
                    {
                        // Proton usage
                        startInfo.FileName = wineCommand;
                        startInfo.Arguments = $"run \"{executablePath}\"";
                    }
                    else
                    {
                        // Wine usage
                        startInfo.FileName = wineCommand;
                        startInfo.Arguments = $"\"{executablePath}\"";
                    }

                    startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? gamePath;
                    startInfo.UseShellExecute = false;
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
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        GameManager.OnPropertyChanged(nameof(GameManager.Games));
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

        private static bool IsWineOrProtonAvailable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            if (IsCommandAvailable("wine") || IsCommandAvailable("wine64"))
                return true;

            // Check for Proton
            var steamPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam/steam/steamapps/common"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Steam/steamapps/common"),
            };

            foreach (var steamPath in steamPaths)
            {
                if (Directory.Exists(steamPath))
                {
                    var protonDirs = Directory.GetDirectories(steamPath, "Proton*", SearchOption.TopDirectoryOnly);
                    foreach (var protonDir in protonDirs)
                    {
                        var protonExe = Path.Combine(protonDir, "proton");
                        if (File.Exists(protonExe))
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool IsCommandAvailable(string command)
        {
            try
            {
                var process = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(process);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch { }

            return false;
        }

        private static string? GetWineCommand()
        {
            if (IsCommandAvailable("wine64"))
                return "wine64";
            if (IsCommandAvailable("wine"))
                return "wine";

            // Check for Proton
            var steamPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam/steam/steamapps/common"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Steam/steamapps/common"),
            };

            foreach (var steamPath in steamPaths)
            {
                if (Directory.Exists(steamPath))
                {
                    // Get latest Proton version
                    var protonDirs = Directory.GetDirectories(steamPath, "Proton*", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(d => d)
                        .ToList();

                    foreach (var protonDir in protonDirs)
                    {
                        var protonExe = Path.Combine(protonDir, "proton");
                        if (File.Exists(protonExe))
                            return protonExe;
                    }
                }
            }

            return null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}