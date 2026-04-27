using GitHubLauncher.Core.Models;
using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        private static readonly N64RecompLauncherProfile Profile = N64RecompLauncherProfile.Instance;
        private static readonly string Repository = Profile.Repository;
        private const string VersionFileName = "version.txt";
        private const string UpdateCheckFileName = "update_check.json";
        private const int UpdaterProcessExitTimeoutSeconds = 120;

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
                    case "--add-steam-shortcut":
                        await InitializeGameManager();
                        return await AddSteamShortcutCommand(args);

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

                    case "--update-launcher":
                    case "--self-update":
                        ClearTerminal();
                        await PrintHeader();
                        return await UpdateLauncher();

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

        private static string GetExactArg(string[] args, int index)
        {
            return args.Length > index ? args[index] : string.Empty;
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
                client.DefaultRequestHeaders.Add("User-Agent", Profile.CliUserAgent);

                var response = await client.GetStringAsync("https://api.github.com/repos/sirdiabo/N64RecompLauncher/releases/latest");
                using var doc = JsonDocument.Parse(response);
                var latestTag = doc.RootElement.GetProperty("tag_name").GetString();

                if (!string.IsNullOrEmpty(latestTag) && latestTag != _currentVersion)
                {
                    WriteColor($"  [UPDATE AVAILABLE] ", ColorWarning);
                    Console.WriteLine($"New version {latestTag} is available! Use --update-launcher to upgrade.");
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
            PrintHelpItem("--update-launcher", "Update the launcher itself");
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

        private async Task<int> AddSteamShortcutCommand(string[] args)
        {
            if (_gameManager?.Games == null)
                return PrintError("Game library is not available.");

            string gameName = GetExactArg(args, 1).Trim('"', '\'');
            bool waitForSteamExit = args.Any(arg => string.Equals(arg, "--wait-for-steam-exit", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(gameName))
                return PrintError("No game name was provided for --add-steam-shortcut.");

            var game = _gameManager.Games
                .FirstOrDefault(g => string.Equals(g?.Name, gameName, StringComparison.Ordinal));

            if (game == null)
                return PrintError($"Could not find a game named '{gameName}'.");

            try
            {
                string launcherPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(launcherPath))
                    return PrintError("Could not determine launcher location.");

                await ShortcutHelper.AddGameToSteamFromCliAsync(
                    game,
                    launcherPath,
                    _gameManager.CacheFolder,
                    waitForSteamExit);

                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to add Steam shortcut: {ex.Message}");
            }
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
            var gamesFolder = _gameManager.GamesFolder;
            if (string.IsNullOrEmpty(gamesFolder) || string.IsNullOrEmpty(game.FolderName))
            {
                return PrintError("Game folder is not configured.");
            }

            // Check if need to select executable
            var storedExe = game.LoadSelectedExecutable(gamesFolder);

            if (string.IsNullOrEmpty(storedExe))
            {
                // Check if there are multiple executables
                var gamePath = game.GetInstallPath(gamesFolder);
                GameInfo.EnsureExecutableAtRoot(gamePath);

                var executables = GameInfo.GetExecutableCandidates(gamePath, SearchOption.TopDirectoryOnly, out _);
                if (executables.Count == 0)
                {
                    executables = GameInfo.GetExecutableCandidates(gamePath, SearchOption.AllDirectories, out _);
                }

                if (executables.Count == 0)
                {
                    return PrintError($"No executable found for {game.Name} in {gamePath}.\nThe game may not have installed correctly, or it's an unsupported format.");
                }
                else if (executables.Count > 1)
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
                    game.SaveSelectedExecutable(game.SelectedExecutable, gamesFolder);
                }
            }

            WriteColor($"→ Launching {game.Name}...", ColorSuccess);
            Console.WriteLine();
            Console.WriteLine();

            var launchedProcessSource = new TaskCompletionSource<Process?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnGameProcessStarted(Process? process) => launchedProcessSource.TrySetResult(process);

            try
            {
                game.GameProcessStarted += OnGameProcessStarted;
                await game.PerformActionAsync(
                    _gameManager.HttpClient,
                    gamesFolder,
                    settings.IsPortable,
                    settings);

                game.GameProcessStarted -= OnGameProcessStarted;

                if (launchedProcessSource.Task.IsCompletedSuccessfully)
                {
                    await WaitForLaunchedGameSessionAsync(launchedProcessSource.Task.Result);
                }

                return 0;
            }
            catch (Exception ex)
            {
                game.GameProcessStarted -= OnGameProcessStarted;
                return PrintError($"Failed to launch {game.Name}: {ex.Message}");
            }
        }

        private static async Task WaitForLaunchedGameSessionAsync(Process? process)
        {
            if (process == null)
                return;

            int? processGroupId = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var currentProcessGroupId = await TryGetProcessGroupIdAsync(Environment.ProcessId);
                var launchedProcessGroupId = await TryGetProcessGroupIdAsync(process.Id);
                if (launchedProcessGroupId.HasValue && launchedProcessGroupId != currentProcessGroupId)
                {
                    processGroupId = launchedProcessGroupId;
                }
            }

            try
            {
                await process.WaitForExitAsync();
            }
            catch
            {
                // The process may already be gone by this point.
            }

            if (processGroupId.HasValue)
            {
                while (await HasActiveProcessGroupAsync(processGroupId.Value))
                {
                    await Task.Delay(1000);
                }
            }
        }

        private static async Task<int?> TryGetProcessGroupIdAsync(int processId)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o pgid= -p {processId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var psProcess = Process.Start(startInfo);
                if (psProcess == null)
                    return null;

                var output = await psProcess.StandardOutput.ReadToEndAsync();
                await psProcess.WaitForExitAsync();

                return psProcess.ExitCode == 0 && int.TryParse(output.Trim(), out var processGroupId)
                    ? processGroupId
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> HasActiveProcessGroupAsync(int processGroupId)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o pid= -g {processGroupId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var psProcess = Process.Start(startInfo);
                if (psProcess == null)
                    return false;

                var output = await psProcess.StandardOutput.ReadToEndAsync();
                await psProcess.WaitForExitAsync();

                return psProcess.ExitCode == 0 &&
                       output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                           .Any(line => !string.IsNullOrWhiteSpace(line));
            }
            catch
            {
                return false;
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

        private async Task<int> UpdateLauncher()
        {
            try
            {
                string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string currentVersion = LoadCurrentLauncherVersion(currentAppDirectory);

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.Add("User-Agent", Profile.CliUserAgent);

                WriteColor("→ Checking launcher release...", ColorMuted);
                Console.WriteLine();

                string releaseJson = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
                var release = JsonSerializer.Deserialize<GitHubRelease>(releaseJson);
                if (release == null || string.IsNullOrWhiteSpace(release.tag_name))
                    return PrintError("Could not read launcher update information.");

                if (!IsNewerVersion(release.tag_name, currentVersion))
                {
                    WriteColor($"✓ Launcher is already up to date ({currentVersion})", ColorSuccess);
                    Console.WriteLine();
                    return 0;
                }

                string platformIdentifier = GetPlatformIdentifier();
                var asset = release.assets.FirstOrDefault(a =>
                    a.name.Contains(platformIdentifier, StringComparison.OrdinalIgnoreCase) &&
                    (a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)));

                if (asset == null)
                    return PrintError($"No downloadable launcher update found for this platform ({platformIdentifier}).");

                WriteColor($"→ Downloading launcher {release.tag_name}...", ColorMuted);
                Console.WriteLine();

                string tempDownloadPath = Path.Combine(Path.GetTempPath(), asset.name);
                string tempUpdateFolder = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_temp_update");
                if (Directory.Exists(tempUpdateFolder))
                    Directory.Delete(tempUpdateFolder, true);
                Directory.CreateDirectory(tempUpdateFolder);

                await using (var downloadStream = await client.GetStreamAsync(asset.browser_download_url))
                await using (var fileStream = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await downloadStream.CopyToAsync(fileStream);
                }

                WriteColor("→ Extracting launcher update...", ColorMuted);
                Console.WriteLine();

                if (asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(tempDownloadPath, tempUpdateFolder, true);
                }
                else
                {
                    int tarResult = await ExtractTarGzAsync(tempDownloadPath, tempUpdateFolder);
                    if (tarResult != 0)
                        return PrintError("Failed to extract launcher update archive.");
                }

                if (!ValidateLauncherUpdateFiles(tempUpdateFolder))
                    return PrintError("Downloaded launcher update appears to be incomplete.");

                WriteColor("→ Starting updater. The launcher will relaunch when it finishes.", ColorWarning);
                Console.WriteLine();

                await CreateAndRunLauncherUpdaterScript(release, tempUpdateFolder, tempDownloadPath, currentAppDirectory);
                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to update launcher: {ex.Message}");
            }
        }

        private static string LoadCurrentLauncherVersion(string currentAppDirectory)
        {
            string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);
            if (File.Exists(updateCheckFilePath))
            {
                try
                {
                    var json = File.ReadAllText(updateCheckFilePath);
                    var updateInfo = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (updateInfo != null &&
                        updateInfo.TryGetValue("CurrentVersion", out var versionElement) &&
                        !string.IsNullOrWhiteSpace(versionElement.GetString()))
                    {
                        return versionElement.GetString()!;
                    }
                }
                catch
                {
                }
            }

            string versionFilePath = Path.Combine(currentAppDirectory, VersionFileName);
            return File.Exists(versionFilePath) ? File.ReadAllText(versionFilePath).Trim() : "0.0";
        }

        private static bool ValidateLauncherUpdateFiles(string updateDirectory)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
                Directory.GetDirectories(updateDirectory, "*.app", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            string executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "N64RecompLauncher.exe"
                : "N64RecompLauncher";

            string executablePath = Path.Combine(updateDirectory, executableName);
            return File.Exists(executablePath) && new FileInfo(executablePath).Length > 1024;
        }

        private static async Task<int> ExtractTarGzAsync(string tarGzPath, string extractPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tarGzPath}\" -C \"{extractPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return 1;

            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        private static async Task CreateAndRunLauncherUpdaterScript(GitHubRelease release, string tempUpdateFolder, string tempDownloadPath, string currentAppDirectory)
        {
            int currentProcessId = Environment.ProcessId;
            string applicationExecutable = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Could not determine launcher executable path.");
            string backupDir = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string updateCheckFilePath = Path.Combine(currentAppDirectory, UpdateCheckFileName);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.cmd");
                string scriptContent = $@"@echo off
echo N64RecompLauncher Updater - Version {release.tag_name}
echo.
echo Waiting for N64RecompLauncher CLI to close...
set /A waitCount=0
:wait_loop
tasklist /FI ""PID eq {currentProcessId}"" 2>NUL | find /I ""{currentProcessId}"">NUL
if ""%ERRORLEVEL%""==""0"" (
    if %waitCount% GEQ {UpdaterProcessExitTimeoutSeconds} (
        echo Launcher did not close in time. Aborting update to avoid replacing files while the app is still running.
        pause
        goto cleanup
    )
    set /A waitCount+=1
    timeout /T 1 >NUL
    goto wait_loop
)

set ""appDir={currentAppDirectory}""
set ""backupDir={backupDir}""
set ""updateDir={tempUpdateFolder}""
if not exist ""%backupDir%"" mkdir ""%backupDir%""

echo Backing up files replaced by this update...
for /F ""delims="" %%i in ('dir /B ""%updateDir%""') do (
    if exist ""%appDir%\%%i\\"" (
        xcopy ""%appDir%\%%i"" ""%backupDir%\%%i\\"" /S /E /Y /I >nul 2>&1
    ) else if exist ""%appDir%\%%i"" (
        copy /Y ""%appDir%\%%i"" ""%backupDir%\"" >nul 2>&1
    )
)

echo Applying update...
xcopy ""%updateDir%\*"" ""%appDir%"" /S /E /Y /I >nul 2>&1
if errorlevel 1 (
    echo Update failed! Restoring backup...
    xcopy ""%backupDir%\*"" ""%appDir%"" /S /E /Y /I >nul 2>&1
    pause
    goto cleanup
)

echo {{""CurrentVersion"":""{release.tag_name}"",""LastCheckTime"":""{DateTime.UtcNow:o}"",""LastKnownVersion"":""{release.tag_name}"",""ETag"":"""",""UpdateAvailable"":false}} > ""{updateCheckFilePath}""
echo Update completed successfully.
start """" ""{applicationExecutable}""

:cleanup
if exist ""%backupDir%"" rmdir /S /Q ""%backupDir%"" >nul 2>&1
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
            }
            else
            {
                string updaterScriptPath = Path.Combine(Path.GetTempPath(), "N64RecompLauncher_Updater.sh");
                string scriptContent = $@"#!/bin/bash
echo ""N64RecompLauncher Updater - Version {release.tag_name}""
echo
echo ""Waiting for N64RecompLauncher CLI to close...""
waitCount=0
while kill -0 {currentProcessId} 2>/dev/null; do
    if [ ""$waitCount"" -ge {UpdaterProcessExitTimeoutSeconds} ]; then
        echo ""Launcher did not close in time. Aborting update to avoid replacing files while the app is still running.""
        exit 1
    fi
    waitCount=$((waitCount + 1))
    sleep 1
done

appDir=""{currentAppDirectory}""
backupDir=""{backupDir}""
updateDir=""{tempUpdateFolder}""
mkdir -p ""$backupDir""

echo ""Backing up files replaced by this update...""
find ""$updateDir"" -mindepth 1 -maxdepth 1 -exec basename {{}} \; | while IFS= read -r entry; do
    if [ -e ""$appDir/$entry"" ]; then
        cp -R ""$appDir/$entry"" ""$backupDir/"" 2>/dev/null || true
    fi
done

echo ""Applying update...""
if cp -r ""$updateDir""/* ""$appDir""/ 2>/dev/null; then
    echo ""Update applied successfully""
else
    echo ""Update failed! Restoring backup...""
    cp -r ""$backupDir""/* ""$appDir""/ 2>/dev/null || true
    rm -rf ""$backupDir"" ""$updateDir"" 2>/dev/null || true
    rm -f ""{tempDownloadPath}"" 2>/dev/null || true
    exit 1
fi

cat > ""{updateCheckFilePath}"" << 'EOF'
{{""CurrentVersion"":""{release.tag_name}"",""LastCheckTime"":""{DateTime.UtcNow:o}"",""LastKnownVersion"":""{release.tag_name}"",""ETag"":"""",""UpdateAvailable"":false}}
EOF

if [ -f ""$appDir/N64RecompLauncher"" ]; then
    chmod +x ""$appDir/N64RecompLauncher""
    cd ""$appDir""
    nohup ""./N64RecompLauncher"" > /dev/null 2>&1 &
fi

rm -rf ""$backupDir"" ""$updateDir"" 2>/dev/null || true
rm -f ""{tempDownloadPath}"" 2>/dev/null || true
rm -- ""$0""
";
                scriptContent = scriptContent.Replace("\r\n", "\n").Replace("\r", "\n");
                await File.WriteAllTextAsync(updaterScriptPath, scriptContent);

                using var chmod = Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{updaterScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (chmod != null)
                    await chmod.WaitForExitAsync();

                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"\"{updaterScriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
        }

        private static string GetPlatformIdentifier()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "macOS";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "Linux-ARM64",
                    Architecture.X64 => "Linux-X64",
                    Architecture.X86 => "Linux-X86",
                    Architecture.Arm => "Linux-ARM",
                    _ => "Linux-X64"
                };
            }

            throw new PlatformNotSupportedException("Unsupported operating system");
        }

        private static bool IsNewerVersion(string latestVersion, string currentVersion)
        {
            try
            {
                string cleanLatest = latestVersion.TrimStart('v', 'V');
                string cleanCurrent = currentVersion.TrimStart('v', 'V');
                return Version.TryParse(cleanLatest, out var latest) &&
                    Version.TryParse(cleanCurrent, out var current) &&
                    latest > current;
            }
            catch
            {
                return !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
            }
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
                if (string.IsNullOrEmpty(game.FolderName))
                {
                    return PrintError("Game folder is not configured.");
                }

                var gamePath = game.GetInstallPath(_gameManager.GamesFolder);

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
