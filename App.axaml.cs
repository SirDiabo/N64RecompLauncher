using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace N64RecompLauncher;

public class App : Application, INotifyPropertyChanged
{

    private string _currentVersionString;

    public string currentVersionString
    {
        get => _currentVersionString;
        set
        {
            if (_currentVersionString != value)
            {
                _currentVersionString = value;
                OnPropertyChanged();
            }
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private class GitHubAsset
    {
        public string name { get; set; }
        public string browser_download_url { get; set; }
    }

    private class GitHubRelease
    {
        public string tag_name { get; set; }
        public GitHubAsset[] assets { get; set; }
    }

    private class UpdateCheckInfo
    {
        public DateTime LastCheckTime { get; set; }
        public string LastKnownVersion { get; set; }
        public string CurrentVersion { get; set; }
        public string ETag { get; set; }
        public bool UpdateAvailable { get; set; }
    }

    private class ProgressWindow : Window
    {
        private readonly TextBlock _statusText;
        private readonly ProgressBar _progressBar;
        private readonly TextBlock _percentText;

        public ProgressWindow()
        {
            Title = "Updating Launcher";
            Width = 450;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;

            var panel = new StackPanel
            {
                Margin = new Thickness(30),
                Spacing = 15
            };

            _statusText = new TextBlock
            {
                Text = "Preparing download...",
                FontSize = 14,
                Foreground = new SolidColorBrush(Colors.White),
                TextAlignment = TextAlignment.Center
            };

            _progressBar = new ProgressBar
            {
                Height = 24,
                Minimum = 0,
                Maximum = 100,
                Value = 0
            };

            _percentText = new TextBlock
            {
                Text = "0%",
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.LightGray),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, -5, 0, 0)
            };

            panel.Children.Add(_statusText);
            panel.Children.Add(_progressBar);
            panel.Children.Add(_percentText);

            Content = panel;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1a));
        }

        public void UpdateProgress(double percentage, string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _progressBar.Value = percentage;
                _percentText.Text = $"{percentage:F1}%";
                _statusText.Text = status;
            });
        }
    }

    private static bool _hasCheckedForAppUpdates = false;
    private static readonly object _updateLock = new object();
    private const string Repository = "SirDiabo/N64RecompLauncher";
    private const string VersionFileName = "version.txt";
    private const string UpdateCheckFileName = "update_check.json";

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = this
            };
        }

        base.OnFrameworkInitializationCompleted();

        lock (_updateLock)
        {
            if (!_hasCheckedForAppUpdates)
            {
                _hasCheckedForAppUpdates = true;
                Task.Run(async () => await CheckForUpdatesAndApplyAsync(isManualCheck: false));
            }
        }
    }

    public async Task CheckForAppUpdatesManually()
    {
        await CheckForUpdatesAndApplyAsync(isManualCheck: true);
    }

    private async Task CheckForUpdatesAndApplyAsync(bool isManualCheck = false)
    {
        string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);

        UpdateCheckInfo updateCheckInfo = await LoadUpdateCheckInfo(updateCheckFilePath);
        string currentVersionString = updateCheckInfo.CurrentVersion;

        // get it from version.txt if it exists
        if (string.IsNullOrEmpty(currentVersionString))
        {
            string versionFilePath = Path.Combine(currentAppDirectory, VersionFileName);
            if (File.Exists(versionFilePath))
            {
                try
                {
                    currentVersionString = (await File.ReadAllTextAsync(versionFilePath).ConfigureAwait(false))?.Trim() ?? "0.0";
                }
                catch
                {
                    currentVersionString = "0.0";
                }
            }
            else
            {
                currentVersionString = "0.0";
            }

            // Store it in update check info
            updateCheckInfo.CurrentVersion = currentVersionString;
            await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);
        }

        // Skip check if not manual and recently checked
        if (!isManualCheck && ShouldSkipUpdateCheck(updateCheckInfo, currentVersionString))
        {
            Trace.WriteLine($"Skipping app update check - last checked {updateCheckInfo.LastCheckTime}, current version {currentVersionString}");

            if (updateCheckInfo.UpdateAvailable &&
                !string.IsNullOrEmpty(updateCheckInfo.LastKnownVersion) &&
                IsNewerVersion(updateCheckInfo.LastKnownVersion, currentVersionString))
            {
                Trace.WriteLine($"Cached app update available: {updateCheckInfo.LastKnownVersion}");

                // Prompt user about available update
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var result = await ShowMessageBoxWithChoiceAsync(
                        $"Launcher update {updateCheckInfo.LastKnownVersion} is available!\n\nWould you like to update now?",
                        "Update Available");

                    if (result)
                    {
                        using (var httpClient = new HttpClient())
                        {
                            httpClient.Timeout = DownloadTimeout;
                            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("N64RecompLauncher-Updater");

                            var settings = AppSettings.Load();
                            if (!string.IsNullOrEmpty(settings?.GitHubApiToken))
                            {
                                httpClient.DefaultRequestHeaders.Authorization =
                                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.GitHubApiToken);
                            }

                            try
                            {
                                string apiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
                                var response = await httpClient.GetAsync(apiUrl);
                                response.EnsureSuccessStatusCode();

                                string releaseResponse = await response.Content.ReadAsStringAsync();
                                GitHubRelease? latestRelease = JsonSerializer.Deserialize<GitHubRelease>(releaseResponse);

                                if (latestRelease != null)
                                {
                                    await DownloadAndApplyUpdate(latestRelease, AppDomain.CurrentDomain.BaseDirectory, updateCheckInfo);
                                }
                            }
                            catch (Exception ex)
                            {
                                await ShowMessageBoxAsync($"Failed to download update: {ex.Message}", "Update Error");
                            }
                        }
                    }
                });
            }

            return;
        }

        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = DownloadTimeout;
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("N64RecompLauncher-Updater");

            var settings = AppSettings.Load();
            if (!string.IsNullOrEmpty(settings?.GitHubApiToken))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.GitHubApiToken);
            }

            if (!string.IsNullOrEmpty(updateCheckInfo.ETag))
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", updateCheckInfo.ETag);
            }

            try
            {
                string apiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";
                var response = await httpClient.GetAsync(apiUrl);

                updateCheckInfo.LastCheckTime = DateTime.UtcNow;
                updateCheckInfo.CurrentVersion = currentVersionString;

                if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                {
                    Trace.WriteLine("No app updates available (304 Not Modified)");
                    updateCheckInfo.UpdateAvailable = false;
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    if (isManualCheck)
                    {
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await ShowMessageBoxAsync("Launcher is up to date!", "No Updates");
                        });
                    }
                    return;
                }

                response.EnsureSuccessStatusCode();

                if (response.Headers.ETag != null)
                {
                    updateCheckInfo.ETag = response.Headers.ETag.Tag;
                }

                string releaseResponse = await response.Content.ReadAsStringAsync();
                GitHubRelease? latestRelease = JsonSerializer.Deserialize<GitHubRelease>(releaseResponse);

                if (latestRelease == null || string.IsNullOrWhiteSpace(latestRelease.tag_name))
                {
                    Trace.WriteLine("No valid latest release information found on GitHub.");
                    updateCheckInfo.UpdateAvailable = false;
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    if (isManualCheck)
                    {
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await ShowMessageBoxAsync("Could not find launcher update information.", "No Updates");
                        });
                    }
                    return;
                }

                updateCheckInfo.LastKnownVersion = latestRelease.tag_name;

                if (currentVersionString == "0" || currentVersionString == "0.0" || currentVersionString == "v0.0")
                {
                    Trace.WriteLine($"Current version is '{currentVersionString}' - forcing update to {latestRelease.tag_name}");
                    updateCheckInfo.UpdateAvailable = true;
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    if (isManualCheck)
                    {
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            var result = await ShowMessageBoxWithChoiceAsync(
                                $"Launcher update {latestRelease.tag_name} is available!\n\nWould you like to update now?",
                                "Update Available");

                            if (result)
                            {
                                await DownloadAndApplyUpdate(latestRelease, currentAppDirectory, updateCheckInfo);
                            }
                        });
                    }
                    return;
                }

                if (!IsNewerVersion(latestRelease.tag_name, currentVersionString))
                {
                    Trace.WriteLine($"Current launcher version {currentVersionString} is up to date or newer than {latestRelease.tag_name}. No update needed.");
                    updateCheckInfo.UpdateAvailable = false;
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    if (isManualCheck)
                    {
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await ShowMessageBoxAsync($"Launcher is up to date! (v{currentVersionString})", "No Updates");
                        });
                    }
                    return;
                }

                Trace.WriteLine($"Newer launcher version {latestRelease.tag_name} available. Current version is {currentVersionString}.");
                updateCheckInfo.UpdateAvailable = true;
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);
                if (!isManualCheck)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var result = await ShowMessageBoxWithChoiceAsync(
                            $"Launcher update {latestRelease.tag_name} is available!\n\nWould you like to update now?",
                            "Update Available");

                        if (result)
                        {
                            await DownloadAndApplyUpdate(latestRelease, currentAppDirectory, updateCheckInfo);
                        }
                    });
                }

                if (isManualCheck)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var result = await ShowMessageBoxWithChoiceAsync(
                            $"Launcher update {latestRelease.tag_name} is available!\n\nWould you like to update now?",
                            "Update Available");

                        if (result)
                        {
                            await DownloadAndApplyUpdate(latestRelease, currentAppDirectory, updateCheckInfo);
                        }
                    });
                }
            }
            catch (HttpRequestException httpEx)
            {
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                if (isManualCheck)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync($"Could not check for launcher updates (Network Error): {httpEx.Message}",
                            "Update Check Failed");
                    });
                }
            }
            catch (Exception ex)
            {
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                if (isManualCheck)
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync($"An error occurred during launcher update check: {ex.Message}",
                            "Update Check Failed");
                    });
                }
            }
        }
    }

    private async Task<bool> ShowMessageBoxWithChoiceAsync(string message, string title)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            bool result = false;
            var messageBox = new Window
            {
                Title = title,
                Width = 450,
                Height = 170,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 20)
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new Button
                            {
                                Content = "Yes",
                                Margin = new Thickness(0, 0, 10, 0),
                                MinWidth = 80
                            },
                            new Button
                            {
                                Content = "No",
                                MinWidth = 80
                            }
                        }
                    }
                }
                }
            };

            var buttonPanel = ((StackPanel)messageBox.Content).Children[1] as StackPanel;
            var yesButton = buttonPanel.Children[0] as Button;
            var noButton = buttonPanel.Children[1] as Button;

            yesButton.Click += (s, e) =>
            {
                result = true;
                messageBox.Close();
            };

            noButton.Click += (s, e) =>
            {
                result = false;
                messageBox.Close();
            };

            await messageBox.ShowDialog(desktop.MainWindow);
            return result;
        }
        return false;
    }

    private async Task ShowMessageBoxAsync(string message, string title)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            var messageBox = new Window
            {
                Title = title,
                Width = 400,
                Height = 150,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
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
    }

    private async Task<UpdateCheckInfo> LoadUpdateCheckInfo(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new UpdateCheckInfo
            {
                LastCheckTime = DateTime.MinValue,
                LastKnownVersion = string.Empty,
                CurrentVersion = string.Empty,
                ETag = string.Empty,
                UpdateAvailable = false
            };
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath);
            var info = JsonSerializer.Deserialize<UpdateCheckInfo>(json) ?? new UpdateCheckInfo();

            if (string.IsNullOrEmpty(info.CurrentVersion))
                info.CurrentVersion = string.Empty;

            return info;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error loading update check info: {ex.Message}");
            return new UpdateCheckInfo
            {
                LastCheckTime = DateTime.MinValue,
                LastKnownVersion = string.Empty,
                CurrentVersion = string.Empty,
                ETag = string.Empty,
                UpdateAvailable = false
            };
        }
    }

    private async Task SaveUpdateCheckInfo(string filePath, UpdateCheckInfo info)
    {
        try
        {
            string json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error saving update check info: {ex.Message}");
        }
    }

    private bool ShouldSkipUpdateCheck(UpdateCheckInfo info, string currentVersion)
    {
        if (info.LastCheckTime == DateTime.MinValue)
            return false;

        if (DateTime.UtcNow - info.LastCheckTime < UpdateCheckInterval)
            return true;

        if (!string.IsNullOrEmpty(info.CurrentVersion) &&
            !info.CurrentVersion.Equals(currentVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(info.LastKnownVersion) &&
            !string.IsNullOrEmpty(currentVersion) &&
            !IsNewerVersion(info.LastKnownVersion, currentVersion))
        {
            return true;
        }

        return false;
    }

    private bool IsNewerVersion(string latestVersion, string currentVersion)
    {
        try
        {
            string normalizedLatest = NormalizeVersionString(latestVersion);
            string normalizedCurrent = NormalizeVersionString(currentVersion);

            Trace.WriteLine($"Comparing versions - Latest: '{latestVersion}' ({normalizedLatest}) vs Current: '{currentVersion}' ({normalizedCurrent})");

            Version current = new Version(normalizedCurrent);
            Version latest = new Version(normalizedLatest);

            bool isNewer = latest.CompareTo(current) > 0;
            Trace.WriteLine($"IsNewerVersion result: {isNewer}");

            return isNewer;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error comparing versions '{latestVersion}' vs '{currentVersion}': {ex.Message}");

            bool shouldUpdate = !latestVersion.TrimStart('v', 'V').Equals(currentVersion.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
            Trace.WriteLine($"Version parsing failed, using string comparison: {shouldUpdate}");
            return shouldUpdate;
        }
    }

    private string NormalizeVersionString(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0.0";

        version = version.TrimStart('v', 'V').Trim();

        if (version == "0")
            return "0.0.0.0";

        var parts = version.Split('.');
        var validParts = new List<string>();

        foreach (var part in parts)
        {
            if (int.TryParse(part.Trim(), out int number))
            {
                validParts.Add(number.ToString());
            }
        }

        while (validParts.Count < 2)
        {
            validParts.Add("0");
        }

        if (validParts.Count == 2)
        {
            return $"{validParts[0]}.{validParts[1]}";
        }
        else if (validParts.Count == 3)
        {
            return $"{validParts[0]}.{validParts[1]}.{validParts[2]}";
        }
        else
        {
            return $"{validParts[0]}.{validParts[1]}.{validParts[2]}.{validParts[3]}";
        }
    }

    private async Task DownloadAndApplyUpdate(GitHubRelease latestRelease, string currentAppDirectory, UpdateCheckInfo updateCheckInfo)
    {
        string platformIdentifier = GetPlatformIdentifier();
        var asset = latestRelease.assets.FirstOrDefault(a =>
            a.name.Contains(platformIdentifier, StringComparison.OrdinalIgnoreCase) &&
            (a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        );

        if (asset == null)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await ShowMessageBoxAsync($"No downloadable update found for your platform ({platformIdentifier}).",
                    "Update Error");
            });
            return;
        }

        DriveInfo? drive = null;
        string? rootPath = Path.GetPathRoot(currentAppDirectory);
        if (!string.IsNullOrEmpty(rootPath))
        {
            drive = new DriveInfo(rootPath);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await ShowMessageBoxAsync("Could not determine the root drive for update. Update aborted.", "Update Error");
            });
            return;
        }

        ProgressWindow? progressWindow = null;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                progressWindow = new ProgressWindow();
                _ = progressWindow.ShowDialog(desktop.MainWindow);
            }
        });

        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = DownloadTimeout;

            string tempDownloadPath = Path.Combine(Path.GetTempPath(), asset.name);
            try
            {
                progressWindow?.UpdateProgress(0, "Downloading update...");

                using (var downloadResponse = await httpClient.GetAsync(asset.browser_download_url, HttpCompletionOption.ResponseHeadersRead))
                {
                    downloadResponse.EnsureSuccessStatusCode();

                    var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
                    var canReportProgress = totalBytes > 0;

                    using var contentStream = await downloadResponse.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if (canReportProgress)
                        {
                            var percentage = (double)totalRead / totalBytes * 100;
                            progressWindow?.UpdateProgress(percentage, $"Downloading update... ({totalRead / 1024 / 1024:F1} MB / {totalBytes / 1024 / 1024:F1} MB)");
                        }
                    }
                }

                progressWindow?.UpdateProgress(100, "Download complete. Extracting...");
                await Task.Delay(500); // Brief pause so user can see 100%

                string tempUpdateFolder = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_temp_update");
                if (Directory.Exists(tempUpdateFolder))
                {
                    Directory.Delete(tempUpdateFolder, true);
                }
                Directory.CreateDirectory(tempUpdateFolder);

                try
                {
                    if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        ZipFile.ExtractToDirectory(tempDownloadPath, tempUpdateFolder, true);

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                        {
                            var appBundle = Directory.GetDirectories(tempUpdateFolder, "*.app", SearchOption.AllDirectories)
                                .FirstOrDefault();

                            if (!string.IsNullOrEmpty(appBundle))
                            {
                                var appName = Path.GetFileName(appBundle);
                                var newAppPath = Path.Combine(tempUpdateFolder, appName);
                                if (appBundle != newAppPath)
                                {
                                    Directory.Move(appBundle, newAppPath);
                                }
                            }
                        }
                    }
                    else if (asset.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExtractTarGzAsync(tempDownloadPath, tempUpdateFolder);
                    }
                    else
                    {
                        progressWindow?.UpdateProgress(0, "Error: Unsupported archive format");
                        await Task.Delay(2000);
                        await Dispatcher.UIThread.InvokeAsync(() => progressWindow?.Close());

                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await ShowMessageBoxAsync($"Unsupported archive format: {asset.name}",
                                "Update Error");
                        });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    progressWindow?.UpdateProgress(0, "Error during extraction");
                    await Task.Delay(2000);
                    await Dispatcher.UIThread.InvokeAsync(() => progressWindow?.Close());

                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync($"Failed to extract update archive: {ex.Message}",
                            "Update Error");
                    });
                    return;
                }

                progressWindow?.UpdateProgress(100, "Validating update...");

                if (!ValidateUpdateFiles(tempUpdateFolder))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => progressWindow?.Close());

                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync("Downloaded update appears to be corrupted or incomplete.",
                            "Update Error");
                    });
                    return;
                }

                progressWindow?.UpdateProgress(100, "Preparing to install...");
                await Task.Delay(500);

                await Dispatcher.UIThread.InvokeAsync(() => progressWindow?.Close());

                await CreateAndRunUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, updateCheckInfo);
            }
            catch (TaskCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => progressWindow?.Close());

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync("Update download timed out. Please check your internet connection.",
                        "Update Error");
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => progressWindow?.Close());

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error downloading update: {ex.Message}",
                        "Update Error");
                });
            }
        }
    }

    private async Task ExtractTarGzAsync(string tarGzPath, string extractPath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var process = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xzf \"{tarGzPath}\" -C \"{extractPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var proc = Process.Start(process);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode != 0)
                    {
                        var error = await proc.StandardError.ReadToEndAsync();
                        throw new InvalidOperationException($"tar extraction failed: {error}");
                    }
                }
                else
                {
                    throw new InvalidOperationException("Failed to start tar process");
                }
            }
            else
            {
                throw new NotSupportedException("tar.gz extraction not supported on this platform");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract tar.gz file: {ex.Message}", ex);
        }
    }

    private bool ValidateUpdateFiles(string updateDirectory)
    {
        try
        {
            string mainExecutable;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                mainExecutable = Path.Combine(updateDirectory, "N64RecompLauncher.exe");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var appBundle = Directory.GetDirectories(updateDirectory, "*.app", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(appBundle))
                {
                    mainExecutable = appBundle;
                }
                else
                {
                    mainExecutable = Path.Combine(updateDirectory, "N64RecompLauncher");
                }
            }
            else
            {
                mainExecutable = Path.Combine(updateDirectory, "N64RecompLauncher");
            }

            if (!File.Exists(mainExecutable) && !Directory.Exists(mainExecutable))
            {
                Trace.WriteLine($"Main executable not found in update package: {mainExecutable}");
                return false;
            }

            if (File.Exists(mainExecutable))
            {
                FileInfo exeInfo = new FileInfo(mainExecutable);
                if (exeInfo.Length < 1024)
                {
                    Trace.WriteLine($"Main executable too small: {exeInfo.Length} bytes");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error validating update files: {ex.Message}");
            return false;
        }
    }

    private async Task CreateAndRunUpdaterScript(GitHubRelease latestRelease, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory, UpdateCheckInfo updateCheckInfo)
    {
        string applicationExecutable = Process.GetCurrentProcess().MainModule.FileName;
        string backupDir = Path.Combine(currentAppDirectory, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await CreateWindowsUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, updateCheckInfo, applicationExecutable, backupDir);
        }
        else
        {
            await CreateUnixUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, updateCheckInfo, applicationExecutable, backupDir);
        }
    }

    private async Task CreateWindowsUpdaterScript(GitHubRelease latestRelease, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory, UpdateCheckInfo updateCheckInfo, string applicationExecutable, string backupDir)
    {
        string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.cmd");
        string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);

        string scriptContent = $@"@echo off
echo N64RecompLauncher Updater - Version {latestRelease.tag_name}
echo.

echo Waiting for N64RecompLauncher to close...
:wait_loop
tasklist /FI ""IMAGENAME eq {Path.GetFileName(applicationExecutable)}"" 2>NUL | find /I /N ""{Path.GetFileName(applicationExecutable)}"">NUL
if ""%ERRORLEVEL%""==""0"" (
    timeout /T 1 >NUL
    goto wait_loop
)

echo Creating backup...
set ""appDir={currentAppDirectory}""
set ""backupDir={backupDir}""
set ""updateDir={tempUpdateFolder}""

if not exist ""%backupDir%"" mkdir ""%backupDir%""

echo Backing up current version...
for %%i in (""%appDir%\*.exe"" ""%appDir%\*.dll"" ""%appDir%\*.config"" ""%appDir%\*.json"") do (
    if exist ""%%i"" (
        copy ""%%i"" ""%backupDir%\"" >nul 2>&1
    )
)

echo Applying update...
xcopy ""%updateDir%\*"" ""%appDir%"" /S /E /Y /I >nul 2>&1
if errorlevel 1 (
    echo Update failed! Restoring backup...
    xcopy ""%backupDir%\*"" ""%appDir%"" /S /E /Y /I >nul 2>&1
    echo Backup restored. Update failed.
    pause
    goto cleanup
)

echo Updating version info...
echo {{""CurrentVersion"":""{latestRelease.tag_name}"",""LastCheckTime"":""{DateTime.UtcNow:o}"",""LastKnownVersion"":""{latestRelease.tag_name}"",""ETag"":"""",""UpdateAvailable"":false}} > ""{updateCheckFilePath}""

echo Update completed successfully!
echo Deleting backup...
rmdir /S /Q ""%backupDir%"" >nul 2>&1

echo Restarting N64RecompLauncher...
start """" ""{applicationExecutable}""

:cleanup
echo Cleaning up temporary files...
if exist ""{tempDownloadPath}"" del ""{tempDownloadPath}"" >nul 2>&1
if exist ""%updateDir%"" rmdir /S /Q ""%updateDir%"" >nul 2>&1

del ""%~f0""
";

        await File.WriteAllTextAsync(updaterScriptPath, scriptContent);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C \"\"{updaterScriptPath}\"\"",
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false,
            UseShellExecute = true
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private async Task CreateUnixUpdaterScript(GitHubRelease latestRelease, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory, UpdateCheckInfo updateCheckInfo, string applicationExecutable, string backupDir)
    {
        string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.sh");
        string executableName = Path.GetFileName(applicationExecutable);
        string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);

        bool isMacAppBundle = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                             Directory.GetDirectories(tempUpdateFolder, "*.app", SearchOption.TopDirectoryOnly).Any();

        string scriptContent = $@"#!/bin/bash
echo ""N64RecompLauncher Updater - Version {latestRelease.tag_name}""
echo

echo ""Waiting for N64RecompLauncher to close...""
while pgrep -x ""{executableName}"" > /dev/null; do
    sleep 1
done

echo ""Creating backup...""
appDir=""{currentAppDirectory}""
backupDir=""{backupDir}""
updateDir=""{tempUpdateFolder}""

mkdir -p ""$backupDir""

echo ""Backing up current version...""
if [ -d ""$appDir"" ]; then
    cp -r ""$appDir""/* ""$backupDir""/ 2>/dev/null || true
fi

echo ""Applying update...""
if cp -r ""$updateDir""/* ""$appDir""/ 2>/dev/null; then
    echo ""Update applied successfully""
else
    echo ""Update failed! Restoring backup...""
    cp -r ""$backupDir""/* ""$appDir""/ 2>/dev/null || true
    echo ""Backup restored. Update failed.""
    read -p ""Press Enter to continue...""
    exit 1
fi

echo ""Updating version info...""
cat > ""{updateCheckFilePath}"" << 'EOF'
{{""CurrentVersion"":""{latestRelease.tag_name}"",""LastCheckTime"":""{DateTime.UtcNow:o}"",""LastKnownVersion"":""{latestRelease.tag_name}"",""ETag"":"""",""UpdateAvailable"":false}}
EOF

echo ""Update completed successfully!""
echo ""Deleting backup...""
rm -rf ""$backupDir"" 2>/dev/null || true

if [ ""{RuntimeInformation.IsOSPlatform(OSPlatform.OSX).ToString().ToLower()}"" = ""true"" ]; then
    appBundle=$(find ""$appDir"" -maxdepth 1 -name ""*.app"" -type d | head -n 1)
    if [ -n ""$appBundle"" ]; then
        echo ""Found .app bundle: $appBundle""
    elif [ -f ""$appDir/N64RecompLauncher"" ]; then
        chmod +x ""$appDir/N64RecompLauncher""
    fi
else
    if [ -f ""$appDir/N64RecompLauncher"" ]; then
        chmod +x ""$appDir/N64RecompLauncher""
    fi
fi

echo ""Restarting N64RecompLauncher...""

if [ ""{RuntimeInformation.IsOSPlatform(OSPlatform.OSX).ToString().ToLower()}"" = ""true"" ]; then
    appBundle=$(find ""$appDir"" -maxdepth 1 -name ""*.app"" -type d | head -n 1)
    if [ -n ""$appBundle"" ]; then
        echo ""Starting .app bundle: $appBundle""
        nohup open ""$appBundle"" > /dev/null 2>&1 &
    elif [ -f ""$appDir/N64RecompLauncher"" ]; then
        cd ""$appDir""
        nohup ""./N64RecompLauncher"" > /dev/null 2>&1 &
    fi
else
    if [ -f ""$appDir/N64RecompLauncher"" ]; then
        cd ""$appDir""
        nohup ""./N64RecompLauncher"" > /dev/null 2>&1 &
    fi
fi

echo ""Cleaning up temporary files...""
rm -f ""{tempDownloadPath}"" 2>/dev/null || true
rm -rf ""$updateDir"" 2>/dev/null || true

rm -- ""$0""
";

        scriptContent = scriptContent.Replace("\r\n", "\n").Replace("\r", "\n");

        await File.WriteAllTextAsync(updaterScriptPath, scriptContent, new UTF8Encoding(false));

        try
        {
            var chmodProcess = new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{updaterScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(chmodProcess))
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    Trace.WriteLine($"chmod failed for updater script: {error}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to make updater script executable: {ex.Message}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"\"{updaterScriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
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
                Architecture.Arm64 => "Linux-ARM64",
                Architecture.X64 => Environment.GetEnvironmentVariable("FLATPAK_ID") != null
                    ? "Linux-Flatpak-X64"
                    : "Linux-X64",
                _ => "Linux-X64"
            };
        }

        throw new PlatformNotSupportedException("Unsupported operating system");
    }
}