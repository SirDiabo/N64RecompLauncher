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

        public ObservableCollection<GameInfo> Games { get; }
        public HttpClient HttpClient => _httpClient;
        public string GamesFolder => _gamesFolder;
        public string CacheFolder => _cacheFolder;

        private string _currentVersionString;
        public string CurrentVersionString
        {
            get => _currentVersionString;
            set
            {
                if (_currentVersionString != value)
                {
                    _currentVersionString = value;
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

            Games = new ObservableCollection<GameInfo>
            {
                new GameInfo
                {
                    Name = "Zelda 64 Recomp",
                    Repository = "Zelda64Recomp/Zelda64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Zelda64Recomp",
                    GameManager = this,
                },
                new GameInfo
                {
                    Name = "Goemon 64 Recomp",
                    Repository = "klorfmorf/Goemon64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Goemon64Recomp",
                    GameManager = this,
                },
                new GameInfo
                {
                    Name = "Mario Kart 64 Recomp",
                    Repository = "sonicdcer/MarioKart64Recomp",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "MarioKart64Recomp",
                    GameManager = this,
                },
                new GameInfo
                {
                    Name = "Dinosaur Planet Recomp",
                    Repository = "Francessco121/dino-recomp",
                    Branch = "main",
                    ImageRes = "64",
                    FolderName = "DinosaurPlanetRecomp",
                    GameManager = this,
                },
            };

            LoadCustomIcons();
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


        private void LoadCustomIcons()
        {
            foreach (var game in Games)
            {
                game.LoadCustomIcon(_cacheFolder);
            }
        }

        public GameInfo FindGameByName(string name)
        {
            return Games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public GameInfo FindGameByFolderName(string folderName)
        {
            return Games.FirstOrDefault(g => g.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task LoadGamesAsync()
        {
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

        public event PropertyChangedEventHandler PropertyChanged;
    }
}