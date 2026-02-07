using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace N64RecompLauncher
{
    public class CLIHandler
    {
        private const ConsoleColor ColorTitle = ConsoleColor.Cyan;
        private const ConsoleColor ColorSuccess = ConsoleColor.Green;
        private const ConsoleColor ColorWarning = ConsoleColor.Yellow;
        private const ConsoleColor ColorError = ConsoleColor.Red;
        private const ConsoleColor ColorMuted = ConsoleColor.DarkGray;

        private GameManager? _gameManager;
        private string _currentVersion = "Unknown";

        public async Task<int> Execute(string[] args)
        {
            if (args.Length == 0)
            {
                ClearTerminal();
                await PrintHeader();
                ShowHelp();
                return 0;
            }

            var command = args[0].ToLower();

            try
            {
                switch (command)
                {
                    case "-h":
                    case "--help":
                        ClearTerminal();
                        await PrintHeader();
                        ShowHelp();
                        return 0;

                    case "-l":
                    case "--list":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await ListGames();

                    case "-r":
                    case "--run":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await RunGame(GetGameNameFromArgs(args));

                    case "-d":
                    case "--download":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await DownloadGameCommand(GetGameNameFromArgs(args));

                    case "-u":
                    case "--update":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await UpdateAllGames();

                    case "-x":
                    case "--uninstall":
                        ClearTerminal();
                        await PrintHeader();
                        await InitializeGameManager();
                        return await UninstallGame(GetGameNameFromArgs(args));

                    default:
                        ClearTerminal();
                        await PrintHeader();
                        ShowHelp();
                        return PrintError($"Unknown command: {command}");
                }
            }
            catch (Exception ex)
            {
                return PrintError($"Critical error: {ex.Message}");
            }
            finally
            {
                _gameManager?.Dispose();
            }
        }

        private static void ClearTerminal()
        {
            try
            {
                Console.Clear();
            }
            catch
            {
                // Fallback for terminals that don't support Clear()
                // Send ANSI escape sequences that work on most modern terminals
                Console.Write("\x1b[2J\x1b[H");
            }
        }

        private async Task InitializeGameManager()
        {
            if (_gameManager == null)
            {
                _gameManager = new GameManager();

                // Force load all games
                var settings = AppSettings.Load();
                var originalHiddenGames = settings.HiddenGames.ToList();
                settings.HiddenGames.Clear();

                await _gameManager.LoadGamesAsync();

                // Restore hidden games list
                settings.HiddenGames = originalHiddenGames;
            }
        }

        private string GetGameNameFromArgs(string[] args)
        {
            if (args.Length < 2) return string.Empty;
            return string.Join(" ", args.Skip(1)).Trim('"', '\'');
        }

        private void LoadVersion()
        {
            try
            {
                string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string updateCheckFilePath = Path.Combine(currentAppDirectory, "update_check.json");

                if (File.Exists(updateCheckFilePath))
                {
                    var json = File.ReadAllText(updateCheckFilePath);
                    var updateInfo = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (updateInfo != null && updateInfo.TryGetValue("CurrentVersion", out var versionElement))
                    {
                        _currentVersion = versionElement.GetString() ?? "Unknown";
                        return;
                    }
                }

                // Fallback to version.txt
                string versionFilePath = Path.Combine(currentAppDirectory, "version.txt");
                if (File.Exists(versionFilePath))
                {
                    _currentVersion = File.ReadAllText(versionFilePath).Trim();
                }
            }
            catch
            {
                _currentVersion = "Unknown";
            }
        }

        private async Task PrintHeader()
        {
            LoadVersion();

            Console.WriteLine();
            WriteColor("  _   _   __   _  _   ", ColorTitle);
            WriteColor(" ____                                 ", ColorMuted);
            Console.WriteLine();

            WriteColor(" | \\ | | / /_ | || |  ", ColorTitle);
            WriteColor("|  _ \\ ___  ___ ___  _ __ ___  _ __   ", ColorMuted);
            Console.WriteLine();

            WriteColor(" |  \\| || '_ \\| || |_ ", ColorTitle);
            WriteColor("| |_) / _ \\/ __/ _ \\| '_ ` _ \\| '_ \\  ", ColorMuted);
            Console.WriteLine();

            WriteColor(" | |\\  || (_) |__   _|", ColorTitle);
            WriteColor("|  _ <  __/ (_| (_) | | | | | | |_) | ", ColorMuted);
            Console.WriteLine();

            WriteColor(" |_| \\_| \\___/   |_|  ", ColorTitle);
            WriteColor("|_| \\_\\___|\\___\\___/|_| |_| |_| .__/  ", ColorMuted);
            Console.WriteLine();

            WriteColor("                      ", ColorTitle);
            WriteColor("                              |_|     ", ColorMuted);
            Console.WriteLine();

            Console.WriteLine($"  Launcher Version: {_currentVersion}");

            await CheckForLauncherUpdates();
            PrintLine();
        }

        private async Task CheckForLauncherUpdates()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                client.DefaultRequestHeaders.Add("User-Agent", "N64RecompLauncher-CLI");

                var response = await client.GetStringAsync("https://api.github.com/repos/sirdiabo/N64RecompLauncher/releases/latest");
                using var doc = JsonDocument.Parse(response);
                var latestTag = doc.RootElement.GetProperty("tag_name").GetString();

                if (!string.IsNullOrEmpty(latestTag) && latestTag != _currentVersion)
                {
                    WriteColor($"  [UPDATE AVAILABLE] ", ColorWarning);
                    Console.WriteLine($"New version {latestTag} is available! Use --update to upgrade.");
                    Console.WriteLine();
                }
            }
            catch
            {
                // Silently skip update check if offline or error
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("Usage: N64RecompLauncher [command] [game name]");
            Console.WriteLine();
            WriteColor("Commands:", ColorTitle);
            Console.WriteLine();
            PrintHelpItem("-h, --help", "Show this help screen");
            PrintHelpItem("-l, --list", "List all available games");
            PrintHelpItem("-u, --update", "Update all installed games");
            PrintHelpItem("-d, --download <name>", "Download and install a game");
            PrintHelpItem("-r, --run <name>", "Run a game (auto-updates if needed)");
            PrintHelpItem("-x, --uninstall <name>", "Uninstall a game");
            Console.WriteLine();
            WriteColor("Examples:", ColorMuted);
            Console.WriteLine();
            Console.WriteLine("  N64RecompLauncher --list");
            Console.WriteLine("  N64RecompLauncher --download Banjo64");
            Console.WriteLine("  N64RecompLauncher --run Banjo64");
            Console.WriteLine("  N64RecompLauncher -r \"Mario Kart 64\"");
            Console.WriteLine();
        }

        private async Task<int> ListGames()
        {
            if (_gameManager?.Games == null || !_gameManager.Games.Any())
                return PrintError("No games found in library.");

            int maxNameLength = _gameManager.Games.Max(g => g?.Name?.Length ?? 10) + 4;
            if (maxNameLength < 30) maxNameLength = 30;

            Console.WriteLine();
            WriteColor("Available Games:", ColorTitle);
            Console.WriteLine();
            Console.WriteLine();

            foreach (var game in _gameManager.Games.OrderBy(g => g?.Name))
            {
                if (game == null) continue;

                string name = game.Name ?? "Unknown";
                Console.Write($"  {name.PadRight(maxNameLength)} ");

                string version = CleanVersion(game.LatestVersion);
                string installedVer = CleanVersion(game.InstalledVersion);

                switch (game.Status)
                {
                    case GameStatus.Installed:
                        WriteColor("[INSTALLED]       ", ColorSuccess);
                        Console.WriteLine($" {installedVer}");
                        break;
                    case GameStatus.UpdateAvailable:
                        WriteColor("[UPDATE AVAILABLE]", ColorWarning);
                        Console.WriteLine($" {installedVer} -> {version}");
                        break;
                    default:
                        WriteColor("[NOT INSTALLED]   ", ColorMuted);
                        Console.WriteLine($" Latest: {version}");
                        break;
                }
            }

            Console.WriteLine();
            return 0;
        }

        private async Task<int> RunGame(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("Game name required.");
            }

            var game = FindGame(gameName);
            if (game == null)
            {
                Console.WriteLine();
                WriteColor("Available games: ", ColorMuted);
                Console.WriteLine(string.Join(", ", _gameManager?.Games.Select(g => g.Name) ?? Array.Empty<string>()));
                Console.WriteLine();
                return PrintError($"Game not found: '{gameName}'");
            }

            if (game.Status == GameStatus.NotInstalled)
            {
                return PrintError($"{game.Name} is not installed. Use --download first.");
            }

            // Auto-update if update is available
            if (game.Status == GameStatus.UpdateAvailable)
            {
                WriteColor($"→ Update available for {game.Name}. Updating first...", ColorWarning);
                Console.WriteLine();

                int updateResult = await UpdateOrDownloadGame(game, isUpdate: true);
                if (updateResult != 0) return updateResult;

                // Re-check status after update
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);
            }

            var settings = AppSettings.Load();

            // Check if need to select executable
            var storedExe = game.LoadSelectedExecutable(_gameManager.GamesFolder);

            if (string.IsNullOrEmpty(storedExe))
            {
                // Check if there are multiple executables
                var gamePath = Path.Combine(_gameManager.GamesFolder, game.FolderName);
                var executables = new List<string>();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    executables = Directory.GetFiles(gamePath, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var appBundles = Directory.GetDirectories(gamePath, "*.app", SearchOption.TopDirectoryOnly);
                    executables.AddRange(appBundles);
                }
                else // Linux
                {
                    executables.AddRange(Directory.GetFiles(gamePath, "*.x86_64", SearchOption.TopDirectoryOnly));
                    executables.AddRange(Directory.GetFiles(gamePath, "*.appimage", SearchOption.TopDirectoryOnly));
                    executables.AddRange(Directory.GetFiles(gamePath, "*.arm64", SearchOption.TopDirectoryOnly));
                    executables.AddRange(Directory.GetFiles(gamePath, "*.aarch64", SearchOption.TopDirectoryOnly));
                }

                var launchBat = Path.Combine(gamePath, "launch.bat");
                if (File.Exists(launchBat) && !executables.Contains(launchBat))
                {
                    executables.Add(launchBat);
                }

                if (executables.Count > 1)
                {
                    WriteColor($"⚠ Multiple executables found for {game.Name}.", ColorWarning);
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine("Please run the game through the App Interface to select the correct executable.");
                    Console.WriteLine("After selection, the next time you use --run it will automatically launch that executable.");
                    Console.WriteLine();
                    WriteColor("Opening launcher in 1 second...", ColorMuted);
                    Console.WriteLine();

                    await Task.Delay(1000);

                    // Launch the GUI
                    try
                    {
                        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

                        if (!string.IsNullOrEmpty(exePath))
                        {
                            var exeDir = Path.GetDirectoryName(exePath);
                            var guiExe = exePath;

                            // If running the CLI version, try to find the GUI version
                            if (exePath.Contains("CLI", StringComparison.OrdinalIgnoreCase))
                            {
                                var possibleGuiExe = Path.Combine(exeDir, "N64RecompLauncher.exe");
                                if (File.Exists(possibleGuiExe))
                                {
                                    guiExe = possibleGuiExe;
                                }
                            }

                            var psi = new ProcessStartInfo
                            {
                                FileName = guiExe,
                                UseShellExecute = true
                            };
                            Process.Start(psi);

                            return 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        return PrintError($"Failed to launch GUI: {ex.Message}");
                    }

                    return PrintError("Unable to launch GUI.");
                }
                else if (executables.Count == 1)
                {
                    game.SelectedExecutable = executables[0];
                    game.SaveSelectedExecutable(game.SelectedExecutable, _gameManager.GamesFolder);
                }
            }

            WriteColor($"→ Launching {game.Name}...", ColorSuccess);
            Console.WriteLine();
            Console.WriteLine();

            try
            {
                await game.PerformActionAsync(
                    _gameManager.HttpClient,
                    _gameManager.GamesFolder,
                    settings.IsPortable,
                    settings);

                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to launch {game.Name}: {ex.Message}");
            }
        }

        private async Task<int> UpdateOrDownloadGame(GameInfo game, bool isUpdate)
        {
            try
            {
                var settings = AppSettings.Load();

                WriteColor($"→ {(isUpdate ? "Updating" : "Downloading")} {game.Name}...", isUpdate ? ColorWarning : ColorTitle);
                Console.WriteLine();
                Console.WriteLine();

                // Store initial version for comparison
                string initialVersion = game.InstalledVersion ?? "";

                // Get the latest release info
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                // Get platform identifier
                string platformIdentifier = GameInfo.GetPlatformIdentifier(settings);
                WriteColor($"→ Detected platform: {platformIdentifier}", ColorMuted);
                Console.WriteLine();

                // Check for multiple downloads first
                var cachedRelease = game.GetType().GetMethod("GetLatestRelease", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(game, null);

                if (cachedRelease != null)
                {
                    var assetsProperty = cachedRelease.GetType().GetProperty("assets");
                    if (assetsProperty != null)
                    {
                        var assets = assetsProperty.GetValue(cachedRelease) as System.Collections.IEnumerable;
                        var assetList = assets?.Cast<object>().ToList();

                        if (assetList != null && assetList.Count > 1)
                        {
                            WriteColor($"→ Finding {platformIdentifier} version...", ColorMuted);
                            Console.WriteLine();

                            // Find matching platform
                            object? matchingAsset = null;
                            foreach (var asset in assetList)
                            {
                                var nameProperty = asset.GetType().GetProperty("name");
                                if (nameProperty != null)
                                {
                                    var assetName = nameProperty.GetValue(asset)?.ToString();
                                    if (!string.IsNullOrEmpty(assetName) && GameInfo.MatchesPlatform(assetName, platformIdentifier))
                                    {
                                        matchingAsset = asset;
                                        break;
                                    }
                                }
                            }

                            if (matchingAsset != null)
                            {
                                var nameProperty = matchingAsset.GetType().GetProperty("name");
                                var assetName = nameProperty?.GetValue(matchingAsset)?.ToString();

                                // Set the selected download via reflection
                                var selectedDownloadProperty = game.GetType().GetProperty("SelectedDownload");
                                selectedDownloadProperty?.SetValue(game, matchingAsset);

                                WriteColor($"→ Selected: {assetName}", ColorSuccess);
                                Console.WriteLine();
                                Console.WriteLine();
                            }
                            else
                            {
                                WriteColor($"⚠ No {platformIdentifier} version found, using first available", ColorWarning);
                                Console.WriteLine();
                                Console.WriteLine();

                                var selectedDownloadProperty = game.GetType().GetProperty("SelectedDownload");
                                selectedDownloadProperty?.SetValue(game, assetList[0]);
                            }
                        }
                    }
                }

                // Start the actual download
                var downloadTask = game.PerformActionAsync(
                    _gameManager.HttpClient,
                    _gameManager.GamesFolder,
                    settings.IsPortable,
                    settings);

                // Monitor progress and version changes
                double lastProgress = 0;
                int timeout = 600; // 10 minutes
                int waited = 0;

                while (waited < timeout)
                {
                    // Check if installation completed
                    if (game.Status == GameStatus.Installed)
                    {
                        break;
                    }

                    // Check if version changed (installation complete)
                    if (!string.IsNullOrEmpty(game.InstalledVersion) &&
                        game.InstalledVersion != initialVersion &&
                        game.InstalledVersion != "Unknown")
                    {
                        break;
                    }

                    if (game.DownloadProgress > lastProgress + 5 || game.DownloadProgress >= 100)
                    {
                        Console.Write($"\r  Progress: {game.DownloadProgress:F0}%   ");
                        lastProgress = game.DownloadProgress;
                    }

                    await Task.Delay(1000);
                    waited++;
                }

                Console.WriteLine();
                Console.WriteLine();

                if (waited >= timeout)
                {
                    return PrintError("Download timed out.");
                }

                // Verify installation
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                if (game.Status == GameStatus.Installed)
                {
                    WriteColor($"✓ {game.Name} ", ColorSuccess);
                    Console.WriteLine($"{(isUpdate ? "updated" : "installed")} successfully ({CleanVersion(game.InstalledVersion)})");
                    Console.WriteLine();
                    return 0;
                }
                else
                {
                    return PrintError($"{(isUpdate ? "Update" : "Download")} failed. Final status: {game.Status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                return PrintError($"Failed to {(isUpdate ? "update" : "download")} {game.Name}: {ex.Message}");
            }
        }

        private async Task<int> DownloadGameCommand(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("Game name required.");
            }

            var game = FindGame(gameName);
            if (game == null)
            {
                Console.WriteLine();
                WriteColor("Available games: ", ColorMuted);
                Console.WriteLine(string.Join(", ", _gameManager?.Games.Select(g => g.Name) ?? Array.Empty<string>()));
                Console.WriteLine();
                return PrintError($"Game not found: '{gameName}'");
            }

            if (game.Status == GameStatus.Installed)
            {
                WriteColor($"✓ {game.Name} ", ColorSuccess);
                Console.WriteLine($"is already installed ({CleanVersion(game.InstalledVersion)})");
                Console.WriteLine();
                return 0;
            }

            if (game.Status == GameStatus.UpdateAvailable)
            {
                return await UpdateOrDownloadGame(game, isUpdate: true);
            }

            return await UpdateOrDownloadGame(game, isUpdate: false);
        }

        private async Task<int> UpdateAllGames()
        {
            if (_gameManager?.Games == null || !_gameManager.Games.Any())
                return PrintError("No games found in library.");

            var installedGames = _gameManager.Games.Where(g => g.Status == GameStatus.Installed || g.Status == GameStatus.UpdateAvailable).ToList();

            if (!installedGames.Any())
            {
                WriteColor("✓ No installed games to update.", ColorSuccess);
                Console.WriteLine();
                return 0;
            }

            Console.WriteLine($"Checking {installedGames.Count} installed game(s) for updates...");
            Console.WriteLine();

            int updatedCount = 0;
            int errorCount = 0;

            foreach (var game in installedGames)
            {
                if (game.Status == GameStatus.UpdateAvailable)
                {
                    int result = await UpdateOrDownloadGame(game, isUpdate: true);
                    if (result == 0)
                        updatedCount++;
                    else
                        errorCount++;
                }
            }

            Console.WriteLine();
            if (updatedCount > 0)
            {
                WriteColor($"✓ Updated {updatedCount} game(s)", ColorSuccess);
                Console.WriteLine();
            }
            if (errorCount > 0)
            {
                WriteColor($"⚠ {errorCount} game(s) failed to update", ColorWarning);
                Console.WriteLine();
            }
            if (updatedCount == 0 && errorCount == 0)
            {
                WriteColor("✓ All games are up to date", ColorSuccess);
                Console.WriteLine();
            }

            return errorCount > 0 ? 1 : 0;
        }

        private async Task<int> UninstallGame(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
            {
                ShowHelp();
                return PrintError("Game name required.");
            }

            var game = FindGame(gameName);
            if (game == null)
            {
                Console.WriteLine();
                WriteColor("Available games: ", ColorMuted);
                Console.WriteLine(string.Join(", ", _gameManager?.Games.Select(g => g.Name) ?? Array.Empty<string>()));
                Console.WriteLine();
                return PrintError($"Game not found: '{gameName}'");
            }

            if (game.Status == GameStatus.NotInstalled)
            {
                WriteColor($"✓ {game.Name} ", ColorMuted);
                Console.WriteLine("is not installed.");
                Console.WriteLine();
                return 0;
            }

            WriteColor($"→ Uninstalling {game.Name}...", ColorWarning);
            Console.WriteLine();

            try
            {
                var gamePath = Path.Combine(_gameManager.GamesFolder, game.FolderName);

                if (Directory.Exists(gamePath))
                {
                    Directory.Delete(gamePath, recursive: true);
                }

                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                WriteColor($"✓ {game.Name} ", ColorSuccess);
                Console.WriteLine("uninstalled successfully.");
                Console.WriteLine();
                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to uninstall {game.Name}: {ex.Message}");
            }
        }

        private GameInfo? FindGame(string name)
        {
            if (_gameManager?.Games == null) return null;

            return _gameManager.Games.FirstOrDefault(g =>
                (g?.Name != null && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                (g?.FolderName != null && g.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase)));
        }

        private string CleanVersion(string? version)
        {
            if (string.IsNullOrEmpty(version)) return "v0.0.0";
            if (version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "Unknown";
            return "v" + version.TrimStart('v', 'V');
        }

        private int PrintError(string message)
        {
            WriteColor("ERROR: ", ColorError);
            Console.WriteLine(message);
            Console.WriteLine();
            return 1;
        }

        private void PrintHelpItem(string command, string desc)
        {
            Console.Write("  ");
            WriteColor(command.PadRight(30), ColorSuccess);
            Console.WriteLine(desc);
        }

        private void PrintLine() => Console.WriteLine(new string('─', 70));

        private void WriteColor(string text, ConsoleColor color)
        {
            var old = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.Write(text);
            }
            finally
            {
                Console.ForegroundColor = old;
            }
        }
    }
}