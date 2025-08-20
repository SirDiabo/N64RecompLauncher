using System;
using System.IO;
using System.IO.Compression; // For ZipFile
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json; // For JsonSerializer
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics; // For Process and Trace
using System.Linq; // For FirstOrDefault

namespace N64RecompLauncher
{
    public partial class App : Application
    {
        // GitHub API response classes for deserialization
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

        // Constants for the GitHub repository and version file
        private const string Repository = "SirDiabo/N64RecompLauncher";
        private const string VersionFileName = "version.txt"; // File to store the current application version

        /// <summary>
        /// Overrides the OnStartup method to initiate the update check when the application launches.
        /// The update check runs asynchronously in a background task to prevent blocking the UI.
        /// </summary>
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Run the update check in a background task to avoid freezing the UI during network operations
            await Task.Run(async () =>
            {
                await CheckForUpdatesAndApplyAsync();
            });
        }

        /// <summary>
        /// Checks for a newer version of the application on GitHub, downloads it if available,
        /// and orchestrates the update process by launching an external script.
        /// </summary>
        private async Task CheckForUpdatesAndApplyAsync()
        {
            string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string versionFilePath = Path.Combine(currentAppDirectory, VersionFileName);
            string currentVersionString = "0.0.0"; // Default to a very old version if version.txt is not found

            // Attempt to read the current application version from version.txt
            if (File.Exists(versionFilePath))
            {
                try
                {
                    currentVersionString = (await File.ReadAllTextAsync(versionFilePath).ConfigureAwait(false)).Trim();
                }
                catch (Exception ex)
                {
                    // Log the error if the version file can't be read, but don't block startup
                    Trace.WriteLine($"Error reading current version from {VersionFileName}: {ex.Message}");
                }
            }

            using (var httpClient = new HttpClient())
            {
                // GitHub API requires a User-Agent header
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("N64RecompLauncher-Updater");

                try
                {
                    // Fetch the latest release information from GitHub
                    string releaseResponse = await httpClient.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
                    GitHubRelease latestRelease = JsonSerializer.Deserialize<GitHubRelease>(releaseResponse);

                    // Validate the fetched release information
                    if (latestRelease == null || string.IsNullOrWhiteSpace(latestRelease.tag_name))
                    {
                        Trace.WriteLine("No valid latest release information found on GitHub.");
                        return;
                    }

                    // Compare current version with the latest GitHub release version
                    // Trim 'v' prefix if present (e.g., 'v1.0.0' vs '1.0.0') for proper Version comparison
                    Version current = new Version(currentVersionString.TrimStart('v'));
                    Version latest = new Version(latestRelease.tag_name.TrimStart('v'));

                    if (latest.CompareTo(current) <= 0)
                    {
                        Trace.WriteLine($"Current version {currentVersionString} is up to date or newer than {latestRelease.tag_name}. No update needed.");
                        return; // No update necessary
                    }

                    // Newer version detected, proceed with download
                    Trace.WriteLine($"Newer version {latestRelease.tag_name} available. Current version is {currentVersionString}.");

                    // Determine the platform identifier to find the correct download asset
                    string platformIdentifier = GetPlatformIdentifier();
                    var asset = latestRelease.assets.FirstOrDefault(a =>
                        a.name.Contains(platformIdentifier, StringComparison.OrdinalIgnoreCase) &&
                        (a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    );

                    if (asset == null)
                    {
                        // Notify the user if no suitable asset is found for their platform
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show($"No downloadable update found for your platform ({platformIdentifier}).",
                                "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                        return;
                    }

                    // Download the update package to a temporary file
                    string tempDownloadPath = Path.Combine(Path.GetTempPath(), asset.name);
                    using (var downloadResponse = await httpClient.GetAsync(asset.browser_download_url))
                    {
                        downloadResponse.EnsureSuccessStatusCode(); // Throws on HTTP error codes
                        using (var fs = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await downloadResponse.Content.CopyToAsync(fs);
                        }
                    }

                    // Prepare a temporary folder for extracting the update
                    string tempUpdateFolder = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_temp_update");
                    if (Directory.Exists(tempUpdateFolder))
                    {
                        Directory.Delete(tempUpdateFolder, true); // Clean up previous temp updates
                    }
                    Directory.CreateDirectory(tempUpdateFolder);

                    // Extract the downloaded archive to the temporary update folder
                    if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        ZipFile.ExtractToDirectory(tempDownloadPath, tempUpdateFolder, true);
                    }
                    else if (asset.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    {
                        ExtractTarGz(tempDownloadPath, tempUpdateFolder);
                    }

                    // Notify the user that the update has been downloaded and restart is needed
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"A new version ({latestRelease.tag_name}) has been downloaded. The application will now restart to apply the update.",
                            "Update Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    });

                    // Prepare the updater script to perform file replacement and restart
                    string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.cmd");
                    string applicationExecutable = Process.GetCurrentProcess().MainModule.FileName; // Full path to the running EXE

                    // Construct the batch script content. This script will:
                    // 1. Forcefully kill the current application instance.
                    // 2. Wait a few seconds for file handles to release.
                    // 3. Delete old application files (except the script itself).
                    // 4. Copy new files from the temporary update folder to the application directory.
                    // 5. Write the new version tag to version.txt.
                    // 6. Clean up all temporary files.
                    // 7. Restart the application.
                    // 8. Delete itself.
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

                    // Start the updater script hidden and then shut down the current application
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C \"\"{updaterScriptPath}\"\"", // Execute the script and then close cmd
                        WindowStyle = ProcessWindowStyle.Hidden, // Keep the console window hidden
                        CreateNoWindow = true,
                        UseShellExecute = true // Necessary for 'start' command in script to work correctly
                    });

                    // Shut down the current instance of the application
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Application.Current.Shutdown();
                    });
                }
                catch (HttpRequestException httpEx)
                {
                    // Handle network-related errors during update check/download
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Could not check for updates (Network Error): {httpEx.Message}",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                catch (JsonException jsonEx)
                {
                    // Handle errors during JSON deserialization of GitHub API response
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Could not parse GitHub release information: {jsonEx.Message}",
                            "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                catch (Exception ex)
                {
                    // Catch any other unexpected errors during the update process
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"An error occurred during update: {ex.Message}",
                            "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            }
        }

        /// <summary>
        /// Extracts a .tar.gz archive to a specified destination directory.
        /// It uses platform-specific methods (built-in for Windows, external 'tar' command for Unix-like).
        /// </summary>
        /// <param name="sourceFilePath">Path to the .tar.gz file.</param>
        /// <param name="destinationDirectoryPath">Directory where contents should be extracted.</param>
        private void ExtractTarGz(string sourceFilePath, string destinationDirectoryPath)
        {
            Directory.CreateDirectory(destinationDirectoryPath); // Ensure destination exists

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

        /// <summary>
        /// Extracts a .tar.gz archive on Windows using built-in .NET capabilities.
        /// </summary>
        /// <param name="sourceFilePath">Path to the .tar.gz file.</param>
        /// <param name="destinationDirectoryPath">Directory where contents should be extracted.</param>
        private void ExtractTarGzWindows(string sourceFilePath, string destinationDirectoryPath)
        {
            try
            {
                // Decompress the GZip stream first, then extract the TAR archive from it
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

        /// <summary>
        /// Extracts files from a TAR stream to a specified directory.
        /// Handles basic file and directory entries.
        /// </summary>
        /// <param name="tarStream">The input stream containing the TAR archive.</param>
        /// <param name="destinationDirectoryPath">The root directory for extraction.</param>
        private void ExtractTarFromStream(Stream tarStream, string destinationDirectoryPath)
        {
            // A simple TAR reader implementation. More complex TAR features (like sparse files, long names)
            // would require a dedicated TAR library.
            using (var reader = new BinaryReader(tarStream, Encoding.ASCII, true)) // Leave stream open
            {
                while (true)
                {
                    // Read TAR header (512 bytes)
                    byte[] headerBytes = reader.ReadBytes(512);
                    if (headerBytes.Length < 512 || headerBytes.All(b => b == 0))
                    {
                        // End of archive (two consecutive 512-byte zero blocks) or unexpected end
                        break;
                    }

                    // Extract file name from header
                    string fileName = Encoding.ASCII.GetString(headerBytes, 0, 100).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        // Likely a bad header or end of useful entries
                        break;
                    }

                    // Extract file size from header (octal string conversion)
                    string fileSizeStr = Encoding.ASCII.GetString(headerBytes, 124, 12).TrimEnd('\0');
                    long fileSize;
                    try
                    {
                        fileSize = Convert.ToInt64(fileSizeStr, 8); // Base 8 (octal) conversion
                    }
                    catch (FormatException)
                    {
                        Trace.WriteLine($"Invalid file size format in tar header for '{fileName}': '{fileSizeStr}'. Skipping entry.");
                        // Skip the rest of this entry if size is unreadable
                        tarStream.Seek(512 - (tarStream.Position % 512), SeekOrigin.Current); // Skip to next block start
                        continue;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        Trace.WriteLine($"File size out of range in tar header for '{fileName}': '{fileSizeStr}'. Skipping entry.");
                        tarStream.Seek(512 - (tarStream.Position % 512), SeekOrigin.Current);
                        continue;
                    }

                    // Determine file type ('0' or '\0' for regular file, '5' for directory)
                    byte fileType = headerBytes[156];

                    // Construct the full destination path for the current entry
                    string destPath = Path.Combine(destinationDirectoryPath, fileName);

                    // Ensure the target directory for the file exists
                    string dirName = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dirName) && !Directory.Exists(dirName))
                    {
                        Directory.CreateDirectory(dirName);
                    }

                    if (fileType == '5') // Directory entry
                    {
                        Directory.CreateDirectory(destPath);
                        // No file content to read for directories, just padding
                        // Padding for directories is typically zero but we'll still skip past any potential "data" that isn't data.
                        long paddingBytesDir = (512 - (fileSize % 512)) % 512;
                        if (fileSize > 0 || paddingBytesDir > 0)
                        {
                            tarStream.Seek(fileSize + paddingBytesDir, SeekOrigin.Current);
                        }
                    }
                    else if (fileType == '0' || fileType == '\0') // Regular file entry
                    {
                        using (var fileStream = File.Create(destPath))
                        {
                            int bytesRead = 0;
                            const int bufferSize = 4096; // Read in chunks for efficiency
                            byte[] buffer = new byte[bufferSize];

                            while (bytesRead < fileSize)
                            {
                                int bytesToRead = (int)Math.Min(bufferSize, fileSize - bytesRead);
                                int read = tarStream.Read(buffer, 0, bytesToRead);
                                if (read == 0)
                                {
                                    // Unexpected end of stream before reading full file
                                    Trace.WriteLine($"Premature end of tar stream while reading {fileName}. Expected {fileSize} bytes, got {bytesRead}.");
                                    break;
                                }
                                fileStream.Write(buffer, 0, read);
                                bytesRead += read;
                            }
                        }

                        // Skip padding bytes to align to the next 512-byte block boundary
                        long paddingBytes = (512 - (fileSize % 512)) % 512;
                        if (paddingBytes > 0)
                        {
                            tarStream.Seek(paddingBytes, SeekOrigin.Current);
                        }
                    }
                    // Other file types (symlinks, hardlinks, devices, etc.) are ignored by this basic extractor.
                    // If your archive contains these, you might need a more comprehensive TAR library.
                }
            }
        }

        /// <summary>
        /// Extracts a .tar.gz archive on Unix-like systems (Linux, macOS) using the external 'tar' command.
        /// This is generally more robust for complex archives on these platforms.
        /// </summary>
        /// <param name="sourceFilePath">Path to the .tar.gz file.</param>
        /// <param name="destinationDirectoryPath">Directory where contents should be extracted.</param>
        private void ExtractTarGzUnix(string sourceFilePath, string destinationDirectoryPath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"-xzf \"{sourceFilePath}\" -C \"{destinationDirectoryPath}\"",
                    UseShellExecute = false, // Do not use shell to directly execute 'tar'
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true // Do not show the console window for 'tar'
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit(); // Wait for the tar command to complete

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

        /// <summary>
        /// Determines a platform-specific identifier used to match against GitHub release asset names.
        /// </summary>
        /// <returns>A string representing the current operating system and architecture (e.g., "Windows-x64", "macOS", "Linux-X64").</returns>
        private string GetPlatformIdentifier()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Differentiate Windows by architecture if releases are separate (e.g., x64, x86)
                return RuntimeInformation.OSArchitecture == Architecture.X64 ? "Windows-x64" : "Windows-x86";
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
                        ? "Linux-X64-Flatpak" // Example if you distribute via Flatpak
                        : "Linux-X64",
                    _ => "Linux-X64" // Default for other Linux architectures
                };
            }

            throw new PlatformNotSupportedException("Unsupported operating system for update platform detection.");
        }
    }
}
