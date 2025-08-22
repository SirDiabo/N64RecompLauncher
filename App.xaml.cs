using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace N64RecompLauncher
{
    public partial class App : Application
    {
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            await Task.Run(async () =>
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
                    GitHubRelease latestRelease = JsonSerializer.Deserialize<GitHubRelease>(releaseResponse);

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

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Could not check for updates (Network Error): {httpEx.Message}",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                catch (JsonException jsonEx)
                {
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Could not parse GitHub release information: {jsonEx.Message}",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                catch (TaskCanceledException)
                {
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("Update check timed out. Please check your internet connection.",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                catch (Exception ex)
                {
                    await SaveUpdateCheckInfo(updateCheckFilePath, updateCheckInfo);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"An error occurred during update: {ex.Message}",
                            "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
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
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"No downloadable update found for your platform ({platformIdentifier}).",
                        "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
                return;
            }

            DriveInfo drive = new DriveInfo(Path.GetPathRoot(currentAppDirectory));
            if (drive.AvailableFreeSpace < 500 * 1024 * 1024)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Insufficient disk space for update. At least 500MB required.",
                        "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    else if (asset.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractTarGz(tempDownloadPath, tempUpdateFolder);
                    }

                    if (!ValidateUpdateFiles(tempUpdateFolder))
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show("Downloaded update appears to be corrupted or incomplete.",
                                "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                        return;
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"A new version ({latestRelease.tag_name}) has been downloaded. The application will now restart to apply the update.",
                            "Update Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    });

                    await CreateAndRunUpdaterScript(latestRelease, tempUpdateFolder, tempDownloadPath, currentAppDirectory, versionFilePath);
                }
                catch (TaskCanceledException)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("Update download timed out. Please check your internet connection.",
                            "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Error downloading update: {ex.Message}",
                            "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            }
        }

        private bool ValidateUpdateFiles(string updateDirectory)
        {
            try
            {
                string mainExe = Path.Combine(updateDirectory, "N64RecompLauncher.exe");
                if (!File.Exists(mainExe))
                {
                    Trace.WriteLine("Main executable not found in update package");
                    return false;
                }

                FileInfo exeInfo = new FileInfo(mainExe);
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
            string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.cmd");
            string applicationExecutable = Process.GetCurrentProcess().MainModule.FileName;

            string backupDir = Path.Combine(currentAppDirectory, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

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

            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
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
                throw new PlatformNotSupportedException("Unsupported operating system for tar.gz extraction.");
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
                MessageBox.Show($"Error extracting tar.gz on Windows: {ex.Message}",
                    "Extraction Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private bool IsSafePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            if (fileName.Contains("..") || fileName.Contains("./") || fileName.Contains(".\\"))
                return false;

            if (Path.IsPathRooted(fileName))
                return false;

            if (fileName.IndexOfAny(Path.GetInvalidPathChars()) != -1)
                return false;

            string[] reservedNames = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
            if (reservedNames.Contains(nameWithoutExtension))
                return false;

            return true;
        }

        private void ExtractTarFromStream(Stream tarStream, string destinationDirectoryPath)
        {
            using (var reader = new BinaryReader(tarStream, Encoding.ASCII, true))
            {
                while (true)
                {
                    byte[] headerBytes = reader.ReadBytes(512);
                    if (headerBytes.Length < 512 || headerBytes.All(b => b == 0))
                    {
                        break;
                    }

                    string fileName = Encoding.ASCII.GetString(headerBytes, 0, 100).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        break;
                    }

                    string fileSizeStr = Encoding.ASCII.GetString(headerBytes, 124, 12).TrimEnd('\0');
                    long fileSize;

                    if (!IsSafePath(fileName))
                    {
                        Trace.WriteLine($"Skipping potentially unsafe path in tar: {fileName}");
                        try
                        {
                            fileSize = Convert.ToInt64(fileSizeStr, 8);
                            long paddingBytes = (512 - (fileSize % 512)) % 512;
                            tarStream.Seek(fileSize + paddingBytes, SeekOrigin.Current);
                        }
                        catch
                        {
                            tarStream.Seek(512 - (tarStream.Position % 512), SeekOrigin.Current);
                        }
                        continue;
                    }
                    try
                    {
                        fileSize = Convert.ToInt64(fileSizeStr, 8);
                    }
                    catch (FormatException)
                    {
                        Trace.WriteLine($"Invalid file size format in tar header for '{fileName}': '{fileSizeStr}'. Skipping entry.");
                        tarStream.Seek(512 - (tarStream.Position % 512), SeekOrigin.Current);
                        continue;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        Trace.WriteLine($"File size out of range in tar header for '{fileName}': '{fileSizeStr}'. Skipping entry.");
                        tarStream.Seek(512 - (tarStream.Position % 512), SeekOrigin.Current);
                        continue;
                    }

                    byte fileType = headerBytes[156];

                    string destPath = Path.Combine(destinationDirectoryPath, fileName);

                    string dirName = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dirName) && !Directory.Exists(dirName))
                    {
                        Directory.CreateDirectory(dirName);
                    }

                    if (fileType == '5')
                    {
                        Directory.CreateDirectory(destPath);
                        long paddingBytesDir = (512 - (fileSize % 512)) % 512;
                        if (fileSize > 0 || paddingBytesDir > 0)
                        {
                            tarStream.Seek(fileSize + paddingBytesDir, SeekOrigin.Current);
                        }
                    }
                    else if (fileType == '0' || fileType == '\0')
                    {
                        using (var fileStream = File.Create(destPath))
                        {
                            int bytesRead = 0;
                            const int bufferSize = 4096;
                            byte[] buffer = new byte[bufferSize];

                            while (bytesRead < fileSize)
                            {
                                int bytesToRead = (int)Math.Min(bufferSize, fileSize - bytesRead);
                                int read = tarStream.Read(buffer, 0, bytesToRead);
                                if (read == 0)
                                {
                                    Trace.WriteLine($"Premature end of tar stream while reading {fileName}. Expected {fileSize} bytes, got {bytesRead}.");
                                    break;
                                }
                                fileStream.Write(buffer, 0, read);
                                bytesRead += read;
                            }
                        }

                        long paddingBytes = (512 - (fileSize % 512)) % 512;
                        if (paddingBytes > 0)
                        {
                            tarStream.Seek(paddingBytes, SeekOrigin.Current);
                        }
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
                        throw new Exception($"Tar extraction failed with exit code {process.ExitCode}: {errorOutput}");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting tar.gz on Unix: {ex.Message}",
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
    }
}