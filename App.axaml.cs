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

    private const string Repository = "SirDiabo/N64RecompLauncher";
    private const string VersionFileName = "version.txt";
    private const string UpdateCheckFileName = "update_check.json";

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        Task.Run(async () =>
        {
            await CheckForUpdatesAndApplyAsync();
        });
    }

    private async Task CheckForUpdatesAndApplyAsync()
    {
        string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string versionFilePath = Path.Combine(currentAppDirectory, VersionFileName);
        string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);
        string currentVersionString = "0.0";

        if (File.Exists(versionFilePath))
        {
            try
            {
                currentVersionString = (await File.ReadAllTextAsync(versionFilePath).ConfigureAwait(false)).Trim();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error reading current version from {VersionFileName}: {ex.Message}");
            }
        }

        UpdateCheckInfo updateCheckInfo = await LoadUpdateCheckInfo(updateCheckFilePath);

        if (ShouldSkipUpdateCheck(updateCheckInfo, currentVersionString))
        {
            Trace.WriteLine($"Skipping update check - last checked {updateCheckInfo.LastCheckTime}, current version {currentVersionString}");

            if (updateCheckInfo.UpdateAvailable &&
                !string.IsNullOrEmpty(updateCheckInfo.LastKnownVersion) &&
                IsNewerVersion(updateCheckInfo.LastKnownVersion, currentVersionString))
            {
                Trace.WriteLine($"Cached update available: {updateCheckInfo.LastKnownVersion}");
                var cachedRelease = new GitHubRelease
                {
                    tag_name = updateCheckInfo.LastKnownVersion,
                    assets = new GitHubAsset[0]
                };

                Trace.WriteLine($"Update {updateCheckInfo.LastKnownVersion} is available (cached info)");
            }

            return;
        }

        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = DownloadTimeout;
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("N64RecompLauncher-Updater");

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
                    Trace.WriteLine("No updates available (304 Not Modified)");
                    updateCheckInfo.UpdateAvailable = false;
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);
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
                    return;
                }

                updateCheckInfo.LastKnownVersion = latestRelease.tag_name;

                if (!IsNewerVersion(latestRelease.tag_name, currentVersionString))
                {
                    Trace.WriteLine($"Current version {currentVersionString} is up to date or newer than {latestRelease.tag_name}. No update needed.");
                    updateCheckInfo.UpdateAvailable = false;
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);
                    return;
                }

                Trace.WriteLine($"Newer version {latestRelease.tag_name} available. Current version is {currentVersionString}.");
                updateCheckInfo.UpdateAvailable = true;
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                await DownloadAndApplyUpdate(latestRelease, currentAppDirectory, versionFilePath);
            }
            catch (HttpRequestException httpEx)
            {
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Could not check for updates (Network Error): {httpEx.Message}",
                        "Update Check Failed");
                });
            }
            catch (JsonException jsonEx)
            {
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Could not parse GitHub release information: {jsonEx.Message}",
                        "Update Check Failed");
                });
            }
            catch (TaskCanceledException)
            {
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync("Update check timed out. Please check your internet connection.",
                        "Update Check Failed");
                });
            }
            catch (Exception ex)
            {
                await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"An error occurred during update: {ex.Message}",
                        "Update Failed");
                });
            }
        }
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
            Version current = new Version(currentVersion.TrimStart('v'));
            Version latest = new Version(latestVersion.TrimStart('v'));
            return latest.CompareTo(current) > 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error comparing versions '{latestVersion}' vs '{currentVersion}': {ex.Message}");
            return false;
        }
    }

    private async Task DownloadAndApplyUpdate(GitHubRelease latestRelease, string currentAppDirectory, string versionFilePath)
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

        if (drive.AvailableFreeSpace < 500 * 1024 * 1024)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await ShowMessageBoxAsync($"Insufficient disk space for update. At least 500MB required.",
                    "Update Error");
            });
            return;
        }

        using (var httpClient = new HttpClient())
        {
            httpClient.Timeout = DownloadTimeout;

            string tempDownloadPath = Path.Combine(Path.GetTempPath(), asset.name);
            try
            {
                using (var downloadResponse = await httpClient.GetAsync(asset.browser_download_url))
                {
                    downloadResponse.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await downloadResponse.Content.CopyToAsync(fs);
                    }
                }

                string tempUpdateFolder = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_temp_update");
                if (Directory.Exists(tempUpdateFolder))
                {
                    Directory.Delete(tempUpdateFolder, true);
                }
                Directory.CreateDirectory(tempUpdateFolder);

                if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(tempDownloadPath, tempUpdateFolder, true);
                }

                if (!ValidateUpdateFiles(tempUpdateFolder))
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await ShowMessageBoxAsync("Downloaded update appears to be corrupted or incomplete.",
                            "Update Error");
                    });
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"A new version ({latestRelease.tag_name}) has been downloaded. The application will now restart to apply the update.",
                        "Update Available");
                });

                await CreateAndRunUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, versionFilePath);
            }
            catch (TaskCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync("Update download timed out. Please check your internet connection.",
                        "Update Error");
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error downloading update: {ex.Message}",
                        "Update Error");
                });
            }
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
            else
            {
                mainExecutable = Path.Combine(updateDirectory, "N64RecompLauncher");
            }

            if (!File.Exists(mainExecutable))
            {
                Trace.WriteLine($"Main executable not found in update package: {mainExecutable}");
                return false;
            }

            FileInfo exeInfo = new FileInfo(mainExecutable);
            if (exeInfo.Length < 1024)
            {
                Trace.WriteLine($"Main executable too small: {exeInfo.Length} bytes");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error validating update files: {ex.Message}");
            return false;
        }
    }

    private async Task CreateAndRunUpdaterScript(GitHubRelease latestRelease, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory, string versionFilePath)
    {
        string applicationExecutable = Process.GetCurrentProcess().MainModule.FileName;
        string backupDir = Path.Combine(currentAppDirectory, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await CreateWindowsUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, versionFilePath, applicationExecutable, backupDir);
        }
        else
        {
            await CreateUnixUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, versionFilePath, applicationExecutable, backupDir);
        }
    }

    private async Task CreateWindowsUpdaterScript(GitHubRelease latestRelease, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory, string versionFilePath, string applicationExecutable, string backupDir)
    {
        string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.cmd");

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

echo Updating version file...
echo {latestRelease.tag_name} > ""{versionFilePath}""

echo Update completed successfully!
echo Restarting N64RecompLauncher...
timeout /T 2 >nul
start """" ""{applicationExecutable}""

:cleanup
echo Cleaning up temporary files...
if exist ""{tempDownloadPath}"" del ""{tempDownloadPath}"" >nul 2>&1
if exist ""%updateDir%"" rmdir /S /Q ""%updateDir%"" >nul 2>&1

timeout /T 1 >nul
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

    private async Task CreateUnixUpdaterScript(GitHubRelease latestRelease, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory, string versionFilePath, string applicationExecutable, string backupDir)
    {
        string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.sh");
        string executableName = Path.GetFileName(applicationExecutable);

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

echo ""Updating version file...""
echo ""{latestRelease.tag_name}"" > ""{versionFilePath}""

# Make the main executable file executable
if [ -f ""$appDir/N64RecompLauncher"" ]; then
    chmod +x ""$appDir/N64RecompLauncher""
fi

echo ""Update completed successfully!""
echo ""Restarting N64RecompLauncher...""
sleep 2

# Start the application
if [ -f ""$appDir/N64RecompLauncher"" ]; then
    cd ""$appDir""
    nohup ""./N64RecompLauncher"" > /dev/null 2>&1 &
fi

echo ""Cleaning up temporary files...""
rm -f ""{tempDownloadPath}"" 2>/dev/null || true
rm -rf ""$updateDir"" 2>/dev/null || true

# Self-destruct
rm -- ""$0""
";

        await File.WriteAllTextAsync(updaterScriptPath, scriptContent);

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
                Architecture.Arm64 => "ARM64",
                Architecture.X64 => Environment.GetEnvironmentVariable("FLATPAK_ID") != null
                    ? "X64-Flatpak"
                    : "Linux-X64",
                _ => "Linux-X64"
            };
        }

        throw new PlatformNotSupportedException("Unsupported operating system");
    }
}