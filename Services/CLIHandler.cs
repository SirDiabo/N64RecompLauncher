using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace N64RecompLauncher
{
    public class CLIHandler
    {
        public async Task<int> Execute(string[] args)
        {
            if (args.Length == 0)
            {
                ShowHelp();
                return 0;
            }

            var command = args[0].ToLower();

            switch (command)
            {
                case "-h":
                case "--help":
                    ShowHelp();
                    return 0;

                case "-l":
                case "--list":
                    return await ListGames();

                case "-d":
                case "--download":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Error: Game name required");
                        return 1;
                    }
                    return await DownloadGame(args[1]);

                case "-r":
                case "--run":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Error: Game name required");
                        return 1;
                    }
                    return await RunGame(args[1]);

                case "-dr":
                case "--download-run":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Error: Game name required");
                        return 1;
                    }
                    var downloadResult = await DownloadGame(args[1]);
                    if (downloadResult != 0) return downloadResult;
                    return await RunGame(args[1]);

                default:
                    Console.Error.WriteLine($"Unknown command: {command}");
                    ShowHelp();
                    return 1;
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("N64 Recomp Launcher CLI");
            Console.WriteLine();
            Console.WriteLine("Usage: N64RecompLauncher [command] [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  -h, --help              Show this help");
            Console.WriteLine("  -l, --list              List all available games");
            Console.WriteLine("  -d, --download <game>   Download a game");
            Console.WriteLine("  -r, --run <game>        Run an installed game");
            Console.WriteLine("  -dr, --download-run <game>  Download and run a game");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  N64RecompLauncher --list");
            Console.WriteLine("  N64RecompLauncher --download \"Zelda 64\"");
            Console.WriteLine("  N64RecompLauncher --run Banjo64");
            Console.WriteLine("  N64RecompLauncher --download-run \"Mario Kart 64\"");
        }

        private async Task<int> ListGames()
        {
            using var gameManager = new GameManager();
            await gameManager.LoadGamesAsync();

            Console.WriteLine("Available Games:");
            Console.WriteLine("================");

            foreach (var game in gameManager.Games)
            {
                var statusText = game.Status switch
                {
                    GameStatus.Installed => $"[INSTALLED] v{game.InstalledVersion}",
                    GameStatus.UpdateAvailable => $"[UPDATE AVAILABLE] v{game.InstalledVersion} -> v{game.LatestVersion}",
                    _ => $"[NOT INSTALLED] Latest: v{game.LatestVersion}"
                };

                Console.WriteLine($"  {game.Name,-30} {statusText}");
            }

            return 0;
        }

        private async Task<int> DownloadGame(string gameName)
        {
            using var gameManager = new GameManager();
            await gameManager.LoadGamesAsync();

            var game = gameManager.Games.FirstOrDefault(g =>
                g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase) ||
                g.FolderName.Equals(gameName, StringComparison.OrdinalIgnoreCase));

            if (game == null)
            {
                Console.Error.WriteLine($"Game not found: {gameName}");
                Console.WriteLine("Use --list to see available games");
                return 1;
            }

            if (game.Status == GameStatus.Installed)
            {
                Console.WriteLine($"{game.Name} is already installed (v{game.InstalledVersion})");
                return 0;
            }

            Console.WriteLine($"Downloading {game.Name}...");

            var settings = AppSettings.Load();
            await game.PerformActionAsync(
                gameManager.HttpClient,
                gameManager.GamesFolder,
                settings.IsPortable,
                settings);

            Console.WriteLine($"✓ {game.Name} installed successfully");
            return 0;
        }

        private async Task<int> RunGame(string gameName)
        {
            using var gameManager = new GameManager();
            await gameManager.LoadGamesAsync();

            var game = gameManager.Games.FirstOrDefault(g =>
                g.Name.Equals(gameName, StringComparison.OrdinalIgnoreCase) ||
                g.FolderName.Equals(gameName, StringComparison.OrdinalIgnoreCase));

            if (game == null)
            {
                Console.Error.WriteLine($"Game not found: {gameName}");
                return 1;
            }

            if (game.Status != GameStatus.Installed && game.Status != GameStatus.UpdateAvailable)
            {
                Console.Error.WriteLine($"{game.Name} is not installed");
                Console.WriteLine("Use --download to install it first");
                return 1;
            }

            Console.WriteLine($"Launching {game.Name}...");

            var settings = AppSettings.Load();
            await game.PerformActionAsync(
                gameManager.HttpClient,
                gameManager.GamesFolder,
                settings.IsPortable,
                settings);

            return 0;
        }
    }
}