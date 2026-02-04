using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
                        await PrintHeader();
                        ShowHelp();
                        return 0;

                    case "-v":
                    case "--version":
                        LoadVersion();
                        Console.WriteLine($"N64 Recomp Launcher v{_currentVersion}");
                        return 0;

                    case "-u":
                    case "--update":
                        await PrintHeader();
                        return await UpdateLauncher();

                    case "-l":
                    case "--list":
                        await PrintHeader();
                        await InitializeGameManager();
                        return await ListGames();

                    case "-d":
                    case "--download":
                        await PrintHeader();
                        await InitializeGameManager();
                        return await DownloadGame(GetGameNameFromArgs(args));

                    case "-r":
                    case "--run":
                        await PrintHeader();
                        await InitializeGameManager();
                        return await RunGame(GetGameNameFromArgs(args));

                    case "-dr":
                    case "--download-run":
                        await PrintHeader();
                        await InitializeGameManager();
                        string name = GetGameNameFromArgs(args);
                        int dlResult = await DownloadGame(name);
                        if (dlResult != 0) return dlResult;
                        return await RunGame(name);

                    default:
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

        private async Task<int> UpdateLauncher()
        {
            WriteColor("Checking for launcher updates...", ColorTitle);
            Console.WriteLine();

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "N64RecompLauncher-CLI");

                var response = await client.GetStringAsync("https://api.github.com/repos/sirdiabo/N64RecompLauncher/releases/latest");
                using var doc = JsonDocument.Parse(response);

                var latestTag = doc.RootElement.GetProperty("tag_name").GetString();

                if (string.IsNullOrEmpty(latestTag))
                {
                    return PrintError("Could not determine latest version.");
                }

                if (latestTag == _currentVersion)
                {
                    WriteColor("✓ ", ColorSuccess);
                    Console.WriteLine("Launcher is already up to date!");
                    return 0;
                }

                WriteColor($"Update available: {_currentVersion} -> {latestTag}", ColorWarning);
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("To update, please:");
                Console.WriteLine("  1. Download the latest release from:");
                Console.WriteLine($"     https://github.com/SirDiabo/N64RecompLauncher/releases/tag/{latestTag}");
                Console.WriteLine("  2. Extract and replace the current launcher files");
                Console.WriteLine();
                WriteColor("Note: ", ColorMuted);
                Console.WriteLine("Automatic updates will be added in a future version.");

                return 0;
            }
            catch (Exception ex)
            {
                return PrintError($"Failed to check for updates: {ex.Message}");
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("Usage: N64RecompLauncher [command] [game name]");
            Console.WriteLine();
            WriteColor("Commands:", ColorTitle);
            Console.WriteLine();
            PrintHelpItem("-h, --help", "Show this help screen");
            PrintHelpItem("-v, --version", "Show launcher version");
            PrintHelpItem("-u, --update", "Check for launcher updates");
            PrintHelpItem("-l, --list", "List all available games");
            PrintHelpItem("-d, --download <n>", "Download or update a game");
            PrintHelpItem("-r, --run <n>", "Run a game (auto-updates if needed)");
            PrintHelpItem("-dr, --download-run <n>", "Download then immediately run");
            Console.WriteLine();
            WriteColor("Examples:", ColorMuted);
            Console.WriteLine();
            Console.WriteLine("  N64RecompLauncher --list");
            Console.WriteLine("  N64RecompLauncher --download \"Zelda 64\"");
            Console.WriteLine("  N64RecompLauncher --run Banjo64");
            Console.WriteLine("  N64RecompLauncher -dr \"Mario Kart 64\"");
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

        private async Task<int> DownloadGame(string gameName)
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

            // Check if update is available
            if (game.Status == GameStatus.UpdateAvailable)
            {
                WriteColor($"→ Updating {game.Name} ", ColorWarning);
                Console.WriteLine($"from {CleanVersion(game.InstalledVersion)} to {CleanVersion(game.LatestVersion)}...");
            }
            else if (game.Status == GameStatus.Installed)
            {
                WriteColor($"✓ {game.Name} ", ColorSuccess);
                Console.WriteLine($"is already up to date ({CleanVersion(game.InstalledVersion)})");
                return 0;
            }
            else
            {
                WriteColor($"→ Downloading {game.Name} ", ColorTitle);
                Console.WriteLine($"{CleanVersion(game.LatestVersion)}...");
            }

            Console.WriteLine();

            try
            {
                var settings = AppSettings.Load();

                // Hook into progress events
                double lastProgress = 0;
                PropertyChangedEventHandler progressHandler = (s, e) =>
                {
                    if (e.PropertyName == nameof(GameInfo.DownloadProgress))
                    {
                        var progress = game.DownloadProgress;
                        if (progress > lastProgress + 5 || progress >= 100) // Update every 5%
                        {
                            Console.Write($"\r  Progress: {progress:F0}%   ");
                            lastProgress = progress;
                        }
                    }
                };

                game.PropertyChanged += progressHandler;

                try
                {
                    await game.PerformActionAsync(
                        _gameManager.HttpClient,
                        _gameManager.GamesFolder,
                        settings.IsPortable,
                        settings);

                    // Wait for download to complete
                    int timeout = 300; // 5 minutes
                    int waited = 0;
                    while ((game.Status == GameStatus.Downloading ||
                            game.Status == GameStatus.Installing ||
                            game.Status == GameStatus.Updating) &&
                           waited < timeout)
                    {
                        await Task.Delay(1000);
                        waited++;
                    }
                }
                finally
                {
                    game.PropertyChanged -= progressHandler;
                }

                Console.WriteLine();

                // Verify installation
                await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder, forceUpdateCheck: true);

                if (game.Status == GameStatus.Installed || game.Status == GameStatus.UpdateAvailable)
                {
                    WriteColor($"✓ {game.Name} ", ColorSuccess);
                    Console.WriteLine($"installed successfully ({CleanVersion(game.InstalledVersion)})");
                    Console.WriteLine();
                    return 0;
                }
                else
                {
                    return PrintError($"Installation may have failed. Status: {game.Status}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                return PrintError($"Failed to download {game.Name}: {ex.Message}");
            }
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
                Console.WriteLine();

                int updateResult = await DownloadGame(gameName);
                if (updateResult != 0) return updateResult;
            }

            WriteColor($"→ Launching {game.Name}...", ColorSuccess);
            Console.WriteLine();
            Console.WriteLine();

            try
            {
                var settings = AppSettings.Load();
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