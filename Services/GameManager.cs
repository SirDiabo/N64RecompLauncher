using N64RecompLauncher.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Xml.Linq;

namespace N64RecompLauncher.Services
{
    public class GameManager : INotifyPropertyChanged, IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _disposed = false;
        private readonly string _gamesFolder;
        private readonly string _cacheFolder;

        public ObservableCollection<GameInfo> Games { get; set; } = [];
        public HttpClient HttpClient => _httpClient;
        public string? GamesFolder => _gamesFolder;
        public string? CacheFolder => _cacheFolder;

        private string _CurrentVersionString;
        public string CurrentVersionString
        {
            get => _CurrentVersionString;
            set
            {
                if (_CurrentVersionString != value)
                {
                    _CurrentVersionString = value;
                    OnPropertyChanged(nameof(CurrentVersionString));
                }
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                }
                _disposed = true;
            }
        }
        public GameManager()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "N64Recomp-Launcher/1.0");
            _gamesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecompiledGames");
            _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");

            Directory.CreateDirectory(_gamesFolder);
            Directory.CreateDirectory(_cacheFolder);

            LoadVersionString();

            Games = new ObservableCollection<GameInfo>();
        }

        private void LoadVersionString()
        {
            try
            {
                string versionFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
                CurrentVersionString = File.Exists(versionFilePath)
                    ? File.ReadAllText(versionFilePath).Trim()
                    : "Version information not found";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading version: {ex.Message}");
                CurrentVersionString = "Version loading failed";
            }
        }

        private List<GameInfo> GetBaseGames()
        {
            return new List<GameInfo>
            {
                new() {
                    Name = "Zelda 64 Recomp",
                    Repository = "Zelda64Recomp/Zelda64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Zelda64Recomp",
                    GameManager = this,
                },
                new() {
                    Name = "Goemon 64 Recomp",
                    Repository = "klorfmorf/Goemon64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Goemon64Recomp",
                    GameManager = this,
                },
                new() {
                    Name = "Mario Kart 64 Recomp",
                    Repository = "sonicdcer/MarioKart64Recomp",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "MarioKart64Recomp",
                    GameManager = this,
                },
                new() {
                    Name = "Dinosaur Planet Recomp",
                    Repository = "Francessco121/dino-recomp",
                    Branch = "main",
                    ImageRes = "64",
                    FolderName = "DinosaurPlanetRecomp",
                    GameManager = this,
                },
                new() {
                    Name = "Dr. Mario 64 Recomp",
                    Repository = "theboy181/drmario64_recomp_plus",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "DrMario64RecompPlus",
                    GameManager = this,
                },
                new() {
                    Name = "Duke Nukem: Zero Hour Recomp",
                    Repository = "sonicdcer/DNZHRecomp",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "DNZHRecomp",
                    GameManager = this,
                },
                new() {
                    Name = "Starfox 64 Recomp",
                    Repository = "sonicdcer/Starfox64Recomp",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "Starfox64Recomp",
                    GameManager = this,
                },
            };
        }

        private void LoadCustomIcons()
        {
            foreach (var game in Games)
            {
                game.LoadCustomIcon(_cacheFolder);
            }
        }

        public GameInfo? FindGameByName(string name)
        {
            return Games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public GameInfo? FindGameByFolderName(string folderName)
        {
            return Games.FirstOrDefault(predicate: g => g.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task LoadGamesAsync()
        {
            var settings = AppSettings.Load();

            Games.Clear();

            var allGames = GetBaseGames();

            foreach (var game in allGames)
            {
                if (!settings.HiddenGames.Contains(game.Name))
                {
                    Games.Add(game);
                }
            }

            LoadCustomIcons();

            var gameStatuses = Games
                .Select(g => new {
                    Game = g,
                    IsInstalled = Directory.Exists(Path.Combine(_gamesFolder, g.FolderName)),
                    LastPlayed = GetLastPlayedTime(g.FolderName)
                })
                .ToList();

            var sortedGames = gameStatuses
                .OrderBy(x => x.IsInstalled ? 0 : 1)
                .ThenByDescending(x => x.LastPlayed)
                .ThenBy(x => x.Game.Name)
                .Select(x => x.Game)
                .ToList();

            for (int i = 0; i < sortedGames.Count; i++)
            {
                var currentIndex = Games.IndexOf(sortedGames[i]);
                if (currentIndex != i && currentIndex != -1)
                {
                    Games.Move(currentIndex, i);
                }
            }

            var statusTasks = Games.Select(async game =>
            {
                try
                {
                    await game.CheckStatusAsync(_httpClient, _gamesFolder);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error checking status for {game.Name}: {ex.Message}");
                }
            });

            await Task.WhenAll(statusTasks);
        }

        private DateTime GetLastPlayedTime(string folderName)
        {
            try
            {
                var gamePath = Path.Combine(_gamesFolder, folderName);
                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");

                if (File.Exists(lastPlayedPath))
                {
                    var timeString = File.ReadAllText(lastPlayedPath).Trim();
                    if (DateTime.TryParseExact(timeString, "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out DateTime lastPlayed))
                    {
                        return lastPlayed;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read LastPlayed.txt for {folderName}: {ex.Message}");
            }

            return DateTime.MinValue;
        }

        public void HideGame(string gameName)
        {
            var settings = AppSettings.Load();
            if (!settings.HiddenGames.Contains(gameName))
            {
                settings.HiddenGames.Add(gameName);
                AppSettings.Save(settings);
                FilterGames(settings);
            }
        }

        public void UnhideAllGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);
            FilterGames(settings);
        }

        public void HideAllNonInstalledGames()
        {
            var settings = AppSettings.Load();
            foreach (var game in Games)
            {
                if (game.Status == GameStatus.NotInstalled && !settings.HiddenGames.Contains(game.Name))
                {
                    settings.HiddenGames.Add(game.Name);
                }
            }
            AppSettings.Save(settings);
            FilterGames(settings);
        }

        private void FilterGames(AppSettings settings)
        {
            for (int i = Games.Count - 1; i >= 0; i--)
            {
                if (settings.HiddenGames.Contains(Games[i].Name))
                {
                    Games.RemoveAt(i);
                }
            }
        }

        public void RefreshGamesWithFilter(AppSettings settings)
        {
            var allGames = GetBaseGames();

            Games.Clear();

            foreach (var game in allGames)
            {
                if (!settings.HiddenGames.Contains(game.Name))
                {
                    Games.Add(game);
                }
            }

            LoadCustomIcons();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}