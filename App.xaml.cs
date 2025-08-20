using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json; 
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.Linq;

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

        private const string Repository = "SirDiabo/N64RecompLauncher";
        private const string VersionFileName = "version.txt";

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
            string currentVersionString = "0.1";

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

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("N64RecompLauncher-Updater");

                try
                {
                    string releaseResponse = await httpClient.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
                    GitHubRelease latestRelease = JsonSerializer.Deserialize<GitHubRelease>(releaseResponse);

                    if (latestRelease == null || string.IsNullOrWhiteSpace(latestRelease.tag_name))
                    {
                        Trace.WriteLine("No valid latest release information found on GitHub.");
                        return;
                    }

                    Version current = new Version(currentVersionString.TrimStart('v'));
                    Version latest = new Version(latestRelease.tag_name.TrimStart('v'));

                    if (latest.CompareTo(current) <= 0)
                    {
                        Trace.WriteLine($"Current version {currentVersionString} is up to date or newer than {latestRelease.tag_name}. No update needed.");
                        return;
                    }

                    Trace.WriteLine($"Newer version {latestRelease.tag_name} available. Current version is {currentVersionString}.");

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

                    string tempDownloadPath = Path.Combine(Path.GetTempPath(), asset.name);
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

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"A new version ({latestRelease.tag_name}) has been downloaded. The application will now restart to apply the update.",
                            "Update Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    });

                    string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.cmd");
                    string applicationExecutable = Process.GetCurrentProcess().MainModule.FileName;

                    string scriptContent = $@"
@echo off
echo Waiting for N64RecompLauncher to close...
taskkill /F /IM ""{Path.GetFileName(applicationExecutable)}"" > nul 2>&1
timeout /t 3 /nobreak > nul

echo Applying update...
rem Delete old application files, but exclude the updater script itself
for /D %%i in (""{currentAppDirectory}""\*) do rmdir /S /Q ""%%i"" > nul 2>&1
for %%i in (""{currentAppDirectory}""\*.*) do if not ""%%~nxi""==""{Path.GetFileName(updaterScriptPath)}"" del ""%%i"" > nul 2>&1

rem Copy new files from temporary update folder to the application directory
xcopy ""{tempUpdateFolder}\*"" ""{currentAppDirectory}"" /S /E /Y /I > nul 2>&1

rem Write the new version to version.txt
echo {latestRelease.tag_name} > ""{versionFilePath}""

rem Clean up temporary downloaded file and extracted folder
del ""{tempDownloadPath}"" > nul 2>&1
rmdir /S /Q ""{tempUpdateFolder}"" > nul 2>&1

echo Update applied. Restarting N64RecompLauncher...
start """" ""{applicationExecutable}""
rem Delete the updater script itself after restarting the app
del ""%~f0""
";
                    await File.WriteAllTextAsync(updaterScriptPath, scriptContent);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C \"\"{updaterScriptPath}\"\"",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                        UseShellExecute = true
                    });

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Application.Current.Shutdown();
                    });
                }
                catch (HttpRequestException httpEx)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Could not check for updates (Network Error): {httpEx.Message}",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                catch (JsonException jsonEx)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Could not parse GitHub release information: {jsonEx.Message}",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"An error occurred during update: {ex.Message}",
                            "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
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
