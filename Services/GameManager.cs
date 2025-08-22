using N64RecompLauncher.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;

namespace N64RecompLauncher.Services
{
    public class GameManager : INotifyPropertyChanged
    {
        private readonly HttpClient _httpClient;
        private readonly string _gamesFolder;
        private readonly string _cacheFolder;

        public ObservableCollection<GameInfo> Games { get; }
        public HttpClient HttpClient => _httpClient;
        public string GamesFolder => _gamesFolder;
        public string CacheFolder => _cacheFolder;

        private string _currentVersionString;
        public string currentVersionString
        {
            get => _currentVersionString;
            set
            {
                _currentVersionString = value;
                OnPropertyChanged(nameof(currentVersionString));
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
                },
                new GameInfo
                {
                    Name = "Goemon 64 Recomp",
                    Repository = "klorfmorf/Goemon64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Goemon64Recomp",
                },
                new GameInfo
                {
                    Name = "Mario Kart 64 Recomp",
                    Repository = "sonicdcer/MarioKart64Recomp",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "MarioKart64Recomp",
                },
                new GameInfo
                {
                    Name = "Dinosaur Planet Recomp",
                    Repository = "Francessco121/dino-recomp",
                    Branch = "main",
                    ImageRes = "64",
                    FolderName = "DinosaurPlanetRecomp",
                },
            };

            LoadCustomIcons();
        }

        private void LoadVersionString()
        {
            string versionFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
            if (File.Exists(versionFilePath))
            {
                currentVersionString = File.ReadAllText(versionFilePath).Trim();
            }
            else
            {
                currentVersionString = "Version information not found";
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
            foreach (var game in Games)
            {
                await game.CheckStatusAsync(_httpClient, _gamesFolder);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}